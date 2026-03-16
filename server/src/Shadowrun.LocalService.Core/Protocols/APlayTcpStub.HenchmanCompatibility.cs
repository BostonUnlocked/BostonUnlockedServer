using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.Definitions;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using SRO.Core.Compatibility.Utilities;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private const int HenchmanCollectionHistoryLength = 6;

        private sealed class HenchmanCollectionCacheEntry
        {
            public int CreationIndex;
            public string Serialized;
            public List<PlayerCharacterSnapshot> Snapshots;
        }

        private static readonly LinkedList<HenchmanCollectionCacheEntry> HenchmanCollectionHistory = new LinkedList<HenchmanCollectionCacheEntry>();
        private static DateTime CachedDefaultHenchmanSnapshotsLastWriteUtc;
        private static List<PlayerCharacterSnapshot> CachedDefaultHenchmanSnapshots;
        private static int NextHenchmanCollectionCreationIndex = 1;

        private string SerializeDefaultHenchmanCollection(string activeIdentityHash, int activeCareerIndex)
        {
            lock (HenchmanCollectionCacheLock)
            {
                try
                {
                    var defaultSnapshots = GetDefaultHenchmanSnapshotsNoLock();
                    var collectionSnapshots = new List<PlayerCharacterSnapshot>();
                    if (defaultSnapshots != null && defaultSnapshots.Count > 0)
                    {
                        for (var i = 0; i < defaultSnapshots.Count; i++)
                        {
                            var src = defaultSnapshots[i];
                            if (src == null)
                            {
                                continue;
                            }

                            var clone = src.Copy() as PlayerCharacterSnapshot;
                            if (clone == null)
                            {
                                continue;
                            }

                            clone.IsHenchman = true;
                            clone.PlayerId = 0UL;
                            clone.WantsBackgroundChange = false;
                            EnsureHenchmanSnapshotHasValidLoadout(clone);
                            collectionSnapshots.Add(clone);
                        }
                    }

                    var playerDerivedSnapshots = BuildPlayerDerivedHenchmanSnapshots(activeIdentityHash, activeCareerIndex, 2);
                    if (playerDerivedSnapshots != null && playerDerivedSnapshots.Count > 0)
                    {
                        for (var i = 0; i < playerDerivedSnapshots.Count; i++)
                        {
                            var snapshot = playerDerivedSnapshots[i];
                            if (snapshot == null || ContainsDuplicateHenchmanSnapshot(collectionSnapshots, snapshot))
                            {
                                continue;
                            }

                            collectionSnapshots.Add(snapshot);
                        }
                    }

                    if (collectionSnapshots.Count > 0)
                    {
                        collectionSnapshots.Sort(
                            delegate(PlayerCharacterSnapshot a, PlayerCharacterSnapshot b)
                            {
                                var an = a != null ? a.CharacterName : null;
                                var bn = b != null ? b.CharacterName : null;
                                return string.CompareOrdinal(an ?? string.Empty, bn ?? string.Empty);
                            });

                        return RegisterHenchmanCollectionNoLock(collectionSnapshots);
                    }
                }
                catch
                {
                }

                return RegisterHenchmanCollectionNoLock(BuildFallbackHenchmanSnapshots());
            }
        }

        private static List<PlayerCharacterSnapshot> GetDefaultHenchmanSnapshotsNoLock()
        {
            try
            {
                var staticDataPath = TryFindMetagameplayStaticDataPath();
                if (!IsNullOrWhiteSpace(staticDataPath) && File.Exists(staticDataPath))
                {
                    var lastWriteUtc = File.GetLastWriteTimeUtc(staticDataPath);
                    if (CachedDefaultHenchmanSnapshots != null && CachedDefaultHenchmanSnapshots.Count > 0 && lastWriteUtc == CachedDefaultHenchmanSnapshotsLastWriteUtc)
                    {
                        return CachedDefaultHenchmanSnapshots;
                    }

                    var snapshots = TryLoadDefaultHenchmanSnapshotsFromMetagameplay(staticDataPath);
                    if (snapshots != null && snapshots.Count > 0)
                    {
                        CachedDefaultHenchmanSnapshots = snapshots;
                        CachedDefaultHenchmanSnapshotsLastWriteUtc = lastWriteUtc;
                        return CachedDefaultHenchmanSnapshots;
                    }
                }
            }
            catch
            {
            }

            if (CachedDefaultHenchmanSnapshots == null || CachedDefaultHenchmanSnapshots.Count == 0)
            {
                CachedDefaultHenchmanSnapshots = BuildFallbackHenchmanSnapshots();
                CachedDefaultHenchmanSnapshotsLastWriteUtc = DateTime.MinValue;
            }

            return CachedDefaultHenchmanSnapshots;
        }

        private string RegisterHenchmanCollectionNoLock(List<PlayerCharacterSnapshot> snapshots)
        {
            if (snapshots == null || snapshots.Count == 0)
            {
                snapshots = BuildFallbackHenchmanSnapshots();
            }

            const string ownerCharacterIdentifier = "DEFAULT";
            var henches = new List<HenchmanRepositoryPlayerCharacterSnapshot>();
            for (var i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                if (snapshot == null)
                {
                    continue;
                }

                snapshot.IsHenchman = true;
                snapshot.DataVersion = snapshot.DataVersion != 0 ? snapshot.DataVersion : 48;
                snapshot.PlayerId = 0UL;
                snapshot.WantsBackgroundChange = false;
                EnsureHenchmanSnapshotHasValidLoadout(snapshot);

                var entry = new HenchmanRepositoryPlayerCharacterSnapshot(ownerCharacterIdentifier);
                entry.IsDefaultHench = !IsPlayerDerivedHenchmanIdentifier(snapshot.CharacterIdentifier);
                entry.PlayerCharacterSnapshot = snapshot;
                henches.Add(entry);
            }

            var creationIndex = NextHenchmanCollectionCreationIndex++;
            if (creationIndex <= 0)
            {
                NextHenchmanCollectionCreationIndex = 2;
                creationIndex = 1;
            }

            var collection = new HenchmanCollection
            {
                CreationIndex = creationIndex,
                Data = henches.ToArray(),
            };

            var serialized = HenchRepoSerializer.SerializeHenchmanCollection(collection);
            var historyEntry = new HenchmanCollectionCacheEntry
            {
                CreationIndex = creationIndex,
                Serialized = serialized,
                Snapshots = snapshots,
            };

            HenchmanCollectionHistory.AddFirst(historyEntry);
            while (HenchmanCollectionHistory.Count > HenchmanCollectionHistoryLength)
            {
                HenchmanCollectionHistory.RemoveLast();
            }

            CachedSerializedHenchmanCollection = serialized;
            CachedHenchmanCollectionCreationIndex = creationIndex;
            CachedHenchmanCollectionSnapshots = snapshots;

            return serialized;
        }

        private List<PlayerCharacterSnapshot> BuildPlayerDerivedHenchmanSnapshots(string activeIdentityHash, int activeCareerIndex, int targetCount)
        {
            if (_userStore == null || targetCount <= 0)
            {
                return null;
            }

            try
            {
                var candidates = _userStore.GetRandomOccupiedCareerReferences(activeIdentityHash, activeCareerIndex);
                if (candidates == null || candidates.Count == 0)
                {
                    return null;
                }

                var results = new List<PlayerCharacterSnapshot>();
                for (var i = 0; i < candidates.Count && results.Count < targetCount; i++)
                {
                    var candidate = candidates[i];
                    if (candidate == null || candidate.Slot == null || IsNullOrWhiteSpace(candidate.IdentityHash))
                    {
                        continue;
                    }

                    var characterIdentifier = !IsNullOrWhiteSpace(candidate.Slot.CharacterIdentifier)
                        ? candidate.Slot.CharacterIdentifier
                        : (candidate.IdentityHash + ":" + candidate.CareerIndex.ToString(CultureInfo.InvariantCulture));
                    var snapshot = BuildPlayerCharacterSnapshotForSlot(characterIdentifier, candidate.Slot.CharacterName, candidate.Slot);
                    if (snapshot == null || snapshot.SkillTreeDefinitions == null || snapshot.SkillTreeDefinitions.Count == 0)
                    {
                        continue;
                    }

                    snapshot.IsHenchman = true;
                    snapshot.PlayerId = 0UL;
                    snapshot.WantsBackgroundChange = false;
                    snapshot.CharacterIdentifier = BuildPlayerDerivedHenchmanIdentifier(candidate.IdentityHash, candidate.CareerIndex);
                    EnsureHenchmanSnapshotHasValidLoadout(snapshot);

                    if (ContainsDuplicateHenchmanSnapshot(results, snapshot))
                    {
                        continue;
                    }

                    results.Add(snapshot);
                }

                return results;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildPlayerDerivedHenchmanIdentifier(string identityHash, int careerIndex)
        {
            var normalizedIdentity = !IsNullOrWhiteSpace(identityHash)
                ? identityHash.Replace("-", string.Empty)
                : Guid.Empty.ToString("N");
            if (normalizedIdentity.Length > 12)
            {
                normalizedIdentity = normalizedIdentity.Substring(0, 12);
            }

            return "00000000-0000-0000-0000-000000000000:PLAYERHENCH_"
                + normalizedIdentity
                + "_"
                + careerIndex.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsPlayerDerivedHenchmanIdentifier(string characterIdentifier)
        {
            return !IsNullOrWhiteSpace(characterIdentifier)
                && characterIdentifier.IndexOf(":PLAYERHENCH_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsDuplicateHenchmanSnapshot(IEnumerable<PlayerCharacterSnapshot> snapshots, PlayerCharacterSnapshot candidate)
        {
            if (snapshots == null || candidate == null)
            {
                return false;
            }

            foreach (var snapshot in snapshots)
            {
                if (snapshot == null)
                {
                    continue;
                }

                if (!IsNullOrWhiteSpace(snapshot.CharacterIdentifier)
                    && !IsNullOrWhiteSpace(candidate.CharacterIdentifier)
                    && string.Equals(snapshot.CharacterIdentifier, candidate.CharacterIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!IsNullOrWhiteSpace(snapshot.CharacterName)
                    && !IsNullOrWhiteSpace(candidate.CharacterName)
                    && string.Equals(snapshot.CharacterName, candidate.CharacterName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<PlayerCharacterSnapshot> GetSnapshotsForSelectionCollection(IList<ParsedHenchmanSelection> parsedSelections)
        {
            if (parsedSelections == null || parsedSelections.Count == 0)
            {
                return null;
            }

            var creationIndex = parsedSelections[0].CollectionCreationIndex;
            lock (HenchmanCollectionCacheLock)
            {
                foreach (var entry in HenchmanCollectionHistory)
                {
                    if (entry != null && entry.CreationIndex == creationIndex && entry.Snapshots != null && entry.Snapshots.Count > 0)
                    {
                        return entry.Snapshots;
                    }
                }

                return CachedHenchmanCollectionSnapshots;
            }
        }

        private static string TryFindMetagameplayStaticDataPath()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (IsNullOrWhiteSpace(baseDir))
                {
                    return null;
                }

                var portable = Path.Combine(Path.Combine(Path.Combine(baseDir, "Resources"), "static-data"), "metagameplay.json");
                if (File.Exists(portable))
                {
                    return portable;
                }

                var dir = new DirectoryInfo(baseDir);
                for (var i = 0; i < 8 && dir != null; i++)
                {
                    var candidate = Path.Combine(Path.Combine(dir.FullName, "static-data"), "metagameplay.json");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    dir = dir.Parent;
                }
            }
            catch
            {
            }

            return null;
        }

        private static List<PlayerCharacterSnapshot> TryLoadDefaultHenchmanSnapshotsFromMetagameplay(string metagameplayJsonPath)
        {
            if (IsNullOrWhiteSpace(metagameplayJsonPath) || !File.Exists(metagameplayJsonPath))
            {
                return null;
            }

            string json;
            try
            {
                json = File.ReadAllText(metagameplayJsonPath);
            }
            catch
            {
                return null;
            }

            if (IsNullOrWhiteSpace(json))
            {
                return null;
            }

            const string marker = "\"TypeName\": \"Cliffhanger.SRO.ServerClientCommons.Metagameplay.PlayerCharacterSnapshot, Cliffhanger.SRO.ServerClientCommons\"";

            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 256;

            var snapshots = new List<PlayerCharacterSnapshot>();
            var index = 0;
            while (true)
            {
                var markerIndex = json.IndexOf(marker, index, StringComparison.Ordinal);
                if (markerIndex < 0)
                {
                    break;
                }

                var objStart = json.LastIndexOf('{', markerIndex);
                if (objStart < 0)
                {
                    break;
                }

                var objEnd = FindMatchingBrace(json, objStart);
                if (objEnd <= objStart)
                {
                    break;
                }

                var objJson = json.Substring(objStart, (objEnd - objStart) + 1);
                try
                {
                    var obj = serializer.DeserializeObject(objJson) as IDictionary;
                    if (obj == null)
                    {
                        continue;
                    }

                    var isHenchman = false;
                    try
                    {
                        var raw = obj.Contains("IsHenchman") ? obj["IsHenchman"] : null;
                        if (raw is bool)
                        {
                            isHenchman = (bool)raw;
                        }
                        else if (raw != null)
                        {
                            isHenchman = Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
                        }
                    }
                    catch
                    {
                        isHenchman = false;
                    }

                    if (!isHenchman)
                    {
                        continue;
                    }

                    var snapshot = new PlayerCharacterSnapshot();
                    snapshot.DataVersion = GetInt32Value(obj, "DataVersion", 48);
                    snapshot.IsHenchman = true;
                    snapshot.CharacterIdentifier = GetStringValue(obj, "CharacterIdentifier");
                    snapshot.CharacterName = GetStringValue(obj, "CharacterName");
                    snapshot.Voiceset = GetStringValue(obj, "Voiceset");
                    snapshot.PortraitPath = GetStringValue(obj, "PortraitPath");
                    snapshot.Bodytype = GetUInt64Value(obj, "Bodytype", PlayerCharacterDefaultValues.Bodytype);
                    snapshot.SkinTextureIndex = GetInt32Value(obj, "SkinTextureIndex", PlayerCharacterDefaultValues.SkinTextureIndex);
                    snapshot.BackgroundStory = GetUInt64Value(obj, "BackgroundStory", PlayerCharacterDefaultValues.BackgroundStory);
                    snapshot.PlayerId = 0UL;
                    snapshot.WantsBackgroundChange = false;
                    snapshot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);

                    var invObj = obj.Contains("PlayerCharacterInventory") ? (obj["PlayerCharacterInventory"] as IDictionary) : null;
                    var primaryItemId = TryGetNestedItemId(invObj, "PrimaryWeapon");
                    var armorItemId = TryGetNestedItemId(invObj, "Armor");
                    if (!IsNullOrWhiteSpace(primaryItemId))
                    {
                        if (snapshot.PlayerCharacterInventory == null)
                        {
                            snapshot.PlayerCharacterInventory = new PlayerCharacterInventory();
                        }
                        snapshot.PlayerCharacterInventory.PrimaryWeapon = CreateInventoryItem(primaryItemId, 0);
                    }
                    if (!IsNullOrWhiteSpace(armorItemId))
                    {
                        if (snapshot.PlayerCharacterInventory == null)
                        {
                            snapshot.PlayerCharacterInventory = new PlayerCharacterInventory();
                        }
                        snapshot.PlayerCharacterInventory.Armor = CreateInventoryItem(armorItemId, 2);
                    }

                    var equipped = TryReadEquippedItems(invObj);
                    if (equipped != null && equipped.Count > 0)
                    {
                        if (snapshot.PlayerCharacterInventory == null)
                        {
                            snapshot.PlayerCharacterInventory = new PlayerCharacterInventory();
                        }
                        snapshot.PlayerCharacterInventory.EquippedItems = equipped;
                    }

                    EnsureHenchmanSnapshotHasValidLoadout(snapshot);
                    snapshots.Add(snapshot);
                }
                catch
                {
                }

                index = objEnd + 1;
            }

            if (snapshots.Count == 0)
            {
                return null;
            }

            snapshots.Sort(
                delegate(PlayerCharacterSnapshot a, PlayerCharacterSnapshot b)
                {
                    var an = a != null ? a.CharacterName : null;
                    var bn = b != null ? b.CharacterName : null;
                    return string.CompareOrdinal(an ?? string.Empty, bn ?? string.Empty);
                });

            for (var i = 0; i < snapshots.Count; i++)
            {
                var snap = snapshots[i];
                if (snap == null)
                {
                    continue;
                }

                if (IsNullOrWhiteSpace(snap.CharacterIdentifier))
                {
                    snap.CharacterIdentifier = "00000000-0000-0000-0000-000000000000:" + (100 + i).ToString(CultureInfo.InvariantCulture);
                }

                EnsureHenchmanSnapshotHasValidLoadout(snap);
            }

            return snapshots;
        }

        private static List<ItemSlot> TryReadEquippedItems(IDictionary inventoryObj)
        {
            try
            {
                if (inventoryObj == null || !inventoryObj.Contains("EquippedItems") || inventoryObj["EquippedItems"] == null)
                {
                    return null;
                }

                IEnumerable rawList = null;
                var asArray = inventoryObj["EquippedItems"] as object[];
                if (asArray != null)
                {
                    rawList = asArray;
                }
                else
                {
                    rawList = inventoryObj["EquippedItems"] as IEnumerable;
                }

                if (rawList == null)
                {
                    return null;
                }

                var results = new List<ItemSlot>();
                var nextKey = 1000;

                foreach (var entryObj in rawList)
                {
                    var entry = entryObj as IDictionary;
                    if (entry == null)
                    {
                        continue;
                    }

                    var defObj = entry.Contains("Definition") ? (entry["Definition"] as IDictionary) : null;
                    var itemObj = entry.Contains("Item") ? (entry["Item"] as IDictionary) : null;
                    if (defObj == null || itemObj == null)
                    {
                        continue;
                    }

                    var slotId = GetUInt64Value(defObj, "Id", 0UL);
                    var itemId = GetStringValue(itemObj, "ItemId");
                    if (slotId == 0UL || IsNullOrWhiteSpace(itemId))
                    {
                        continue;
                    }

                    if (itemId.StartsWith("Item_Empty", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var assignable = TryReadAssignableItemTypes(defObj);
                    var def = new LogicItemslotDefinition
                    {
                        Id = slotId,
                        AssignableItemTypes = assignable ?? new ulong[0],
                        CannotBeEmpty = false,
                        DefaultItem = null,
                    };

                    results.Add(new ItemSlot(def)
                    {
                        Item = CreateInventoryItem(itemId, nextKey++),
                    });
                }

                return results.Count > 0 ? results : null;
            }
            catch
            {
                return null;
            }
        }

        private static ulong[] TryReadAssignableItemTypes(IDictionary defObj)
        {
            try
            {
                if (defObj == null || !defObj.Contains("AssignableItemTypes") || defObj["AssignableItemTypes"] == null)
                {
                    return null;
                }

                var raw = defObj["AssignableItemTypes"] as IEnumerable;
                if (raw == null)
                {
                    return null;
                }

                var list = new List<ulong>();
                foreach (var v in raw)
                {
                    if (v == null)
                    {
                        continue;
                    }

                    try
                    {
                        if (v is ulong)
                        {
                            list.Add((ulong)v);
                        }
                        else if (v is long)
                        {
                            list.Add(unchecked((ulong)(long)v));
                        }
                        else if (v is int)
                        {
                            list.Add(unchecked((ulong)(int)v));
                        }
                        else
                        {
                            list.Add(Convert.ToUInt64(v, CultureInfo.InvariantCulture));
                        }
                    }
                    catch
                    {
                    }
                }

                return list.Count > 0 ? list.ToArray() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string TryGetNestedItemId(IDictionary inventoryObj, string slotKey)
        {
            try
            {
                if (inventoryObj == null || IsNullOrWhiteSpace(slotKey) || !inventoryObj.Contains(slotKey) || inventoryObj[slotKey] == null)
                {
                    return null;
                }
                var slotObj = inventoryObj[slotKey] as IDictionary;
                if (slotObj == null || !slotObj.Contains("ItemId") || slotObj["ItemId"] == null)
                {
                    return null;
                }
                return slotObj["ItemId"] as string;
            }
            catch
            {
                return null;
            }
        }

        private static int FindMatchingBrace(string json, int startIndex)
        {
            if (json == null || startIndex < 0 || startIndex >= json.Length || json[startIndex] != '{')
            {
                return -1;
            }

            var depth = 0;
            var inString = false;
            var isEscaped = false;

            for (var i = startIndex; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                        continue;
                    }

                    if (c == '\\')
                    {
                        isEscaped = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                    continue;
                }

                if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static void EnsureHenchmanSnapshotHasValidLoadout(PlayerCharacterSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (snapshot.PlayerCharacterInventory == null)
            {
                snapshot.PlayerCharacterInventory = new PlayerCharacterInventory();
            }

            if (Item.IsNullOrEmpty(snapshot.PlayerCharacterInventory.PrimaryWeapon))
            {
                snapshot.PlayerCharacterInventory.PrimaryWeapon = CreateInventoryItem(PlayerCharacterDefaultValues.PrimaryWeapon, 0);
            }

            snapshot.PlayerCharacterInventory.SecondaryWeapon = Item.Empty;

            if (Item.IsNullOrEmptyArmor(snapshot.PlayerCharacterInventory.Armor))
            {
                snapshot.PlayerCharacterInventory.Armor = CreateInventoryItem(PlayerCharacterDefaultValues.Armor, 2);
            }
        }

        private static List<PlayerCharacterSnapshot> BuildFallbackHenchmanSnapshots()
        {
            var snapshots = new List<PlayerCharacterSnapshot>();
            for (var i = 0; i < 8; i++)
            {
                var extension = (100 + i).ToString(CultureInfo.InvariantCulture);
                var snapshot = new PlayerCharacterSnapshot();
                snapshot.DataVersion = 48;
                snapshot.PlayerId = 0UL;
                snapshot.IsHenchman = true;
                snapshot.CharacterIdentifier = "00000000-0000-0000-0000-000000000000:" + extension;
                snapshot.CharacterName = "Henchman " + (i + 1).ToString(CultureInfo.InvariantCulture);
                snapshot.PortraitPath = (i % 2 == 0)
                    ? "GUI/Textures/Metagameplay/player_portraits/portrait_male_troll_shaman_"
                    : "GUI/Textures/Metagameplay/player_portraits/portrait_male_elf_jellyfish_kelly_";
                snapshot.Voiceset = PlayerCharacterDefaultValues.Voiceset;
                snapshot.Bodytype = (i % 2 == 0) ? 196716UL : 196714UL;
                snapshot.SkinTextureIndex = (i % 3) + 1;
                snapshot.BackgroundStory = PlayerCharacterDefaultValues.BackgroundStory;
                snapshot.WantsBackgroundChange = false;

                if (snapshot.Wallet != null)
                {
                    snapshot.Wallet.Reset(CurrencyId.Karma, 0, 0);
                    snapshot.Wallet.Reset(CurrencyId.Nuyen, 0, 0);
                }

                if (snapshot.PlayerCharacterInventory != null)
                {
                    snapshot.PlayerCharacterInventory.PrimaryWeapon = CreateInventoryItem(PlayerCharacterDefaultValues.PrimaryWeapon, 0);
                    snapshot.PlayerCharacterInventory.SecondaryWeapon = Item.Empty;
                    snapshot.PlayerCharacterInventory.Armor = CreateInventoryItem(PlayerCharacterDefaultValues.Armor, 2);
                }

                snapshots.Add(snapshot);
            }

            return snapshots;
        }

        private static string SerializeInventoryFromSlot(CareerSlot slot)
        {
            try
            {
                var inventory = new Inventory();
                if (slot != null && slot.ItemPossessions != null && slot.ItemPossessions.Count > 0)
                {
                    var items = new List<Item>();
                    var keys = new List<string>(slot.ItemPossessions.Keys);
                    keys.Sort(StringComparer.OrdinalIgnoreCase);
                    var nextKey = 0;
                    for (var i = 0; i < keys.Count; i++)
                    {
                        var packed = keys[i];
                        int amount;
                        if (IsNullOrWhiteSpace(packed) || !slot.ItemPossessions.TryGetValue(packed, out amount) || amount <= 0)
                        {
                            continue;
                        }

                        var itemId = packed;
                        var quality = 0;
                        var flavour = -1;
                        try
                        {
                            var parts = packed.Split('|');
                            if (parts != null && parts.Length >= 1)
                            {
                                itemId = parts[0];
                            }
                            if (parts != null && parts.Length >= 2)
                            {
                                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out quality);
                            }
                            if (parts != null && parts.Length >= 3)
                            {
                                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out flavour);
                            }
                        }
                        catch
                        {
                            itemId = packed;
                            quality = 0;
                            flavour = -1;
                        }

                        if (IsNullOrWhiteSpace(itemId))
                        {
                            continue;
                        }

                        var item = new Item();
                        item.InventoryKey = nextKey++;
                        item.ItemId = itemId;
                        item.Amount = amount;
                        item.Quality = quality;
                        item.FlavourIndex = flavour;
                        items.Add(item);
                    }

                    if (items.Count > 0)
                    {
                        inventory.AddRangeWithValidInventoryKey(items);
                    }
                }

                return InventorySerializer.SerializeInventory(inventory);
            }
            catch
            {
                try
                {
                    return InventorySerializer.SerializeInventory(new Inventory());
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        private static string SerializeWalletForSlot(CareerSlot slot)
        {
            var wallet = new Wallet();
            try
            {
                wallet.Reset(CurrencyId.Karma, slot != null ? slot.Karma : 0, slot != null ? slot.SpentKarma : 0);
                wallet.Reset(CurrencyId.Nuyen, slot != null ? slot.Nuyen : 0, 0);
            }
            catch
            {
                wallet.Reset(CurrencyId.Karma, 0, 0);
                wallet.Reset(CurrencyId.Nuyen, 0, 0);
            }
            return JsonFxSerializerProvider.Current.Serialize<Wallet>(wallet);
        }

        private static bool TryInferSkillLevelFromTechnicalName(string skillTechnicalName, out int skillLevel)
        {
            skillLevel = 0;
            try
            {
                if (IsNullOrWhiteSpace(skillTechnicalName))
                {
                    return false;
                }

                var token = "LevelSkill_";
                var idx = skillTechnicalName.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    idx += token.Length;
                    var start = idx;
                    while (idx < skillTechnicalName.Length && char.IsDigit(skillTechnicalName[idx]))
                    {
                        idx++;
                    }
                    if (idx > start)
                    {
                        int parsed;
                        if (int.TryParse(skillTechnicalName.Substring(start, idx - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                        {
                            skillLevel = parsed;
                            return true;
                        }
                    }
                }

                for (var i = 0; i < skillTechnicalName.Length; i++)
                {
                    if (skillTechnicalName[i] != '_')
                    {
                        continue;
                    }

                    var j = i + 1;
                    if (j >= skillTechnicalName.Length || !char.IsDigit(skillTechnicalName[j]))
                    {
                        continue;
                    }

                    var start = j;
                    while (j < skillTechnicalName.Length && char.IsDigit(skillTechnicalName[j]))
                    {
                        j++;
                    }

                    if (j < skillTechnicalName.Length && skillTechnicalName[j] == '_')
                    {
                        int parsed;
                        if (int.TryParse(skillTechnicalName.Substring(start, j - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                        {
                            skillLevel = parsed;
                            return true;
                        }
                    }

                    i = j;
                }

                return false;
            }
            catch
            {
                skillLevel = 0;
                return false;
            }
        }

        private struct ParsedHenchmanSelection
        {
            public int CollectionCreationIndex;
            public int HenchmanId;
        }

        private static List<ParsedHenchmanSelection> TryExtractHenchmanSelections(string rawMessage)
        {
            if (IsNullOrWhiteSpace(rawMessage))
            {
                return null;
            }

            var keyPattern = "\"HenchmanSelection\"";
            var idx = rawMessage.IndexOf(keyPattern, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }

            idx = rawMessage.IndexOf(':', idx);
            if (idx < 0)
            {
                return null;
            }

            idx++;
            while (idx < rawMessage.Length && char.IsWhiteSpace(rawMessage[idx]))
            {
                idx++;
            }
            if (idx >= rawMessage.Length)
            {
                return null;
            }

            if (rawMessage[idx] == 'n')
            {
                return null;
            }

            if (rawMessage[idx] != '[')
            {
                return null;
            }

            var arrEnd = FindMatchingBracket(rawMessage, idx);
            if (arrEnd <= idx)
            {
                return null;
            }

            var arrayJson = rawMessage.Substring(idx, (arrEnd - idx) + 1);
            var results = new List<ParsedHenchmanSelection>();

            var cursor = 0;
            while (cursor < arrayJson.Length)
            {
                var objStart = arrayJson.IndexOf('{', cursor);
                if (objStart < 0)
                {
                    break;
                }
                var objEnd = FindMatchingBrace(arrayJson, objStart);
                if (objEnd <= objStart)
                {
                    break;
                }

                var objJson = arrayJson.Substring(objStart, (objEnd - objStart) + 1);
                var rawCreation = ExtractJsonStringValue(objJson, "HenchmanCollectionCreationIndex");
                var rawId = ExtractJsonStringValue(objJson, "HenchmanId");

                int creationIndex;
                int henchId;
                if (TryParseInt32(rawCreation, out creationIndex) && TryParseInt32(rawId, out henchId))
                {
                    results.Add(new ParsedHenchmanSelection { CollectionCreationIndex = creationIndex, HenchmanId = henchId });
                }

                cursor = objEnd + 1;
            }

            return results.Count > 0 ? results : null;
        }

        private static List<ParsedHenchmanSelection> TryExtractCoopPayloadHenchmanSelections(string payload)
        {
            if (IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            var trimmed = payload.Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (trimmed[0] == '[')
            {
                return TryExtractHenchmanSelections("{\"HenchmanSelection\":" + trimmed + "}");
            }

            return TryExtractHenchmanSelections(trimmed);
        }

        private static int FindMatchingBracket(string json, int startIndex)
        {
            if (json == null || startIndex < 0 || startIndex >= json.Length || json[startIndex] != '[')
            {
                return -1;
            }

            var depth = 0;
            var inString = false;
            var isEscaped = false;

            for (var i = startIndex; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                        continue;
                    }

                    if (c == '\\')
                    {
                        isEscaped = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '[')
                {
                    depth++;
                    continue;
                }

                if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static PlayerCharacterSnapshot CloneHenchSnapshotForMission(PlayerCharacterSnapshot src, Guid ownerAccountGuid, int slotIndex, int ownerKarma, int ownerSpentKarma, int ownerNuyen)
        {
            if (src == null)
            {
                return null;
            }

            var clone = new PlayerCharacterSnapshot();
            clone.DataVersion = src.DataVersion != 0 ? src.DataVersion : 48;
            clone.IsHenchman = true;
            clone.PlayerId = 0UL;
            clone.CharacterName = src.CharacterName;
            clone.Voiceset = src.Voiceset;
            clone.PortraitPath = src.PortraitPath;
            clone.Bodytype = src.Bodytype;
            clone.SkinTextureIndex = src.SkinTextureIndex;
            clone.BackgroundStory = src.BackgroundStory;
            clone.WantsBackgroundChange = false;

            var extension = SafeGetIdentifierExtension(src);
            if (IsNullOrWhiteSpace(extension))
            {
                extension = "HENCH" + slotIndex.ToString(CultureInfo.InvariantCulture);
            }
            clone.CharacterIdentifier = ownerAccountGuid != Guid.Empty
                ? (ownerAccountGuid.ToString() + ":" + extension)
                : ("00000000-0000-0000-0000-000000000000:" + extension);

            clone.SkillTreeDefinitions = src.SkillTreeDefinitions != null
                ? new Dictionary<string, string[]>(src.SkillTreeDefinitions, StringComparer.Ordinal)
                : new Dictionary<string, string[]>(StringComparer.Ordinal);

            clone.PlayerCharacterInventory = new PlayerCharacterInventory();
            if (src.PlayerCharacterInventory != null)
            {
                clone.PlayerCharacterInventory.PrimaryWeapon = src.PlayerCharacterInventory.PrimaryWeapon;
                clone.PlayerCharacterInventory.Armor = src.PlayerCharacterInventory.Armor;
            }
            EnsureHenchmanSnapshotHasValidLoadout(clone);

            try
            {
                if (src.PlayerCharacterInventory != null
                    && src.PlayerCharacterInventory.EquippedItems != null
                    && src.PlayerCharacterInventory.EquippedItems.Count > 0
                    && clone.PlayerCharacterInventory != null)
                {
                    for (var i = 0; i < src.PlayerCharacterInventory.EquippedItems.Count; i++)
                    {
                        var srcSlot = src.PlayerCharacterInventory.EquippedItems[i];
                        if (srcSlot == null || srcSlot.Definition == null || srcSlot.Item == null || IsNullOrWhiteSpace(srcSlot.Item.ItemId))
                        {
                            continue;
                        }

                        var def = new LogicItemslotDefinition();
                        def.Id = srcSlot.Definition.Id;
                        def.AssignableItemTypes = srcSlot.Definition.AssignableItemTypes ?? new ulong[0];
                        def.CannotBeEmpty = srcSlot.Definition.CannotBeEmpty;
                        def.DefaultItem = srcSlot.Definition.DefaultItem;

                        var dstSlot = new ItemSlot(def);
                        var item = new Item();
                        item.ItemId = srcSlot.Item.ItemId;
                        item.InventoryKey = srcSlot.Item.InventoryKey;
                        item.Amount = srcSlot.Item.Amount;
                        item.FlavourIndex = srcSlot.Item.FlavourIndex;
                        item.Quality = srcSlot.Item.Quality;
                        dstSlot.Item = item;

                        clone.PlayerCharacterInventory.EquippedItems.Add(dstSlot);
                    }
                }
            }
            catch
            {
            }

            clone.Wallet = new Wallet();
            clone.Wallet.Reset(CurrencyId.Karma, ownerKarma, ownerSpentKarma);
            clone.Wallet.Reset(CurrencyId.Nuyen, ownerNuyen, 0);

            return clone;
        }

        private static Item CreateInventoryItem(string itemId, int inventoryKey)
        {
            var item = new Item();
            item.ItemId = itemId ?? string.Empty;
            item.InventoryKey = inventoryKey;
            item.Amount = 1;
            return item;
        }
    }
}
