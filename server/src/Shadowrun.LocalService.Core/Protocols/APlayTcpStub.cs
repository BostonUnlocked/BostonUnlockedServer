using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Web.Script.Serialization;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.Definitions;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Hub;
using SRO.Core.Compatibility.Math;
using SRO.Core.Compatibility.Utilities;
using Shadowrun.LocalService.Core.Simulation;
using Shadowrun.LocalService.Core.Career;
using Shadowrun.LocalService.Core.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private sealed class CoopMissionSessionState
        {
            public CoopMissionSessionState(string coopGroupName)
            {
                CoopGroupName = coopGroupName;
                SyncRoot = new object();
                CreatedUtc = DateTime.UtcNow;

                LootSnapshot = null;
                LootAppliedToParticipants = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }

            public readonly string CoopGroupName;
            public readonly object SyncRoot;
            public readonly DateTime CreatedUtc;

            public string MapName;
            public uint Seed0;
            public uint Seed1;
            public uint Seed2;
            public uint Seed3;
            public string CompressedMatchConfiguration;
            public ServerSimulationSession Simulation;

            // Coop loot is shared at the simulation level (one LocalMissionLootController), but rewards must be
            // applied per-player. If the first client to leave drains the loot, other clients would miss it.
            // Snapshot the drained loot once per coop run and apply to each participant (identity+career) once.
            public Shadowrun.LocalService.Core.Simulation.LocalMissionLootController.LootGrant[] LootSnapshot;
            public Dictionary<string, bool> LootAppliedToParticipants;
        }

        private const string DefaultHubId = "Act01_HUB_02";
        private const string FallbackSerializedHubState = "CwAAAEgAVQBCAF8AcwBjAGUAbgBlAF8AMQALAAAASABVAEIAXwBzAGMAZQBuAGUAXwAxAAA=";
        private static long _hubInstanceSequence;
        private static long _hubDuplicateSessionRetiredTotal;
        private static long _hubReadyFallbackTriggeredTotal;
        private static long _hubReadyFallbackSkippedTotal;
        private static readonly object HenchmanCollectionCacheLock = new object();
        private static string CachedSerializedHenchmanCollection;
        private static DateTime CachedSerializedHenchmanCollectionLastWriteUtc;
        private static int CachedHenchmanCollectionCreationIndex;
        private static List<PlayerCharacterSnapshot> CachedHenchmanCollectionSnapshots;

        private readonly object _coopMissionLock = new object();
        private readonly Dictionary<string, List<CoopMissionParticipant>> _coopMissionParticipants = new Dictionary<string, List<CoopMissionParticipant>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CoopMissionSessionState> _coopMissionSessions = new Dictionary<string, CoopMissionSessionState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<Guid, List<ParsedHenchmanSelection>>> _coopMissionHenchSelections = new Dictionary<string, Dictionary<Guid, List<ParsedHenchmanSelection>>>(StringComparer.OrdinalIgnoreCase);

        private static string SerializeDefaultHenchmanCollection()
        {
            lock (HenchmanCollectionCacheLock)
            {
                try
                {
                    var staticDataPath = TryFindMetagameplayStaticDataPath();
                    if (!IsNullOrWhiteSpace(staticDataPath) && File.Exists(staticDataPath))
                    {
                        var lastWriteUtc = File.GetLastWriteTimeUtc(staticDataPath);
                        if (CachedSerializedHenchmanCollection != null && lastWriteUtc == CachedSerializedHenchmanCollectionLastWriteUtc)
                        {
                            return CachedSerializedHenchmanCollection;
                        }

                        var henchSnapshots = TryLoadDefaultHenchmanSnapshotsFromMetagameplay(staticDataPath);
                        if (henchSnapshots != null && henchSnapshots.Count > 0)
                        {
                            var serialized = SerializeHenchmanCollectionFromSnapshots(henchSnapshots, lastWriteUtc);
                            CachedSerializedHenchmanCollection = serialized;
                            CachedSerializedHenchmanCollectionLastWriteUtc = lastWriteUtc;
                            CachedHenchmanCollectionSnapshots = henchSnapshots;
                            return serialized;
                        }
                    }
                }
                catch
                {
                }

                // If anything fails, fall back to a small built-in list so UI isn't empty.
                return SerializeFallbackHenchmanCollection();
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

                // Portable layout: <exeDir>/Resources/static-data/metagameplay.json
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

            // There are exactly 9 PlayerCharacterSnapshot entries in the static-data file, all of which are default henchmen.
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
                    // Avoid deserializing into PlayerCharacterSnapshot directly:
                    // the embedded inventory/equipped-items include types without trivial constructors,
                    // and JavaScriptSerializer can throw. We only need cosmetic fields; loadout is set server-side.
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

                    // Provide a stable (but minimal) skill-tree layout; progression will overwrite this when the client
                    // runs HenchmanProgressionCalculator.ModifyHenchFromReference().
                    snapshot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);

                    // Try to preserve each hench's intended reference weapon from static-data so progression
                    // resolves a matching skill tree (instead of all henches sharing the same default weapon).
                    // We still force SecondaryWeapon empty later to avoid the client indexing Weapons[1].
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

                    // Preserve the intended hench appearance (cosmetic equipment) from static-data.
                    // This is critical: the client renders visuals from PlayerCharacterInventory.EquippedItems.
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

            // Ensure deterministic ordering to keep indices stable across restarts.
            snapshots.Sort(
                delegate(PlayerCharacterSnapshot a, PlayerCharacterSnapshot b)
                {
                    var an = a != null ? a.CharacterName : null;
                    var bn = b != null ? b.CharacterName : null;
                    return string.CompareOrdinal(an ?? string.Empty, bn ?? string.Empty);
                });

            // Populate missing identifiers (static-data templates leave them empty).
            for (var i = 0; i < snapshots.Count; i++)
            {
                var snap = snapshots[i];
                if (snap == null)
                {
                    continue;
                }

                if (IsNullOrWhiteSpace(snap.CharacterIdentifier))
                {
                    // Keep the "GUID:index" pattern the client already uses elsewhere.
                    snap.CharacterIdentifier = "00000000-0000-0000-0000-000000000000:" + (100 + i).ToString(CultureInfo.InvariantCulture);
                }

                // Ensure loadout stays safe even if the earlier Ensure call was skipped for some reason.
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

                    // Skip explicit "empty" placeholder items; leaving the slot absent is safer and allows
                    // client-side fallbacks (e.g., default underwear).
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

        private static string SerializeHenchmanCollectionFromSnapshots(List<PlayerCharacterSnapshot> snapshots, DateTime lastWriteUtc)
        {
            // The client expects a compressed binary string produced by Cliffhanger.SRO.ServerClientCommons.HenchRepoSerializer.
            // Important subtlety: HenchmanCollectionController ignores updates when CreationIndex is unchanged.

            // Another client quirk: it filters OUT entries where OwnerCharacterIdentifier == local player's CharacterIdentifier.
            // Using a sentinel owner keeps the entries visible to the local client.
            const string ownerCharacterIdentifier = "DEFAULT";

            var henches = new List<HenchmanRepositoryPlayerCharacterSnapshot>();
            for (var i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                if (snapshot == null)
                {
                    continue;
                }

                // Defensive: ensure the required flags are present.
                snapshot.IsHenchman = true;
                snapshot.DataVersion = snapshot.DataVersion != 0 ? snapshot.DataVersion : 48;
                snapshot.PlayerId = 0UL;
                snapshot.WantsBackgroundChange = false;

                // Important: the client resolves weapon info by indexing into the *template's* SkillLoadoutComponent.Weapons.
                // It will always access index 0, and it accesses index 1 only when SecondaryWeapon is present.
                // Some hench templates (especially when derived from partial static-data snapshots) result in a template
                // with only one weapon, so we must avoid advertising a secondary weapon to prevent IndexOutOfRangeException.
                EnsureHenchmanSnapshotHasValidLoadout(snapshot);

                var entry = new HenchmanRepositoryPlayerCharacterSnapshot(ownerCharacterIdentifier);
                entry.IsDefaultHench = true;
                entry.PlayerCharacterSnapshot = snapshot;
                henches.Add(entry);
            }

            // HenchmanCollectionController ignores updates when CreationIndex is unchanged.
            // Using a per-process value makes iterative LocalService changes visible without needing to touch static-data.
            var creationIndex = unchecked((int)(DateTime.UtcNow.Ticks & 0x7fffffff)) + 1;
            var collection = new HenchmanCollection
            {
                CreationIndex = creationIndex,
                Data = henches.ToArray(),
            };

            CachedHenchmanCollectionCreationIndex = creationIndex;
            CachedHenchmanCollectionSnapshots = snapshots;

            return HenchRepoSerializer.SerializeHenchmanCollection(collection);
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

            // Force empty to prevent client from indexing skillLoadoutComponent.Weapons[1] for henchmen.
            // Use Item.Empty (not null) because it round-trips through PCSSerializer consistently.
            snapshot.PlayerCharacterInventory.SecondaryWeapon = Item.Empty;

            if (Item.IsNullOrEmptyArmor(snapshot.PlayerCharacterInventory.Armor))
            {
                snapshot.PlayerCharacterInventory.Armor = CreateInventoryItem(PlayerCharacterDefaultValues.Armor, 2);
            }
        }

        private static string SerializeFallbackHenchmanCollection()
        {
            const string ownerCharacterIdentifier = "DEFAULT";

            var henches = new List<HenchmanRepositoryPlayerCharacterSnapshot>();
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

                var entry = new HenchmanRepositoryPlayerCharacterSnapshot(ownerCharacterIdentifier);
                entry.IsDefaultHench = false;
                entry.PlayerCharacterSnapshot = snapshot;
                henches.Add(entry);
            }

            var collection = new HenchmanCollection
            {
                CreationIndex = 1,
                Data = henches.ToArray(),
            };

            CachedHenchmanCollectionCreationIndex = 1;
            CachedHenchmanCollectionSnapshots = henches.Select(h => h != null ? h.PlayerCharacterSnapshot : null).Where(s => s != null).ToList();

            return HenchRepoSerializer.SerializeHenchmanCollection(collection);
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

                // NOTE: MetaGameplayCommunicationObject.onInventoryChanged expects a compressed binary string
                // produced by Cliffhanger.SRO.ServerClientCommons.InventorySerializer, not JSON.
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

                // Common patterns seen in client payloads:
                // - MindLevelSkill_7_2
                // - PistolLevelSkill_4_1
                // Try to read the number immediately after "LevelSkill_".
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

                // Fallback: pick the first underscore-delimited numeric segment, e.g. "..._7_2" => 7.
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
                // null
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

            // Coop start sends the selection array directly as payload string #4, not wrapped under a "HenchmanSelection" key.
            // Reuse the singleplayer parser by wrapping into a small object.
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

            // Cosmetics/appearance in missions are driven by EquippedItems; preserve them from the hub roster.
            // The client will also inject default underwear if those slots are empty.
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

        private static string BuildProgressionHubInstanceId(string hubName)
        {
            var canonicalHubName = !IsNullOrWhiteSpace(hubName) ? hubName : DefaultHubId;
            var sequence = Interlocked.Increment(ref _hubInstanceSequence);
            return canonicalHubName + "#" + sequence.ToString(CultureInfo.InvariantCulture);
        }

        private PlayerCharacterSnapshot BuildMappedPlayerCharacterSnapshotForHub(Guid identityGuid, string characterIdentifier, string characterName, CareerSlot slot)
        {
            if (IsNullOrWhiteSpace(characterIdentifier))
            {
                return null;
            }

            var snapshot = BuildPlayerCharacterSnapshotForSlot(characterIdentifier, characterName, slot);
            var accountId = identityGuid != Guid.Empty ? identityGuid : TryParseAccountIdFromCharacterIdentifier(characterIdentifier);
            ulong mappedPlayerId;
            if (accountId != Guid.Empty
                && TryGetGameClientEntityIdForIdentity(accountId, out mappedPlayerId)
                && mappedPlayerId != 0UL)
            {
                snapshot.PlayerId = mappedPlayerId;
            }

            return snapshot;
        }

        private PortedHubTransitionResult TryExecutePortedHubTransition(string requestedHubId, Guid identityGuid, string characterIdentifier, string characterName, CareerSlot slot, PortedHubInstance currentHubInstance)
        {
            if (_portedHubInstanceManager == null || IsNullOrWhiteSpace(requestedHubId) || IsNullOrWhiteSpace(characterIdentifier))
            {
                return null;
            }

            var snapshot = BuildMappedPlayerCharacterSnapshotForHub(identityGuid, characterIdentifier, characterName, slot);
            if (snapshot == null)
            {
                return null;
            }

            var exactTargetHub = _portedHubInstanceManager.RequestHubInstanceByHubId(requestedHubId);
            if (exactTargetHub != null)
            {
                return _portedHubInstanceManager.ExecuteRequestHubInstance(exactTargetHub, snapshot, currentHubInstance);
            }

            return _portedHubInstanceManager.ExecuteRequestHubInstance(GetHubNameFromHubInstanceId(requestedHubId), snapshot, currentHubInstance, new GroupStatus());
        }

        private byte[] BuildPortedHubStatePayloadForSlot(CareerSlot slot, Guid identityGuid, int careerIndex, bool forceNewHubInstanceId, PortedHubInstance currentHubInstance, out string resolvedHubId, out PortedHubInstance resolvedHubInstance)
        {
            resolvedHubId = slot != null && !IsNullOrWhiteSpace(slot.HubId) ? slot.HubId : DefaultHubId;
            resolvedHubInstance = currentHubInstance;

            var characterIdentifier = slot != null && !IsNullOrWhiteSpace(slot.CharacterIdentifier)
                ? slot.CharacterIdentifier
                : (identityGuid.ToString() + ":" + careerIndex.ToString(CultureInfo.InvariantCulture));
            var characterName = slot != null ? slot.CharacterName : null;
            var requestedHubId = forceNewHubInstanceId ? BuildProgressionHubInstanceId(resolvedHubId) : resolvedHubId;

            if (_portedHubInstanceManager != null && !IsNullOrWhiteSpace(characterIdentifier))
            {
                if (resolvedHubInstance == null)
                {
                    resolvedHubInstance = _portedHubInstanceManager.RequestHubInstance(characterIdentifier);
                }

                var snapshot = BuildMappedPlayerCharacterSnapshotForHub(identityGuid, characterIdentifier, characterName, slot);
                if (snapshot != null)
                {
                    PortedHubTransitionResult transition = forceNewHubInstanceId
                        ? _portedHubInstanceManager.ExecuteRequestExactHubInstance(requestedHubId, snapshot, resolvedHubInstance)
                        : TryExecutePortedHubTransition(requestedHubId, identityGuid, characterIdentifier, characterName, slot, resolvedHubInstance);

                    if (transition != null && transition.TargetHubInstance != null)
                    {
                        resolvedHubInstance = transition.TargetHubInstance;
                        resolvedHubId = resolvedHubInstance.HubId;
                        return BuildMetaHubPushPayload(HubEntityId, resolvedHubInstance.SerializedHubState());
                    }
                }
            }

            resolvedHubId = requestedHubId;
            return BuildMetaHubPushPayload(HubEntityId, FallbackSerializedHubState);
        }

        private string BuildSerializedSharedHubStateOrFallback(string hubId, string fallbackCharacterIdentifier, string fallbackCharacterName, CareerSlot fallbackSlot)
        {
            if (IsNullOrWhiteSpace(hubId))
            {
                return FallbackSerializedHubState;
            }

            if (_portedHubInstanceManager != null)
            {
                var portedHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(hubId);
                if (portedHubInstance == null && !IsNullOrWhiteSpace(fallbackCharacterIdentifier))
                {
                    var identityGuid = TryParseAccountIdFromCharacterIdentifier(fallbackCharacterIdentifier);
                    var snapshot = BuildMappedPlayerCharacterSnapshotForHub(identityGuid, fallbackCharacterIdentifier, fallbackCharacterName, fallbackSlot);
                    if (snapshot != null)
                    {
                        var transition = _portedHubInstanceManager.ExecuteRequestExactHubInstance(hubId, snapshot, null);
                        if (transition != null)
                        {
                            portedHubInstance = transition.TargetHubInstance;
                        }
                    }
                }

                if (portedHubInstance != null)
                {
                    var serializedHubState = portedHubInstance.SerializedHubState();
                    if (!IsNullOrWhiteSpace(serializedHubState))
                    {
                        return serializedHubState;
                    }
                }
            }

            return FallbackSerializedHubState;
        }

        private static string GetHubNameFromHubInstanceId(string hubId)
        {
            if (IsNullOrWhiteSpace(hubId))
            {
                return DefaultHubId;
            }

            var idx = hubId.IndexOf('#');
            if (idx > 0)
            {
                return hubId.Substring(0, idx);
            }

            return hubId;
        }

        private static bool TryParseRequestStoryHubForPayload(byte[] data, out string groupHostCharacterId, out Guid groupHostAccountId)
        {
            groupHostCharacterId = null;
            groupHostAccountId = Guid.Empty;

            if (data == null || data.Length < 8)
            {
                return false;
            }

            var pos = 0;
            string rawHostAccountId;
            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out groupHostCharacterId))
            {
                return false;
            }

            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out rawHostAccountId))
            {
                return false;
            }

            try
            {
                if (!IsNullOrWhiteSpace(rawHostAccountId))
                {
                    groupHostAccountId = new Guid(rawHostAccountId);
                }
            }
            catch
            {
                groupHostAccountId = Guid.Empty;
            }

            return true;
        }

        private HubPlayerCharacter BuildHubPlayerCharacterFromParticipant(HubPresenceRegistry.Participant participant)
        {
            if (participant == null || IsNullOrWhiteSpace(participant.CharacterId))
            {
                return null;
            }

            HubPlayerCharacter authoritativeCharacter;
            if (TryGetAuthoritativeHubPlayerCharacter(participant.HubId, participant.CharacterId, out authoritativeCharacter))
            {
                return authoritativeCharacter;
            }

            CareerSlot slot = null;
            if (_userStore != null && !IsNullOrWhiteSpace(participant.IdentityHash))
            {
                try
                {
                    slot = _userStore.GetOrCreateCareer(participant.IdentityHash, participant.CareerIndex, false);
                }
                catch
                {
                    slot = null;
                }
            }

            var snapshot = BuildPlayerCharacterSnapshotForSlot(participant.CharacterId, participant.CharacterName, slot);
            ulong mappedPlayerId;
            var participantAccountId = TryParseAccountIdFromCharacterIdentifier(participant.CharacterId);
            if (participantAccountId != Guid.Empty
                && TryGetGameClientEntityIdForIdentity(participantAccountId, out mappedPlayerId)
                && mappedPlayerId != 0UL)
            {
                snapshot.PlayerId = mappedPlayerId;
            }
            return new HubPlayerCharacter(participant.CharacterId, new Vector2D(participant.X, participant.Y), snapshot);
        }

        private bool TryGetAuthoritativeHubPlayerCharacter(string hubId, string characterId, out HubPlayerCharacter hubPlayerCharacter)
        {
            hubPlayerCharacter = null;
            if (_portedHubInstanceManager == null || IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            var portedHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(hubId);
            if (portedHubInstance == null || portedHubInstance.HubState == null || portedHubInstance.HubState.PlayerCharacters == null)
            {
                return false;
            }

            return portedHubInstance.HubState.PlayerCharacters.TryGetValue(characterId, out hubPlayerCharacter)
                && hubPlayerCharacter != null;
        }

        private bool TryGetHubParticipantByCharacterId(string characterId, out HubPresenceRegistry.Participant participant)
        {
            participant = null;
            if (IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            string peer;
            if (!_hubPresenceRegistry.TryGetPeerForCharacter(characterId, out peer) || IsNullOrWhiteSpace(peer))
            {
                return false;
            }

            return _hubPresenceRegistry.TryGetParticipantForPeer(peer, out participant) && participant != null;
        }

        private static HubPresenceRegistry.Participant CreateSyntheticParticipant(string hubId, string characterId, HubPlayerCharacter hubPlayerCharacter)
        {
            return new HubPresenceRegistry.Participant
            {
                Peer = string.Empty,
                AccountId = TryParseAccountIdFromCharacterIdentifier(characterId),
                IdentityHash = string.Empty,
                CareerIndex = 0,
                CharacterId = characterId ?? string.Empty,
                CharacterName = hubPlayerCharacter != null && hubPlayerCharacter.Snapshot != null ? (hubPlayerCharacter.Snapshot.CharacterName ?? string.Empty) : string.Empty,
                HubId = hubId ?? string.Empty,
                X = hubPlayerCharacter != null ? hubPlayerCharacter.CurrentPosition.X : 0f,
                Y = hubPlayerCharacter != null ? hubPlayerCharacter.CurrentPosition.Y : 0f,
            };
        }

        private byte[] BuildHubMovementPayload(string hubId, string characterId, float fallbackX, float fallbackY)
        {
            if (IsNullOrWhiteSpace(characterId))
            {
                return null;
            }

            if (_portedHubInstanceManager != null && !IsNullOrWhiteSpace(hubId))
            {
                var portedHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(hubId);
                if (portedHubInstance != null && portedHubInstance.HubState != null && portedHubInstance.HubState.PlayerCharacters != null)
                {
                    HubPlayerCharacter hubPlayerCharacter;
                    if (portedHubInstance.HubState.PlayerCharacters.TryGetValue(characterId, out hubPlayerCharacter)
                        && hubPlayerCharacter != null)
                    {
                        var authoritativeMoves = new[]
                        {
                            new KeyValuePair<string, Vector2D>(characterId, hubPlayerCharacter.CurrentPosition)
                        };

                        return BuildUtf16StringPayload(HubMovementSerializer.Serialize(authoritativeMoves));
                    }
                }
            }

            var moveRequests = new[]
            {
                new KeyValuePair<string, Vector2D>(characterId, new Vector2D(fallbackX, fallbackY))
            };

            return BuildUtf16StringPayload(HubMovementSerializer.Serialize(moveRequests));
        }

        private void RegisterHubPeerStream(string peer, NetworkStream stream)
        {
            if (IsNullOrWhiteSpace(peer) || stream == null)
            {
                return;
            }

            lock (_hubPeerStreamsLock)
            {
                _hubPeerStreams[peer] = stream;
            }
        }

        private void UnregisterHubPeerStream(string peer, NetworkStream stream)
        {
            if (IsNullOrWhiteSpace(peer))
            {
                return;
            }

            lock (_hubPeerStreamsLock)
            {
                NetworkStream existing;
                if (_hubPeerStreams.TryGetValue(peer, out existing) && (stream == null || object.ReferenceEquals(existing, stream)))
                {
                    _hubPeerStreams.Remove(peer);
                }
            }

            ClearHubAnnouncementsForPeer(peer);
        }

        private static string BuildHubAnnouncementToken(string hubId, string characterId)
        {
            return (hubId ?? string.Empty) + "|" + (characterId ?? string.Empty);
        }

        private static bool IsHubAnnouncementTokenForHub(string token, string hubId)
        {
            if (IsNullOrWhiteSpace(token) || IsNullOrWhiteSpace(hubId))
            {
                return false;
            }

            var prefix = hubId + "|";
            return token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private bool TryMarkHubCharacterAnnounced(string targetPeer, string hubId, string characterId)
        {
            if (IsNullOrWhiteSpace(targetPeer) || IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            var token = BuildHubAnnouncementToken(hubId, characterId);
            lock (_hubAnnouncedByPeerLock)
            {
                HashSet<string> announced;
                if (!_hubAnnouncedByPeer.TryGetValue(targetPeer, out announced) || announced == null)
                {
                    announced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _hubAnnouncedByPeer[targetPeer] = announced;
                }

                if (announced.Contains(token))
                {
                    return false;
                }

                announced.Add(token);
                return true;
            }
        }

        private bool IsHubCharacterAnnounced(string targetPeer, string hubId, string characterId)
        {
            if (IsNullOrWhiteSpace(targetPeer) || IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            var token = BuildHubAnnouncementToken(hubId, characterId);
            lock (_hubAnnouncedByPeerLock)
            {
                HashSet<string> announced;
                if (!_hubAnnouncedByPeer.TryGetValue(targetPeer, out announced) || announced == null)
                {
                    return false;
                }

                return announced.Contains(token);
            }
        }

        private void SendHubStateAddToTarget(string hubId, HubPresenceRegistry.Participant participant, HubPeerTarget target, string mode, string source)
        {
            if (IsNullOrWhiteSpace(hubId)
                || participant == null
                || target == null
                || target.Stream == null
                || IsNullOrWhiteSpace(target.Peer)
                || IsNullOrWhiteSpace(participant.CharacterId))
            {
                return;
            }

            var hubPlayerCharacter = BuildHubPlayerCharacterFromParticipant(participant);
            if (hubPlayerCharacter == null)
            {
                return;
            }

            var update = HubStateUpdate.CreateForCharacterAddtion(hubPlayerCharacter, hubId);
            var serializedUpdate = HubSerializer.SerializeHubStateUpdate(update);
            var data = BuildUtf16StringPayload(serializedUpdate);

            LogHubAddPayloadSummary(
                mode,
                hubId,
                target.Peer,
                participant,
                hubPlayerCharacter,
                data,
                source ?? string.Empty);

            var msgNo = ReserveMetaGameplayMsgNos(1);
            var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, HubEntityId, 7, data), msgNo);
            SendRawFrame(target.Stream, target.Peer, PrefixLength(core), "sent HubCommunicationObject HubStateChanged add (" + (mode ?? string.Empty) + ")");

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-add-send",
                mode = mode ?? string.Empty,
                source = source ?? string.Empty,
                hubId = hubId,
                senderPeer = participant.Peer ?? string.Empty,
                characterId = participant.CharacterId,
                sentCount = 1,
                targetPeers = new[] { target.Peer },
            });
        }

        private bool EnsureHubCharacterAnnouncedForTarget(string hubId, HubPresenceRegistry.Participant participant, HubPeerTarget target, string source)
        {
            if (IsNullOrWhiteSpace(hubId)
                || participant == null
                || target == null
                || IsNullOrWhiteSpace(target.Peer)
                || IsNullOrWhiteSpace(participant.CharacterId))
            {
                return false;
            }

            if (IsHubCharacterAnnounced(target.Peer, hubId, participant.CharacterId))
            {
                return true;
            }

            if (!TryMarkHubCharacterAnnounced(target.Peer, hubId, participant.CharacterId))
            {
                return IsHubCharacterAnnounced(target.Peer, hubId, participant.CharacterId);
            }

            SendHubStateAddToTarget(hubId, participant, target, "ensure-announced", source ?? string.Empty);
            return true;
        }

        private void ClearHubAnnouncementsForPeer(string peer)
        {
            if (IsNullOrWhiteSpace(peer))
            {
                return;
            }

            lock (_hubAnnouncedByPeerLock)
            {
                _hubAnnouncedByPeer.Remove(peer);
            }

            lock (_hubReadyByPeerLock)
            {
                _hubReadyByPeer.Remove(peer);
            }

        }

        private void ClearHubAnnouncementsForPeerHub(string peer, string hubId)
        {
            if (IsNullOrWhiteSpace(peer) || IsNullOrWhiteSpace(hubId))
            {
                return;
            }

            lock (_hubAnnouncedByPeerLock)
            {
                HashSet<string> announced;
                if (_hubAnnouncedByPeer.TryGetValue(peer, out announced) && announced != null)
                {
                    announced.RemoveWhere(token => IsHubAnnouncementTokenForHub(token, hubId));
                    if (announced.Count == 0)
                    {
                        _hubAnnouncedByPeer.Remove(peer);
                    }
                }
            }


            lock (_hubReadyByPeerLock)
            {
                HashSet<string> readyHubs;
                if (_hubReadyByPeer.TryGetValue(peer, out readyHubs) && readyHubs != null)
                {
                    readyHubs.Remove(hubId);
                    if (readyHubs.Count == 0)
                    {
                        _hubReadyByPeer.Remove(peer);
                    }
                }
            }

        }

        private void ClearHubAnnouncementForAllPeers(string hubId, string characterId)
        {
            if (IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
            {
                return;
            }

            var token = BuildHubAnnouncementToken(hubId, characterId);
            lock (_hubAnnouncedByPeerLock)
            {
                foreach (var pair in _hubAnnouncedByPeer)
                {
                    if (pair.Value != null)
                    {
                        pair.Value.Remove(token);
                    }
                }
            }

        }

        private static string ResolveHubCharacterIdentifier(CareerSlot slot, Guid identityGuid, int careerIndex, string existingCharacterId)
        {
            if (!IsNullOrWhiteSpace(existingCharacterId))
            {
                return existingCharacterId;
            }

            if (slot != null && !IsNullOrWhiteSpace(slot.CharacterIdentifier))
            {
                return slot.CharacterIdentifier;
            }

            return identityGuid != Guid.Empty
                ? (identityGuid.ToString() + ":" + careerIndex.ToString(CultureInfo.InvariantCulture))
                : string.Empty;
        }

        private bool TryMarkHubPeerReady(string peer, string hubId)
        {
            if (IsNullOrWhiteSpace(peer) || IsNullOrWhiteSpace(hubId))
            {
                return false;
            }

            lock (_hubReadyByPeerLock)
            {
                HashSet<string> readyHubs;
                if (!_hubReadyByPeer.TryGetValue(peer, out readyHubs) || readyHubs == null)
                {
                    readyHubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _hubReadyByPeer[peer] = readyHubs;
                }

                if (readyHubs.Contains(hubId))
                {
                    return false;
                }

                readyHubs.Add(hubId);
                return true;
            }
        }

        private bool IsHubPeerReady(string peer, string hubId)
        {
            if (IsNullOrWhiteSpace(peer) || IsNullOrWhiteSpace(hubId))
            {
                return false;
            }

            lock (_hubReadyByPeerLock)
            {
                HashSet<string> readyHubs;
                if (!_hubReadyByPeer.TryGetValue(peer, out readyHubs) || readyHubs == null)
                {
                    return false;
                }

                return readyHubs.Contains(hubId);
            }
        }

        private IList<HubPeerTarget> GetHubBroadcastTargets(string hubId, string senderPeer)
        {
            var targets = new List<HubPeerTarget>();
            if (IsNullOrWhiteSpace(hubId) || _portedHubInstanceManager == null)
            {
                return targets;
            }

            var portedHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(hubId);
            if (portedHubInstance == null || portedHubInstance.HubState == null || portedHubInstance.HubState.PlayerCharacters == null)
            {
                return targets;
            }

            lock (_hubPeerStreamsLock)
            {
                foreach (var kvp in portedHubInstance.HubState.PlayerCharacters)
                {
                    if (IsNullOrWhiteSpace(kvp.Key))
                    {
                        continue;
                    }

                    string participantPeer;
                    if (!_hubPresenceRegistry.TryGetPeerForCharacter(kvp.Key, out participantPeer) || IsNullOrWhiteSpace(participantPeer))
                    {
                        continue;
                    }

                    if (!IsNullOrWhiteSpace(senderPeer) && string.Equals(participantPeer, senderPeer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    NetworkStream stream;
                    if (!_hubPeerStreams.TryGetValue(participantPeer, out stream) || stream == null)
                    {
                        continue;
                    }

                    targets.Add(new HubPeerTarget(participantPeer, stream));
                }
            }

            return targets;
        }

        private static string ComputePayloadSha1(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                using (var sha1 = new SHA1Managed())
                {
                    var hash = sha1.ComputeHash(payload);
                    var sb = new StringBuilder(hash.Length * 2);
                    for (var i = 0; i < hash.Length; i++)
                    {
                        sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                    }

                    return sb.ToString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetInventoryItemId(Item item)
        {
            if (Item.IsNullOrEmpty(item) || IsNullOrWhiteSpace(item.ItemId))
            {
                return string.Empty;
            }

            return item.ItemId;
        }

        private static object[] BuildEquippedItemsDiagnostics(PlayerCharacterInventory inventory)
        {
            if (inventory == null || inventory.EquippedItems == null || inventory.EquippedItems.Count == 0)
            {
                return new object[0];
            }

            var diagnostics = new List<object>(inventory.EquippedItems.Count);
            for (var i = 0; i < inventory.EquippedItems.Count; i++)
            {
                var equipped = inventory.EquippedItems[i];
                if (equipped == null)
                {
                    continue;
                }

                var item = equipped.Item;
                diagnostics.Add(new
                {
                    slotId = equipped.Definition != null ? equipped.Definition.Id : 0UL,
                    itemId = GetInventoryItemId(item),
                    inventoryKey = item != null ? item.InventoryKey : 0,
                    amount = item != null ? item.Amount : 0,
                    flavourIndex = item != null ? item.FlavourIndex : 0,
                    quality = item != null ? item.Quality : 0,
                });
            }

            return diagnostics.ToArray();
        }

        private void LogHubAddPayloadSummary(
            string source,
            string hubId,
            string targetPeer,
            HubPresenceRegistry.Participant participant,
            HubPlayerCharacter hubPlayerCharacter,
            byte[] payload,
            string replaySource)
        {
            if (participant == null || IsNullOrWhiteSpace(targetPeer))
            {
                return;
            }

            var snapshot = hubPlayerCharacter != null ? hubPlayerCharacter.Snapshot : null;
            var inventory = snapshot != null ? snapshot.PlayerCharacterInventory : null;
            HubPresenceRegistry.Participant targetParticipant;
            _hubPresenceRegistry.TryGetParticipantForPeer(targetPeer, out targetParticipant);

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-add-payload-summary",
                source = source ?? string.Empty,
                hubId = hubId ?? string.Empty,
                targetPeer = targetPeer,
                sourcePeer = participant.Peer ?? string.Empty,
                accountId = participant.AccountId != Guid.Empty ? participant.AccountId.ToString() : string.Empty,
                identityHash = participant.IdentityHash ?? string.Empty,
                targetAccountId = (targetParticipant != null && targetParticipant.AccountId != Guid.Empty)
                    ? targetParticipant.AccountId.ToString()
                    : string.Empty,
                targetIdentityHash = targetParticipant != null ? (targetParticipant.IdentityHash ?? string.Empty) : string.Empty,
                targetCharacterId = targetParticipant != null ? (targetParticipant.CharacterId ?? string.Empty) : string.Empty,
                targetHubId = targetParticipant != null ? (targetParticipant.HubId ?? string.Empty) : string.Empty,
                participantCharacterId = participant.CharacterId ?? string.Empty,
                participantCharacterName = participant.CharacterName ?? string.Empty,
                snapshotCharacterId = snapshot != null ? (snapshot.CharacterIdentifier ?? string.Empty) : string.Empty,
                snapshotCharacterName = snapshot != null ? (snapshot.CharacterName ?? string.Empty) : string.Empty,
                playerId = snapshot != null ? snapshot.PlayerId : 0UL,
                dataVersion = snapshot != null ? snapshot.DataVersion : 0,
                // TEMP DEBUG DIAGNOSTICS: remove these fields once hub add-materialization issues are resolved.
                snapshotBodytype = snapshot != null ? snapshot.Bodytype : 0UL,
                snapshotSkinTextureIndex = snapshot != null ? snapshot.SkinTextureIndex : 0,
                snapshotBackgroundStory = snapshot != null ? snapshot.BackgroundStory : 0UL,
                snapshotVoiceSet = snapshot != null ? (snapshot.Voiceset ?? string.Empty) : string.Empty,
                snapshotPortraitPath = snapshot != null ? (snapshot.PortraitPath ?? string.Empty) : string.Empty,
                snapshotPrimaryWeaponItemId = inventory != null ? GetInventoryItemId(inventory.PrimaryWeapon) : string.Empty,
                snapshotSecondaryWeaponItemId = inventory != null ? GetInventoryItemId(inventory.SecondaryWeapon) : string.Empty,
                snapshotArmorItemId = inventory != null ? GetInventoryItemId(inventory.Armor) : string.Empty,
                snapshotEquippedItems = BuildEquippedItemsDiagnostics(inventory),
                payloadBytes = payload != null ? payload.Length : 0,
                payloadSha1 = ComputePayloadSha1(payload),
                replaySource = replaySource ?? string.Empty,
            });
        }

        private void ReplayHubRosterToPeer(string hubId, string targetPeer, string source)
        {
            if (IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(targetPeer))
            {
                return;
            }

            NetworkStream targetStream;
            lock (_hubPeerStreamsLock)
            {
                if (!_hubPeerStreams.TryGetValue(targetPeer, out targetStream) || targetStream == null)
                {
                    return;
                }
            }

            HubPresenceRegistry.Participant targetParticipant;
            _hubPresenceRegistry.TryGetParticipantForPeer(targetPeer, out targetParticipant);

            if (_portedHubInstanceManager == null)
            {
                return;
            }

            var portedHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(hubId);
            if (portedHubInstance == null || portedHubInstance.HubState == null || portedHubInstance.HubState.PlayerCharacters == null)
            {
                return;
            }

            var updates = new List<byte[]>();
            var sentPlayerIds = new List<ulong>();
            var candidateCount = 0;
            var sentCharacterIds = new List<string>();
            foreach (var kvp in portedHubInstance.HubState.PlayerCharacters)
            {
                var characterId = kvp.Key;
                var hubPlayerCharacter = kvp.Value;
                if (IsNullOrWhiteSpace(characterId)
                    || hubPlayerCharacter == null
                    || (targetParticipant != null && string.Equals(targetParticipant.CharacterId, characterId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                candidateCount++;

                if (IsHubCharacterAnnounced(targetPeer, hubId, characterId))
                {
                    continue;
                }

                if (!TryMarkHubCharacterAnnounced(targetPeer, hubId, characterId))
                {
                    continue;
                }

                var update = HubStateUpdate.CreateForCharacterAddtion(hubPlayerCharacter, hubId);
                var payload = BuildUtf16StringPayload(HubSerializer.SerializeHubStateUpdate(update));
                updates.Add(payload);
                sentCharacterIds.Add(characterId);
                sentPlayerIds.Add(hubPlayerCharacter.Snapshot != null ? hubPlayerCharacter.Snapshot.PlayerId : 0UL);

                HubPresenceRegistry.Participant participant;
                if (!TryGetHubParticipantByCharacterId(characterId, out participant) || participant == null)
                {
                    participant = CreateSyntheticParticipant(hubId, characterId, hubPlayerCharacter);
                }

                LogHubAddPayloadSummary(
                    "roster-replay",
                    hubId,
                    targetPeer,
                    participant,
                    hubPlayerCharacter,
                    payload,
                    source);
            }

            if (updates.Count == 0)
            {
                return;
            }

            var firstMsgNo = ReserveMetaGameplayMsgNos(updates.Count);
            for (var i = 0; i < updates.Count; i++)
            {
                try
                {
                    var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, HubEntityId, 7, updates[i]), firstMsgNo + (ulong)i);
                    SendRawFrame(targetStream, targetPeer, PrefixLength(core), "sent HubCommunicationObject HubStateChanged add (roster replay)");
                }
                catch
                {
                }
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-roster-replay",
                targetPeer = targetPeer,
                hubId = hubId,
                candidates = candidateCount,
                sent = updates.Count,
                sentCharacterIds = sentCharacterIds,
                sentPlayerIds = sentPlayerIds,
                source = source ?? string.Empty,
                authoritative = true,
            });

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-add-send",
                mode = "roster-replay",
                targetPeer = targetPeer,
                hubId = hubId,
                source = source ?? string.Empty,
                sentCount = updates.Count,
                sentCharacterIds = sentCharacterIds,
                authoritative = true,
            });
        }

        private bool TryActivateHubReadiness(string peer, string hubId, string reason)
        {
            if (IsNullOrWhiteSpace(peer) || IsNullOrWhiteSpace(hubId))
            {
                return false;
            }

            HubPresenceRegistry.Participant participant;
            if (!_hubPresenceRegistry.TryGetParticipantForPeer(peer, out participant)
                || participant == null
                || IsNullOrWhiteSpace(participant.HubId)
                || !string.Equals(participant.HubId, hubId, StringComparison.OrdinalIgnoreCase)
                || IsNullOrWhiteSpace(participant.CharacterId))
            {
                return false;
            }

            if (!TryMarkHubPeerReady(peer, hubId))
            {
                return false;
            }

            ReplayHubRosterToPeer(hubId, peer, reason ?? string.Empty);
            BroadcastHubStateAddToReadyPeers(hubId, peer, participant, reason ?? string.Empty);

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-first-move-sync",
                peer = peer,
                hubId = hubId,
                characterId = participant.CharacterId,
                reason = reason ?? string.Empty,
            });

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-ready",
                peer = peer,
                hubId = hubId,
                characterId = participant.CharacterId,
                reason = reason ?? string.Empty,
            });

            return true;
        }

        private void BroadcastHubMovement(string hubId, string senderPeer, string characterId, float x, float y)
        {
            if (IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
            {
                return;
            }

            var targets = GetHubBroadcastTargets(hubId, senderPeer);
            if (targets.Count == 0)
            {
                return;
            }

            HubPresenceRegistry.Participant senderParticipant;
            var canEnsureAnnouncement = _hubPresenceRegistry.TryGetParticipantForPeer(senderPeer, out senderParticipant)
                && senderParticipant != null
                && !IsNullOrWhiteSpace(senderParticipant.CharacterId)
                && string.Equals(senderParticipant.CharacterId, characterId, StringComparison.OrdinalIgnoreCase);

            var filteredTargets = new List<HubPeerTarget>(targets.Count);
            var ensuredTargets = new List<string>();
            var blockedTargets = new List<string>();
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null || IsNullOrWhiteSpace(target.Peer))
                {
                    continue;
                }

                if (IsHubCharacterAnnounced(target.Peer, hubId, characterId))
                {
                    filteredTargets.Add(target);
                }
                else if (canEnsureAnnouncement
                    && IsHubPeerReady(target.Peer, hubId)
                    && EnsureHubCharacterAnnouncedForTarget(hubId, senderParticipant, target, "move-send"))
                {
                    filteredTargets.Add(target);
                    ensuredTargets.Add(target.Peer);
                }
                else
                {
                    blockedTargets.Add(target.Peer);
                }
            }

            if (filteredTargets.Count == 0)
            {
                return;
            }

            var targetPeers = new List<string>(filteredTargets.Count);
            for (var i = 0; i < filteredTargets.Count; i++)
            {
                if (filteredTargets[i] != null && !IsNullOrWhiteSpace(filteredTargets[i].Peer))
                {
                    targetPeers.Add(filteredTargets[i].Peer);
                }
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-move-broadcast",
                hubId = hubId,
                senderPeer = senderPeer ?? string.Empty,
                characterId = characterId,
                x = x,
                y = y,
                targets = targetPeers,
            });

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-move-send",
                hubId = hubId,
                senderPeer = senderPeer ?? string.Empty,
                characterId = characterId,
                totalTargets = targets.Count,
                eligibleTargets = filteredTargets.Count,
                ensuredTargets = ensuredTargets,
                blockedTargets = blockedTargets,
            });

            var data = BuildHubMovementPayload(hubId, characterId, x, y);
            if (data == null)
            {
                return;
            }

            BroadcastHubFieldEvent(filteredTargets, 4, data, "sent HubCommunicationObject ExecuteMoveToPosition (broadcast)");
        }

        private void BroadcastHubStateAddToReadyPeers(string hubId, string senderPeer, HubPresenceRegistry.Participant participant, string source)
        {
            if (IsNullOrWhiteSpace(hubId) || participant == null)
            {
                return;
            }

            var hubPlayerCharacter = BuildHubPlayerCharacterFromParticipant(participant);
            if (hubPlayerCharacter == null)
            {
                return;
            }

            var update = HubStateUpdate.CreateForCharacterAddtion(hubPlayerCharacter, hubId);
            var serializedUpdate = HubSerializer.SerializeHubStateUpdate(update);
            var data = BuildUtf16StringPayload(serializedUpdate);

            var targets = GetHubBroadcastTargets(hubId, senderPeer);
            if (targets.Count == 0)
            {
                return;
            }

            var filteredTargets = new List<HubPeerTarget>(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null || IsNullOrWhiteSpace(target.Peer))
                {
                    continue;
                }

                if (!IsHubPeerReady(target.Peer, hubId))
                {
                    continue;
                }

                if (TryMarkHubCharacterAnnounced(target.Peer, hubId, participant.CharacterId))
                {
                    filteredTargets.Add(target);
                }
            }

            if (filteredTargets.Count == 0)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "hub-add-broadcast",
                    hubId = hubId,
                    senderPeer = senderPeer ?? string.Empty,
                    characterId = participant.CharacterId,
                    playerId = (hubPlayerCharacter.Snapshot != null ? hubPlayerCharacter.Snapshot.PlayerId : 0UL),
                    targets = targets.Count,
                    sentTargets = 0,
                });
                return;
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-add-broadcast",
                hubId = hubId,
                senderPeer = senderPeer ?? string.Empty,
                characterId = participant.CharacterId,
                playerId = (hubPlayerCharacter.Snapshot != null ? hubPlayerCharacter.Snapshot.PlayerId : 0UL),
                targets = targets.Count,
                sentTargets = filteredTargets.Count,
                targetPeers = filteredTargets.Select(t => t != null ? t.Peer : string.Empty).ToList(),
            });

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-add-send",
                mode = "broadcast",
                source = source ?? string.Empty,
                hubId = hubId,
                senderPeer = senderPeer ?? string.Empty,
                characterId = participant.CharacterId,
                sentCount = filteredTargets.Count,
                targetPeers = filteredTargets.Select(t => t != null ? t.Peer : string.Empty).ToList(),
            });

            for (var i = 0; i < filteredTargets.Count; i++)
            {
                var target = filteredTargets[i];
                if (target == null || IsNullOrWhiteSpace(target.Peer))
                {
                    continue;
                }

                LogHubAddPayloadSummary(
                    "broadcast",
                    hubId,
                    target.Peer,
                    participant,
                    hubPlayerCharacter,
                    data,
                    source ?? string.Empty);
            }

            BroadcastHubFieldEvent(filteredTargets, 7, data, "sent HubCommunicationObject HubStateChanged add (broadcast)");
        }

        private void BroadcastHubStateRemove(string hubId, string senderPeer, string removedCharacterId)
        {
            if (IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(removedCharacterId))
            {
                return;
            }

            ClearHubAnnouncementForAllPeers(hubId, removedCharacterId);

            var update = HubStateUpdate.CreateForRemoveCharacter(removedCharacterId, hubId);
            var serializedUpdate = HubSerializer.SerializeHubStateUpdate(update);
            var data = BuildUtf16StringPayload(serializedUpdate);

            var targets = GetHubBroadcastTargets(hubId, senderPeer);
            if (targets.Count == 0)
            {
                return;
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-remove-send",
                hubId = hubId,
                senderPeer = senderPeer ?? string.Empty,
                removedCharacterId = removedCharacterId,
                sentCount = targets.Count,
                targetPeers = targets.Select(t => t != null ? t.Peer : string.Empty).ToList(),
            });

            BroadcastHubFieldEvent(targets, 7, data, "sent HubCommunicationObject HubStateChanged remove (broadcast)");
        }

        private void BroadcastHubFieldEvent(IList<HubPeerTarget> targets, ushort fieldId, byte[] data, string note)
        {
            if (targets == null || targets.Count == 0 || data == null)
            {
                return;
            }

            var firstMsgNo = ReserveMetaGameplayMsgNos(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null || target.Stream == null)
                {
                    continue;
                }

                try
                {
                    var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, HubEntityId, fieldId, data), firstMsgNo + (ulong)i);
                    SendRawFrame(target.Stream, target.Peer, PrefixLength(core), note);
                }
                catch
                {
                }
            }
        }

        private void RemoveHubPresenceWithBroadcast(string peer)
        {
            if (IsNullOrWhiteSpace(peer))
            {
                return;
            }

            ClearHubAnnouncementsForPeer(peer);

            HubPresenceRegistry.Participant existing;
            if (_hubPresenceRegistry.TryGetParticipantForPeer(peer, out existing)
                && existing != null
                && !IsNullOrWhiteSpace(existing.HubId)
                && !IsNullOrWhiteSpace(existing.CharacterId))
            {
                BroadcastHubStateRemove(existing.HubId, peer, existing.CharacterId);
            }

            _hubPresenceRegistry.RemovePeer(peer);
        }

        private void RegisterOrUpdateHubPresenceWithDuplicateRetire(
            string peer,
            Guid accountId,
            string identityHash,
            int careerIndex,
            string characterId,
            string characterName,
            string hubId,
            float x,
            float y,
            string reason)
        {
            if (!IsNullOrWhiteSpace(characterId))
            {
                string existingPeer;
                if (_hubPresenceRegistry.TryGetPeerForCharacter(characterId, out existingPeer)
                    && !IsNullOrWhiteSpace(existingPeer)
                    && !string.Equals(existingPeer, peer, StringComparison.OrdinalIgnoreCase))
                {
                    var total = Interlocked.Increment(ref _hubDuplicateSessionRetiredTotal);
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "hub-duplicate-session-retired",
                        reason = reason ?? string.Empty,
                        characterId = characterId,
                        replacementPeer = peer ?? string.Empty,
                        retiredPeer = existingPeer,
                        total = total,
                    });

                    RemoveHubPresenceWithBroadcast(existingPeer);
                    UnregisterHubPeerStream(existingPeer, null);
                }
            }

            _hubPresenceRegistry.RegisterOrUpdate(
                peer,
                accountId,
                identityHash,
                careerIndex,
                characterId,
                characterName,
                hubId,
                x,
                y);
        }

        private bool IsHubMoveOwnershipValid(
            string peer,
            Guid activeIdentityGuid,
            string movedCharacterId,
            HubPresenceRegistry.Participant movementParticipant)
        {
            if (IsNullOrWhiteSpace(movedCharacterId))
            {
                return true;
            }

            if (activeIdentityGuid == Guid.Empty)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "hub-move-rejected-owner-mismatch",
                    reason = "unauthenticated-identity",
                    peer = peer ?? string.Empty,
                    characterId = movedCharacterId,
                });
                return false;
            }

            var ownerAccountId = TryParseAccountIdFromCharacterIdentifier(movedCharacterId);
            if (ownerAccountId == Guid.Empty || ownerAccountId != activeIdentityGuid)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "hub-move-rejected-owner-mismatch",
                    reason = ownerAccountId == Guid.Empty ? "invalid-character-id" : "account-mismatch",
                    peer = peer ?? string.Empty,
                    characterId = movedCharacterId,
                    activeIdentityGuid = activeIdentityGuid.ToString(),
                    parsedOwnerAccountId = ownerAccountId != Guid.Empty ? ownerAccountId.ToString() : string.Empty,
                });
                return false;
            }

            if (movementParticipant != null
                && !IsNullOrWhiteSpace(movementParticipant.CharacterId)
                && !string.Equals(movementParticipant.CharacterId, movedCharacterId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "hub-move-rejected-owner-mismatch",
                    reason = "registered-character-mismatch",
                    peer = peer ?? string.Empty,
                    characterId = movedCharacterId,
                    registeredCharacterId = movementParticipant.CharacterId,
                });
                return false;
            }

            return true;
        }

        private static PlayerCharacterSnapshot BuildPlayerCharacterSnapshotForSlot(string characterIdentifier, string characterName, CareerSlot slot)
        {
            var snapshot = new PlayerCharacterSnapshot();
            snapshot.IsHenchman = false;
            snapshot.PlayerId = 1UL;
            snapshot.DataVersion = 48;
            snapshot.CharacterIdentifier = characterIdentifier ?? string.Empty;
            snapshot.CharacterName = !IsNullOrWhiteSpace(characterName) ? characterName : PlayerCharacterDefaultValues.PlayerName;
            snapshot.PortraitPath = (slot != null && !IsNullOrWhiteSpace(slot.PortraitPath)) ? slot.PortraitPath : PlayerCharacterDefaultValues.PortraitPath;
            snapshot.Voiceset = (slot != null && !IsNullOrWhiteSpace(slot.Voiceset)) ? slot.Voiceset : PlayerCharacterDefaultValues.Voiceset;
            snapshot.Bodytype = (slot != null && slot.Bodytype != 0UL) ? slot.Bodytype : PlayerCharacterDefaultValues.Bodytype;
            snapshot.SkinTextureIndex = (slot != null) ? slot.SkinTextureIndex : PlayerCharacterDefaultValues.SkinTextureIndex;
            snapshot.BackgroundStory = (slot != null && slot.BackgroundStory != 0UL) ? slot.BackgroundStory : PlayerCharacterDefaultValues.BackgroundStory;
            snapshot.WantsBackgroundChange = slot != null && slot.WantsBackgroundChange;

            // Wallet is what PCSSerializer uses; Karma/Nuyen properties are derived/read-only in this build.
            if (snapshot.Wallet != null)
            {
                var karma = slot != null ? slot.Karma : 0;
                var spentKarma = slot != null ? slot.SpentKarma : 0;
                var nuyen = slot != null ? slot.Nuyen : 0;
                snapshot.Wallet.Reset(CurrencyId.Karma, karma, spentKarma);
                snapshot.Wallet.Reset(CurrencyId.Nuyen, nuyen, 0);
            }

            if (snapshot.SkillTreeDefinitions == null)
            {
                snapshot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
            }
            if (slot != null && slot.SkillTreeDefinitions != null && slot.SkillTreeDefinitions.Count > 0)
            {
                foreach (var kvp in slot.SkillTreeDefinitions)
                {
                    if (IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
                    {
                        continue;
                    }
                    snapshot.SkillTreeDefinitions[kvp.Key] = kvp.Value;
                }
            }

            var pcInv = new PlayerCharacterInventory();
            var primaryItemId = (slot != null && !IsNullOrWhiteSpace(slot.PrimaryWeaponItemId)) ? slot.PrimaryWeaponItemId : PlayerCharacterDefaultValues.PrimaryWeapon;
            var primaryKey = slot != null ? slot.PrimaryWeaponInventoryKey : 0;
            pcInv.PrimaryWeapon = CreateInventoryItem(primaryItemId, primaryKey);

            var secondaryItemId = (slot != null && !IsNullOrWhiteSpace(slot.SecondaryWeaponItemId)) ? slot.SecondaryWeaponItemId : PlayerCharacterDefaultValues.SecondaryWeapon;
            var secondaryKey = slot != null ? slot.SecondaryWeaponInventoryKey : 1;
            pcInv.SecondaryWeapon = CreateInventoryItem(secondaryItemId, secondaryKey);

            var armorItemId = (slot != null && !IsNullOrWhiteSpace(slot.ArmorItemId)) ? slot.ArmorItemId : PlayerCharacterDefaultValues.Armor;
            var armorKey = slot != null ? slot.ArmorInventoryKey : 2;
            pcInv.Armor = CreateInventoryItem(armorItemId, armorKey);
            if (slot != null && slot.EquippedItems != null && slot.EquippedItems.Count > 0)
            {
                foreach (var kvp in slot.EquippedItems)
                {
                    if (IsNullOrWhiteSpace(kvp.Key) || IsNullOrWhiteSpace(kvp.Value))
                    {
                        continue;
                    }
                    ulong slotId;
                    if (!TryParseUInt64(kvp.Key, out slotId) || slotId == 0UL)
                    {
                        continue;
                    }

                    var def = new LogicItemslotDefinition();
                    def.Id = slotId;
                    def.AssignableItemTypes = new ulong[0];
                    def.CannotBeEmpty = false;
                    def.DefaultItem = string.Empty;

                    var itemSlot = new ItemSlot(def);
                    itemSlot.Item = CreateInventoryItem(kvp.Value, 10);
                    pcInv.EquippedItems.Add(itemSlot);
                }
            }
            snapshot.PlayerCharacterInventory = pcInv;

            return snapshot;
        }

        private static Guid TryParseAccountIdFromCharacterIdentifier(string characterIdentifier)
        {
            if (IsNullOrWhiteSpace(characterIdentifier))
            {
                return Guid.Empty;
            }

            var separator = characterIdentifier.IndexOf(':');
            var guidPart = separator > 0 ? characterIdentifier.Substring(0, separator) : characterIdentifier;
            Guid parsed;
            try
            {
                parsed = new Guid(guidPart);
            }
            catch
            {
                return Guid.Empty;
            }

            return parsed;
        }

        private static IDictionary TryDeserializeJsonDict(string json)
        {
            if (IsNullOrWhiteSpace(json))
            {
                return null;
            }
            try
            {
                return Json.DeserializeObject(json) as IDictionary;
            }
            catch
            {
                return null;
            }
        }

        private static IDictionary GetDictValue(IDictionary dict, string key)
        {
            if (dict == null || IsNullOrWhiteSpace(key) || !dict.Contains(key) || dict[key] == null)
            {
                return null;
            }
            return dict[key] as IDictionary;
        }

        private static object[] GetArrayValue(IDictionary dict, string key)
        {
            if (dict == null || IsNullOrWhiteSpace(key) || !dict.Contains(key) || dict[key] == null)
            {
                return null;
            }

            var arr = dict[key] as object[];
            if (arr != null)
            {
                return arr;
            }

            var list = dict[key] as ArrayList;
            if (list != null)
            {
                return list.ToArray();
            }

            return null;
        }

        private static string GetStringValue(IDictionary dict, string key)
        {
            if (dict == null || IsNullOrWhiteSpace(key) || !dict.Contains(key) || dict[key] == null)
            {
                return null;
            }
            return dict[key] as string;
        }

        private static ulong GetUInt64Value(IDictionary dict, string key, ulong fallback)
        {
            if (dict == null || IsNullOrWhiteSpace(key) || !dict.Contains(key) || dict[key] == null)
            {
                return fallback;
            }
            try
            {
                return Convert.ToUInt64(dict[key], CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static int GetInt32Value(IDictionary dict, string key, int fallback)
        {
            if (dict == null || IsNullOrWhiteSpace(key) || !dict.Contains(key) || dict[key] == null)
            {
                return fallback;
            }
            try
            {
                return Convert.ToInt32(dict[key], CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static bool TryInferMetatypeAndGenderFromPortrait(string portraitPath, out ulong metatypeId, out ulong genderId)
        {
            metatypeId = 0UL;
            genderId = 0UL;
            if (IsNullOrWhiteSpace(portraitPath))
            {
                return false;
            }

            // Portrait paths look like:
            // GUI/Textures/Metagameplay/player_portraits/portrait_male_troll_frederick_eccher_
            // GUI/Textures/Metagameplay/player_portraits/portrait_female_human_mage_
            // We use this as a fallback when the client doesn't send BodyChange.
            var p = portraitPath;
            if (p.IndexOf("portrait_male_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                genderId = 196610UL; // Male
            }
            else if (p.IndexOf("portrait_female_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                genderId = 196609UL; // Female
            }

            if (p.IndexOf("_human_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                metatypeId = 197009UL;
            }
            else if (p.IndexOf("_orc_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                metatypeId = 197010UL;
            }
            else if (p.IndexOf("_troll_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                metatypeId = 197011UL;
            }
            else if (p.IndexOf("_dwarf_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                metatypeId = 197012UL;
            }
            else if (p.IndexOf("_elf_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                metatypeId = 197013UL;
            }

            return metatypeId != 0UL && genderId != 0UL;
        }

        private static readonly byte[] CoreHelloPayloadPrefix = HexToBytes("02310000000007000000322E302E322E3720000000433245314137464537424233463930443330413242414331333135443431463001");
        private static readonly byte[] CoreIntroduceGameClientPayload = HexToBytes("012500000003010000000000000005001600000000090000003132372E302E302E3101000000000000000100000000000000");
        private static readonly byte[] CoreInitPayload = HexToBytes("0116000000000D02EA0710280000000100000001000000000000000100000000000000");

        private const ulong DefaultIntroduceMsgNo = 1UL;
        private const ulong AccountEntityId = 2UL;
        private const ulong MetaGameplayEntityId = 3UL;
        private const ulong HubEntityId = 4UL;
        private const ushort GameClientConnectionTypeId = 5;

        private long _nextGameClientEntityId = 1000;
        private readonly object _identityEntityIdLock = new object();
        private readonly Dictionary<Guid, ulong> _gameClientEntityIdByIdentity = new Dictionary<Guid, ulong>();

        private readonly LocalServiceOptions _options;
        private readonly RequestLogger _logger;
        private readonly LocalUserStore _userStore;
        private readonly ISessionIdentityMap _sessionIdentityMap;
        private readonly CareerInfoGenerator _careerInfoGenerator;
        private readonly MatchConfigurationGenerator _matchConfigurationGenerator;
        private readonly CharacterStatePushBroker _characterStatePushBroker;
        private readonly PortedMissionRewardService _missionRewardService;
        private readonly PortedStoryProgressionService _storyProgressionService;
        private readonly PortedSkillPurchaseService _skillPurchaseService;
        private readonly PortedShopInventoryService _shopInventoryService;
        private readonly PortedHubInstanceManager _portedHubInstanceManager;

        // APlay DirectSystem messages include an 8-byte message number the client may use for ordering/dedup.
        // For MetaGameplay pushes we must keep these monotonic even if the client repeats a request with a lower MsgNo.
        private long _metaGameplayOutMsgNoHighWatermark;

        private const int HubStateDedupWindowMs = 1500;
        private const int CreationInfoDedupWindowMs = 10000;
        private readonly object _hubPushDedupLock = new object();
        private readonly Dictionary<string, HubPushDedupState> _hubPushDedupByPeer = new Dictionary<string, HubPushDedupState>(StringComparer.OrdinalIgnoreCase);
        private readonly HubPresenceRegistry _hubPresenceRegistry = new HubPresenceRegistry();
        private readonly object _hubPeerStreamsLock = new object();
        private readonly Dictionary<string, NetworkStream> _hubPeerStreams = new Dictionary<string, NetworkStream>(StringComparer.OrdinalIgnoreCase);
        private readonly object _hubAnnouncedByPeerLock = new object();
        private readonly Dictionary<string, HashSet<string>> _hubAnnouncedByPeer = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _hubReadyByPeerLock = new object();
        private readonly Dictionary<string, HashSet<string>> _hubReadyByPeer = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private sealed class HubPushDedupState
        {
            public ulong HubStateHash;
            public long HubStateSentUtcTicks;
            public ulong CreationInfoHash;
            public long CreationInfoSentUtcTicks;
        }

        private static ulong ComputeFnv1a64(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return 0UL;
            }

            unchecked
            {
                const ulong offset = 1469598103934665603UL;
                const ulong prime = 1099511628211UL;
                var hash = offset;
                for (var i = 0; i < data.Length; i++)
                {
                    hash ^= (ulong)data[i];
                    hash *= prime;
                }
                return hash;
            }
        }

        private bool ShouldSuppressDuplicateHubPush(string peer, bool isCreationInfo, byte[] payload)
        {
            try
            {
                if (IsNullOrWhiteSpace(peer) || payload == null || payload.Length == 0)
                {
                    return false;
                }

                var now = DateTime.UtcNow.Ticks;
                var hash = ComputeFnv1a64(payload);
                if (hash == 0UL)
                {
                    return false;
                }

                lock (_hubPushDedupLock)
                {
                    HubPushDedupState state;
                    if (!_hubPushDedupByPeer.TryGetValue(peer, out state) || state == null)
                    {
                        state = new HubPushDedupState();
                        _hubPushDedupByPeer[peer] = state;
                    }

                    var windowTicks = (long)(TimeSpan.TicksPerMillisecond * (isCreationInfo ? CreationInfoDedupWindowMs : HubStateDedupWindowMs));
                    if (isCreationInfo)
                    {
                        if (state.CreationInfoHash == hash && state.CreationInfoSentUtcTicks > 0 && (now - state.CreationInfoSentUtcTicks) <= windowTicks)
                        {
                            return true;
                        }
                        state.CreationInfoHash = hash;
                        state.CreationInfoSentUtcTicks = now;
                        return false;
                    }

                    if (state.HubStateHash == hash && state.HubStateSentUtcTicks > 0 && (now - state.HubStateSentUtcTicks) <= windowTicks)
                    {
                        return true;
                    }
                    state.HubStateHash = hash;
                    state.HubStateSentUtcTicks = now;
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        public APlayTcpStub(LocalServiceOptions options, RequestLogger logger)
            : this(options, logger, new LocalUserStore(options, logger), null, null)
        {
        }

        public APlayTcpStub(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore)
            : this(options, logger, userStore, null, null)
        {
        }

        public APlayTcpStub(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore, ISessionIdentityMap sessionIdentityMap)
            : this(options, logger, userStore, sessionIdentityMap, null)
        {
        }

        public APlayTcpStub(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore, ISessionIdentityMap sessionIdentityMap, CharacterStatePushBroker characterStatePushBroker)
        {
            _options = options;
            _logger = logger;
            _userStore = userStore ?? new LocalUserStore(options, logger);
            _sessionIdentityMap = sessionIdentityMap;
            _careerInfoGenerator = new CareerInfoGenerator(logger, _userStore);
            _matchConfigurationGenerator = new MatchConfigurationGenerator(logger);
            _characterStatePushBroker = characterStatePushBroker ?? CharacterStatePushBroker.Shared;
            _missionRewardService = new PortedMissionRewardService(_options);
            _storyProgressionService = new PortedStoryProgressionService(_options);
            _skillPurchaseService = new PortedSkillPurchaseService(_options);
            _shopInventoryService = new PortedShopInventoryService(_options);
            _portedHubInstanceManager = new PortedHubInstanceManager(new PortedHubRepository(new PortedHubLoader()), false);
        }

        private ulong AllocateGameClientEntityId()
        {
            var next = Interlocked.Increment(ref _nextGameClientEntityId);
            if (next <= 0)
            {
                // Should never happen, but avoid returning 0 which would break player ownership comparisons.
                next = 1000;
                Interlocked.Exchange(ref _nextGameClientEntityId, next);
            }
            return unchecked((ulong)next);
        }

        private void RegisterGameClientEntityIdForIdentity(Guid identityGuid, ulong gameClientEntityId, string peer)
        {
            if (identityGuid == Guid.Empty || gameClientEntityId == 0UL)
            {
                return;
            }

            lock (_identityEntityIdLock)
            {
                _gameClientEntityIdByIdentity[identityGuid] = gameClientEntityId;
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "aplay-identity-entityid",
                peer = peer,
                identityGuid = identityGuid,
                gameClientEntityId = gameClientEntityId,
            });
        }

        private bool TryGetGameClientEntityIdForIdentity(Guid identityGuid, out ulong gameClientEntityId)
        {
            gameClientEntityId = 0UL;
            if (identityGuid == Guid.Empty)
            {
                return false;
            }

            lock (_identityEntityIdLock)
            {
                return _gameClientEntityIdByIdentity.TryGetValue(identityGuid, out gameClientEntityId) && gameClientEntityId != 0UL;
            }
        }

        private ulong ReserveMetaGameplayMsgNos(int count)
        {
            if (count <= 0)
            {
                count = 1;
            }

            while (true)
            {
                var observed = Interlocked.Read(ref _metaGameplayOutMsgNoHighWatermark);
                var observedU = observed > 0 ? (ulong)observed : 0UL;
                var first = observedU + 1UL;
                if (first == 0UL)
                {
                    first = 1UL;
                }

                var last = first + (ulong)count - 1UL;
                if (Interlocked.CompareExchange(ref _metaGameplayOutMsgNoHighWatermark, (long)last, observed) == observed)
                {
                    return first;
                }
            }
        }

        private void TryFlushPendingCharacterStatePushes(Guid identityGuid, string identityHash, int activeCareerIndex, string peer, NetworkStream stream)
        {
            if (_characterStatePushBroker == null || _userStore == null || _careerInfoGenerator == null)
            {
                return;
            }
            if (identityGuid == Guid.Empty || IsNullOrWhiteSpace(identityHash) || stream == null)
            {
                return;
            }

            CharacterStatePushPaths paths;
            if (!_characterStatePushBroker.TryDequeue(identityGuid, out paths) || paths == CharacterStatePushPaths.None)
            {
                return;
            }

            try
            {
                var slot = _userStore.GetOrCreateCareer(identityHash, activeCareerIndex, false);
                if (slot == null)
                {
                    return;
                }

                var sendCount = 0;
                if ((paths & CharacterStatePushPaths.CareerSummaries) != 0) sendCount++;
                if ((paths & CharacterStatePushPaths.Wallet) != 0) sendCount++;
                if ((paths & CharacterStatePushPaths.Inventory) != 0) sendCount++;
                if ((paths & CharacterStatePushPaths.MetaSnapshot) != 0) sendCount++;

                if (sendCount <= 0)
                {
                    return;
                }

                var msgNo = ReserveMetaGameplayMsgNos(sendCount);

                if ((paths & CharacterStatePushPaths.CareerSummaries) != 0)
                {
                    var updatePayload = BuildUtf16StringPayload(BuildCareerSummaryJson(_userStore.GetCareers(identityHash)));
                    var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 2, 16, updatePayload), msgNo++);
                    SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries (queued character-state push)");
                }

                if ((paths & CharacterStatePushPaths.Wallet) != 0)
                {
                    var serializedWallet = SerializeWalletForSlot(slot);
                    var walletChangedPayload = BuildUtf16StringPayload(serializedWallet);
                    var walletChangedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 32, walletChangedPayload), msgNo++);
                    SendRawFrame(stream, peer, PrefixLength(walletChangedCore), "sent MetaGameplayCommunicationObject WalletChanged (queued character-state push)");
                }

                if ((paths & CharacterStatePushPaths.Inventory) != 0)
                {
                    var serializedInventory = SerializeInventoryFromSlot(slot);
                    var emptyShopChanges = InventorySerializer.SerializeShopItemChanges(new ShopItemChanges
                    {
                        Failed = false,
                        TotalNuyenChange = 0,
                        AppliedChanges = new ItemChange[0],
                        NotAppliedChanges = new ItemChange[0],
                    });

                    var inventoryChangedPayload = BuildUtf16StringPayload(serializedInventory, emptyShopChanges);
                    var inventoryChangedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 31, inventoryChangedPayload), msgNo++);
                    SendRawFrame(stream, peer, PrefixLength(inventoryChangedCore), "sent MetaGameplayCommunicationObject InventoryChanged (queued character-state push)");
                }

                if ((paths & CharacterStatePushPaths.MetaSnapshot) != 0)
                {
                    var zippedCareerInfo = _careerInfoGenerator.GetZippedCareerInfo(identityGuid, activeCareerIndex, slot);
                    var metaSnapshotPayload = BuildUtf16StringPayload(zippedCareerInfo);
                    var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), msgNo++);
                    SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient (queued character-state push)");
                }

                _logger.LogAdmin(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "character-state-push",
                    action = "flushed",
                    identityGuid = identityGuid,
                    careerIndex = activeCareerIndex,
                    paths = paths.ToString(),
                });
            }
            catch (Exception ex)
            {
                _logger.LogAdmin(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "character-state-push",
                    action = "flush-failed",
                    identityGuid = identityGuid,
                    careerIndex = activeCareerIndex,
                    paths = paths.ToString(),
                    error = ex.Message,
                });
            }
        }

        private static byte[] BuildCoreIntroduceGameClientPayload(ulong gameClientEntityId, string remoteAddress, ulong aplayClientId)
        {
            if (gameClientEntityId == 0UL)
            {
                gameClientEntityId = 1UL;
            }
            if (IsNullOrWhiteSpace(remoteAddress))
            {
                remoteAddress = "127.0.0.1";
            }

            // CRITICAL: the client is very picky about the introduce packet layout.
            // The known-good hardcoded payload (`CoreIntroduceGameClientPayload`) is:
            //   0x01 + int32(len) + 0x03 + uint64(entityId) + ushort(typeId=5) + int32(payloadLen) + payload + uint64(msgNo)
            // Where:
            //   - `len` does NOT include the trailing msgNo (it matches the captured 0x25 value for addr="127.0.0.1").
            //   - `payload` is: uint8(isAdmin=0) + int32(addrLen) + ASCII(addr) + uint64(aplayClientId)

            var addrBytes = Encoding.ASCII.GetBytes(remoteAddress);
            var payload = Concat(
                new byte[] { 0 },
                BitConverter.GetBytes(addrBytes.Length),
                addrBytes,
                BitConverter.GetBytes(aplayClientId));

            var len = 1 + 8 + 2 + 4 + payload.Length;
            return Concat(
                new byte[] { 0x01 },
                BitConverter.GetBytes(len),
                new byte[] { 0x03 },
                BitConverter.GetBytes(gameClientEntityId),
                BitConverter.GetBytes(GameClientConnectionTypeId),
                BitConverter.GetBytes(payload.Length),
                payload,
                BitConverter.GetBytes(DefaultIntroduceMsgNo));
        }

        private static byte[] BuildCoreApInitializedPayload(uint connectedServerId, ulong entityId, ulong msgNo)
        {
            // APlay-level message type 0 (Initialized):
            //   uint8(type=0) + APDateTime(9 bytes) + uint32(connectedServerId) + uint64(entityId)
            var raw = Concat(
                new byte[] { 0 },
                BuildApDatePayload(DateTimeOffset.UtcNow),
                BitConverter.GetBytes(connectedServerId),
                BitConverter.GetBytes(entityId));

            return Concat(
                new byte[] { 0x01 },
                BitConverter.GetBytes(raw.Length),
                raw,
                BitConverter.GetBytes(msgNo));
        }

        private static byte[] BuildCoreWelcomePayload(ulong id, ulong secret, ulong lastClientMsgNo)
        {
            // Core message type 0 (Welcome): uint8(type=0) + uint64(id) + uint64(secret) + uint64(lastClientMsgNo)
            var raw = Concat(
                new byte[] { 0 },
                BitConverter.GetBytes(id),
                BitConverter.GetBytes(secret),
                BitConverter.GetBytes(lastClientMsgNo));
            return Concat(BitConverter.GetBytes(raw.Length), raw);
        }

        private void SendPendingLootPreviews(ServerSimulationSession simulationSession, System.Net.Sockets.NetworkStream stream, string peer, ulong msgNoBase)
        {
            if (simulationSession == null || stream == null)
            {
                return;
            }

            string[] previews;
            try
            {
                previews = simulationSession.DrainPendingLootPreviews();
            }
            catch
            {
                return;
            }

            if (previews == null || previews.Length == 0)
            {
                return;
            }

            var idx = 0;
            for (var i = 0; i < previews.Length; i++)
            {
                var itemId = previews[i];
                if (IsNullOrWhiteSpace(itemId))
                {
                    continue;
                }

                try
                {
                    // MetaGameplayCommunicationObject.onLootPreview(string item)
                    var payload = BuildUtf16StringPayload(itemId);
                    var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 30, payload), msgNoBase + (ulong)idx);
                    SendRawFrame(stream, peer, PrefixLength(core), "sent MetaGameplayCommunicationObject LootPreview (itemId=" + itemId + ")");
                    idx++;
                }
                catch
                {
                }
            }
        }

        private static StoryMissionstate ParseStoryMissionStateOrDefault(string value, StoryMissionstate fallback)
        {
            if (IsNullOrWhiteSpace(value))
            {
                return fallback;
            }
            try
            {
                return (StoryMissionstate)Enum.Parse(typeof(StoryMissionstate), value.Trim(), true);
            }
            catch
            {
                return fallback;
            }
        }

        private bool IsMissionCompletedForCareer(string identityHash, int careerIndex, string missionName, HashSet<string> fallbackCompletedMissions)
        {
            if (IsNullOrWhiteSpace(missionName))
            {
                return false;
            }

            try
            {
                if (_userStore != null && !IsNullOrWhiteSpace(identityHash))
                {
                    var slot = _userStore.GetOrCreateCareer(identityHash, careerIndex, false);
                    if (slot != null && slot.MainCampaignMissionStates != null)
                    {
                        string raw;
                        if (slot.MainCampaignMissionStates.TryGetValue(missionName, out raw) && !IsNullOrWhiteSpace(raw))
                        {
                            var state = ParseStoryMissionStateOrDefault(raw, StoryMissionstate.Available);
                            if (state >= StoryMissionstate.ReadyToReceiveRewards)
                            {
                                return true;
                            }
                        }

                        return false;
                    }
                }
            }
            catch
            {
            }

            return fallbackCompletedMissions != null && fallbackCompletedMissions.Contains(missionName);
        }

        private void SendUnlocksChanged(NetworkStream stream, string peer, ulong msgNo, string[] activatedUnlocks, string[] deactivatedUnlocks, string reason)
        {
            if (stream == null)
            {
                return;
            }

            var changes = new UnlockChanges();
            AddUnlockChangeEntries(changes.ActivatedUnlocks, activatedUnlocks);
            AddUnlockChangeEntries(changes.DeactivatedUnlocks, deactivatedUnlocks);
            if (changes.IsEmpty())
            {
                return;
            }

            var payload = BuildUtf16StringPayload(Json.Serialize(changes));
            var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 28, payload), msgNo);
            SendRawFrame(stream, peer, PrefixLength(core), "sent MetaGameplayCommunicationObject UnlocksChanged" + (IsNullOrWhiteSpace(reason) ? string.Empty : " (" + reason + ")"));
        }

        private void SendMissionReward(NetworkStream stream, string peer, ulong msgNo, MissionReward missionReward, string reason)
        {
            if (stream == null || missionReward == null)
            {
                return;
            }

            var earnedCurrencies = new List<object>();
            var currencies = missionReward.EarnedCurrencies ?? new CurrencyReward[0];
            for (var i = 0; i < currencies.Length; i++)
            {
                var currency = currencies[i];
                if (currency == null || currency.EarnedValue == 0)
                {
                    continue;
                }

                earnedCurrencies.Add(new Dictionary<string, object>
                {
                    { "CurrencyId", currency.CurrencyId.ToString() },
                    { "EarnedValue", currency.EarnedValue },
                });
            }

            var itemChangesPayload = new List<object>();
            var itemChanges = missionReward.ItemChanges ?? new ItemChange[0];
            for (var i = 0; i < itemChanges.Length; i++)
            {
                var change = itemChanges[i];
                if (change == null || IsNullOrWhiteSpace(change.ItemDefintionId) || change.Delta == 0)
                {
                    continue;
                }

                itemChangesPayload.Add(new Dictionary<string, object>
                {
                    { "ItemDefintionId", change.ItemDefintionId },
                    { "Delta", change.Delta },
                    { "Quality", change.Quality },
                    { "Flavour", change.Flavour },
                });
            }

            var rewardJson = Json.Serialize(new Dictionary<string, object>
            {
                { "GrantedUnlocks", missionReward.GrantedUnlocks ?? new string[0] },
                { "EarnedCurrencies", earnedCurrencies.ToArray() },
                { "ItemChanges", itemChangesPayload.ToArray() },
            });

            var rewardPayload = BuildUtf16StringPayload(rewardJson);
            var rewardCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 35, rewardPayload), msgNo);
            SendRawFrame(stream, peer, PrefixLength(rewardCore), "sent MetaGameplayCommunicationObject GotMissionReward " + reason);
        }

        private static void AddUnlockChangeEntries(List<Unlock> target, string[] unlocks)
        {
            if (target == null || unlocks == null || unlocks.Length <= 0)
            {
                return;
            }

            for (var i = 0; i < unlocks.Length; i++)
            {
                var unlock = unlocks[i];
                if (IsNullOrWhiteSpace(unlock))
                {
                    continue;
                }

                target.Add(new Unlock
                {
                    TechnicalName = unlock,
                });
            }
        }

        private static bool TryGetUlong(IDictionary dict, string key, out ulong value)
        {
            value = 0UL;
            if (dict == null || IsNullOrWhiteSpace(key) || !dict.Contains(key) || dict[key] == null)
            {
                return false;
            }
            try
            {
                value = Convert.ToUInt64(dict[key]);
                return true;
            }
            catch
            {
                value = 0UL;
                return false;
            }
        }

        public void Run(ManualResetEvent stopEvent)
        {
            var listener = new TcpListener(ResolveBindAddress(_options.Host), _options.APlayPort);
            listener.Start();
            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "aplay",
                message = string.Format("tcp stub listening on {0}:{1}", _options.Host, _options.APlayPort),
            });

            ThreadPool.QueueUserWorkItem(delegate
            {
                stopEvent.WaitOne();
                try { listener.Stop(); }
                catch { }
            });

            while (!stopEvent.WaitOne(0))
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (stopEvent.WaitOne(0))
                    {
                        break;
                    }
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                ThreadPool.QueueUserWorkItem(delegate (object state)
                {
                    try
                    {
                        HandleClient((TcpClient)state, stopEvent);
                    }
                    catch (Exception ex)
                    {
                        string workerPeer = "unknown";
                        try
                        {
                            var workerClient = state as TcpClient;
                            if (workerClient != null && workerClient.Client != null && workerClient.Client.RemoteEndPoint != null)
                            {
                                workerPeer = workerClient.Client.RemoteEndPoint.ToString();
                            }
                        }
                        catch
                        {
                        }

                        try
                        {
                            Console.Error.WriteLine("[{0}] [aplay-client-worker-fault] peer={1} exception={2}", RequestLogger.UtcNowIso(), workerPeer, ex.Message);
                            Console.Error.WriteLine(ex.ToString());
                        }
                        catch
                        {
                        }

                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "aplay-client-worker-fault",
                            peer = workerPeer,
                            exception = ex.GetType().FullName,
                            message = ex.Message,
                            stack = ex.ToString(),
                        });
                    }
                }, client);
            }
        }

        private void HandleClient(TcpClient client, ManualResetEvent stopEvent)
        {
            using (client)
            {
                var peer = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "unknown";
                using (var stream = client.GetStream())
                {
                    // The client uses `GameClientConnection.APlayEntityId` as its local PlayerID.
                    // To make coop ownership work, each connection must have a unique entity id.
                    var gameClientEntityId = AllocateGameClientEntityId();
                    var gameClientIntroducePayload = BuildCoreIntroduceGameClientPayload(gameClientEntityId, "127.0.0.1", 1UL);
                    var apInitializedPayload = BuildCoreApInitializedPayload(1U, gameClientEntityId, DefaultIntroduceMsgNo + 1UL);

                    var connectionClosed = new ManualResetEvent(false);
                    var keepAliveLoopStarted = false;
                    long keepAliveMsgNo = 500000;

                    var first = ReadChunk(stream);
                    if (first.Length == 0)
                    {
                        _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "aplay-conn", peer = peer, note = "connected then closed" });
                        return;
                    }

                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "aplay-conn",
                        peer = peer,
                        bytes = first.Length,
                        preview = Encoding.ASCII.GetString(first, 0, Math.Min(first.Length, 180)),
                    });

                    if (LooksLikeHttp(first))
                    {
                        HandleHttpProbe(stream, peer, first);
                        return;
                    }

                    if (StartsWith(first, new byte[] { (byte)'X', (byte)'M', (byte)'L', 0x00 }))
                    {
                        _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "aplay-proto", peer = peer, action = "received", payload = "XML\\u0000" });
                    }

                    var buffer = new List<byte>(first);
                    var sentIntro = false;
                    var sentInit = false;
                    var sentAccountIntro = false;
                    var sentMetaGameplayIntro = false;
                    var sentRegularConnectReply = false;
                    var sentEnterCareerUpdate = false;
                    var sentHubIntro = false;
                    var sentMissionEntityIntros = false;
                    byte[] cachedHubStatePayload = null;
                    byte[] cachedCreationInfoPayload = null;

                    // Extra diagnostics for the post-character-creation stall.
                    long metaSendMessageSeen = 0;
                    long metaSetStoryMissionStateSeen = 0;
                    long metaStartSingleplayerMissionSeen = 0;
                    long metaRequestHubSeen = 0;
                    long postCreateArmGeneration = 0;

                    // Per-connection identity resolved from RequestToLogin(sessionHash, deviceModel, loginMethod).
                    // Enforced: no fallback to a global/default identity.
                    string activeIdentityHash = null;
                    Guid activeIdentityGuid = Guid.Empty;
                    var activeCareerIndex = 0;
                    var activeCharacterName = "OfflineRunner";
                    string currentHubInstanceId = null;
                    PortedHubInstance currentHubInstance = null;
                    const int HubReadyFallbackDelayMs = 2500;
                    var hubReadyFallbackLock = new object();
                    var hubReadyFallbackGeneration = 0;
                    string hubReadyFallbackHubId = null;
                    string hubReadyFallbackCharacterId = null;

                    Action<string> cancelHubReadyFallback = null;
                    Action<string, string, string> armHubReadyFallback = null;

                    cancelHubReadyFallback = delegate (string reason)
                    {
                        lock (hubReadyFallbackLock)
                        {
                            hubReadyFallbackGeneration++;
                            hubReadyFallbackHubId = null;
                            hubReadyFallbackCharacterId = null;
                        }

                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "hub-ready-fallback",
                            peer = peer,
                            status = "cancel",
                            reason = reason ?? string.Empty,
                        });
                    };

                    armHubReadyFallback = delegate (string hubId, string characterId, string reason)
                    {
                        if (IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
                        {
                            return;
                        }

                        if (IsHubPeerReady(peer, hubId))
                        {
                            return;
                        }

                        int generation;
                        lock (hubReadyFallbackLock)
                        {
                            hubReadyFallbackGeneration++;
                            generation = hubReadyFallbackGeneration;
                            hubReadyFallbackHubId = hubId;
                            hubReadyFallbackCharacterId = characterId;
                        }

                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "hub-ready-fallback",
                            peer = peer,
                            status = "arm",
                            reason = reason ?? string.Empty,
                            hubId = hubId,
                            characterId = characterId,
                            delayMs = HubReadyFallbackDelayMs,
                        });

                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            SleepWithStop(stopEvent, HubReadyFallbackDelayMs);
                            if (stopEvent.WaitOne(0) || connectionClosed.WaitOne(0))
                            {
                                return;
                            }

                            string pendingHubId;
                            string pendingCharacterId;
                            lock (hubReadyFallbackLock)
                            {
                                if (generation != hubReadyFallbackGeneration)
                                {
                                    return;
                                }

                                pendingHubId = hubReadyFallbackHubId;
                                pendingCharacterId = hubReadyFallbackCharacterId;
                            }

                            if (IsNullOrWhiteSpace(pendingHubId) || IsNullOrWhiteSpace(pendingCharacterId))
                            {
                                return;
                            }

                            var activated = TryActivateHubReadiness(peer, pendingHubId, "fallback-delay");
                            var total = activated
                                ? Interlocked.Increment(ref _hubReadyFallbackTriggeredTotal)
                                : Interlocked.Increment(ref _hubReadyFallbackSkippedTotal);

                            lock (hubReadyFallbackLock)
                            {
                                if (generation == hubReadyFallbackGeneration)
                                {
                                    hubReadyFallbackGeneration++;
                                    hubReadyFallbackHubId = null;
                                    hubReadyFallbackCharacterId = null;
                                }
                            }

                            _logger.Log(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "hub-ready-fallback",
                                peer = peer,
                                status = activated ? "triggered" : "skipped",
                                reason = "fallback-delay",
                                hubId = pendingHubId,
                                characterId = pendingCharacterId,
                                total = total,
                            });
                        });
                    };

                    // Track simple story progression locally so DirectStart missions don't loop forever.
                    // Keyed by map name (e.g., "1_010_Prologue").
                    var completedStoryMissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string currentMissionMapName = null;
                    string currentCoopGroupName = null;

                    ServerSimulationSession simulationSession = null;
                    object simulationSessionSync = null;

                    const ulong gameworldEntityId = 5;
                    const ulong missionInstanceEntityId = 6;
                    const ulong missionCommandEntityId = 7;

                    const ushort gameworldCommunicationObjectTypeId = 7;
                    const ushort missionInstanceCommunicationObjectTypeId = 9;
                    const ushort missionCommandCommunicationObjectTypeId = 10;

                    // Authoritative simulation session (created per mission) used to safely skip AI turns.
                    RegisterHubPeerStream(peer, stream);

                    while (!stopEvent.WaitOne(0))
                    {
                        byte[] frame;
                        while (TryExtractNullTerminatedFrame(buffer, out frame))
                        {
                            if (frame.Length == 0)
                            {
                                continue;
                            }

                            var asciiFrame = Encoding.ASCII.GetString(frame);
                            _logger.Log(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "aplay-frame",
                                peer = peer,
                                frame = asciiFrame,
                                rawHex = ToHexString(frame, 0, Math.Min(64, frame.Length)).ToLowerInvariant(),
                            });

                            if (asciiFrame == "XML")
                            {
                                continue;
                            }

                            byte[] decoded;
                            try
                            {
                                decoded = Convert.FromBase64String(asciiFrame);
                            }
                            catch
                            {
                                _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "aplay-frame-invalid-base64", peer = peer, frame = asciiFrame });
                                continue;
                            }

                            var decodedLog = new Dictionary<string, object>();
                            decodedLog["ts"] = RequestLogger.UtcNowIso();
                            decodedLog["type"] = "aplay-frame-decoded";
                            decodedLog["peer"] = peer;
                            decodedLog["bytes"] = decoded.Length;
                            decodedLog["decodedHex"] = ToHexString(decoded, 0, decoded.Length).ToLowerInvariant();
                            if (decoded.Length >= 5)
                            {
                                decodedLog["coreEnvelopeLen"] = ReadInt32LE(decoded, 0);
                                decodedLog["coreMessageType"] = decoded[4];
                            }
                            var utf16Preview = Encoding.Unicode.GetString(decoded).Trim('\0');
                            if (!IsNullOrWhiteSpace(utf16Preview))
                            {
                                decodedLog["utf16Preview"] = utf16Preview.Length > 200 ? utf16Preview.Substring(0, 200) : utf16Preview;
                            }
                            _logger.Log(decodedLog);

                            // Client "hello" is a Core.Client->Server message: RawData(len=1, payload={0x00}).
                            // That decodes to: int32(1) + uint8(0). Some clients may base64-encode it as "AQAAAAA=".
                            if (decoded.Length == 5 && ReadInt32LE(decoded, 0) == 1 && decoded[4] == 0)
                            {
                                var welcome = BuildCoreWelcomePayload(1UL, 1UL, 0UL);
                                SendRawFrame(stream, peer, welcome, "sent core welcome");
                            }

                            if (decoded.Length < 8)
                            {
                                continue;
                            }

                            var msgLen = ReadInt32LE(decoded, 0);
                            var corePayload = new byte[decoded.Length - 4];
                            Buffer.BlockCopy(decoded, 4, corePayload, 0, corePayload.Length);
                            if (msgLen != corePayload.Length)
                            {
                                continue;
                            }

                            if (StartsWith(corePayload, CoreHelloPayloadPrefix))
                            {
                                if (!sentIntro)
                                {
                                    SendRawFrame(stream, peer, PrefixLength(gameClientIntroducePayload), "sent AP introduce shared entity (type=5 game client connection)");
                                    sentIntro = true;
                                }

                                if (!sentInit)
                                {
                                    // IMPORTANT: entityId in Initialized must match the player's entity id.
                                    SendRawFrame(stream, peer, PrefixLength(apInitializedPayload), "sent AP initialized in response to AP hello (entityId=" + gameClientEntityId + ")");
                                    sentInit = true;
                                }
                            }

                            if (corePayload.Length == 0 || corePayload[0] != 3)
                            {
                                continue;
                            }

                            var direct = ParseCoreDirectSystem(corePayload);
                            if (!direct.HasValue)
                            {
                                continue;
                            }

                            var shared = ParseApSharedFieldEvent(direct.Value.Raw);
                            if (!shared.HasValue)
                            {
                                continue;
                            }

                            List<string> payloadStrings;
                            try
                            {
                                payloadStrings = ParseUtf16StringPayload(shared.Value.Data);
                            }
                            catch (Exception ex)
                            {
                                payloadStrings = new List<string>();
                                _logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "aplay-utf16-payload-parse-failed",
                                    peer = peer,
                                    message = ex.Message,
                                });
                            }

                            // Some APlay calls (notably MetaGameplayCommunicationObject.ChangeCharacter / callField(6))
                            // use a WString encoding that isn't our simple int32-length-prefixed list. If we couldn't
                            // parse any strings, fall back to scanning for a UTF-16 JSON object within the payload.
                            if (payloadStrings.Count == 0)
                            {
                                var extracted = TryExtractUtf16JsonObject(shared.Value.Data);
                                if (!IsNullOrWhiteSpace(extracted))
                                {
                                    payloadStrings.Add(extracted);
                                }
                            }

                            string prepareMatchIdentifier = null;
                            string prepareMatchPlayers = null;
                            bool prepareMatchCoop = false;
                            string prepareMatchMapName = null;
                            string prepareMatchSelectedHenchmen = null;
                            var hasPrepareMatchPayload = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 4
                                && TryParsePrepareMatchPayload(
                                    shared.Value.Data,
                                    out prepareMatchIdentifier,
                                    out prepareMatchPlayers,
                                    out prepareMatchCoop,
                                    out prepareMatchMapName,
                                    out prepareMatchSelectedHenchmen);

                            if (hasPrepareMatchPayload)
                            {
                                payloadStrings = new List<string>(4)
                                {
                                    prepareMatchIdentifier ?? string.Empty,
                                    prepareMatchPlayers ?? string.Empty,
                                    prepareMatchMapName ?? string.Empty,
                                    prepareMatchSelectedHenchmen ?? string.Empty,
                                };

                                var selectedPreview = prepareMatchSelectedHenchmen ?? string.Empty;
                                if (selectedPreview.Length > 320)
                                {
                                    selectedPreview = selectedPreview.Substring(0, 320);
                                }

                                _logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "aplay-preparematch-decoded",
                                    peer = peer,
                                    matchIdentifier = prepareMatchIdentifier,
                                    coop = prepareMatchCoop,
                                    mapName = prepareMatchMapName,
                                    selectedHenchmenLength = prepareMatchSelectedHenchmen != null ? prepareMatchSelectedHenchmen.Length : 0,
                                    selectedHenchmenPreview = selectedPreview,
                                });
                            }

                            var isHubEntityCall = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == HubEntityId;
                            if (isHubEntityCall)
                            {
                                var payloadPreview = payloadStrings.Count > 0 && payloadStrings[0] != null
                                    ? payloadStrings[0]
                                    : string.Empty;
                                if (payloadPreview.Length > 320)
                                {
                                    payloadPreview = payloadPreview.Substring(0, 320);
                                }

                                _logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "hub-entity-call",
                                    peer = peer,
                                    apMsgId = shared.Value.ApMsgId,
                                    entityId = shared.Value.EntityId,
                                    fieldId = shared.Value.FieldId,
                                    dataBytes = shared.Value.Data != null ? shared.Value.Data.Length : 0,
                                    payloadCount = payloadStrings.Count,
                                    payloadPreview = payloadPreview,
                                });

                                if (shared.Value.FieldId == 1 || shared.Value.FieldId == 2)
                                {
                                    var hubPos = 0;
                                    string movedCharacterId;
                                    if (TryReadUtf16LengthPrefixedString(shared.Value.Data, ref hubPos, out movedCharacterId))
                                    {
                                        int rawX;
                                        int rawY;
                                        if (TryReadInt32LE(shared.Value.Data, ref hubPos, out rawX)
                                            && TryReadInt32LE(shared.Value.Data, ref hubPos, out rawY))
                                        {
                                            var movedX = BitConverter.ToSingle(BitConverter.GetBytes(rawX), 0);
                                            var movedY = BitConverter.ToSingle(BitConverter.GetBytes(rawY), 0);

                                            if (shared.Value.FieldId == 1)
                                            {
                                                _hubPresenceRegistry.UpdatePosition(peer, movedX, movedY);

                                                string movementHubId;
                                                if (!_hubPresenceRegistry.TryGetHubIdForPeer(peer, out movementHubId))
                                                {
                                                    movementHubId = currentHubInstanceId;
                                                }

                                                HubPresenceRegistry.Participant movementParticipant;
                                                _hubPresenceRegistry.TryGetParticipantForPeer(peer, out movementParticipant);

                                                if (!IsHubMoveOwnershipValid(peer, activeIdentityGuid, movedCharacterId, movementParticipant))
                                                {
                                                    continue;
                                                }

                                                if (movementParticipant != null
                                                    && !IsNullOrWhiteSpace(movedCharacterId)
                                                    && !string.Equals(movementParticipant.CharacterId, movedCharacterId, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    var previousCharacterId = movementParticipant.CharacterId;
                                                    var updatedHubId = !IsNullOrWhiteSpace(movementParticipant.HubId)
                                                        ? movementParticipant.HubId
                                                        : movementHubId;

                                                    RegisterOrUpdateHubPresenceWithDuplicateRetire(
                                                        peer,
                                                        movementParticipant.AccountId,
                                                        movementParticipant.IdentityHash,
                                                        movementParticipant.CareerIndex,
                                                        movedCharacterId,
                                                        movementParticipant.CharacterName,
                                                        updatedHubId,
                                                        movedX,
                                                        movedY,
                                                        "hub-move-character-shift");

                                                    if (!IsNullOrWhiteSpace(updatedHubId) && !IsNullOrWhiteSpace(movementParticipant.CharacterId))
                                                    {
                                                        BroadcastHubStateRemove(updatedHubId, peer, movementParticipant.CharacterId);
                                                    }

                                                    HubPresenceRegistry.Participant shiftedParticipant;
                                                    _hubPresenceRegistry.TryGetParticipantForPeer(peer, out shiftedParticipant);
                                                    if (shiftedParticipant != null && !IsNullOrWhiteSpace(shiftedParticipant.HubId))
                                                    {
                                                        ClearHubAnnouncementForAllPeers(shiftedParticipant.HubId, shiftedParticipant.CharacterId);
                                                        BroadcastHubStateAddToReadyPeers(shiftedParticipant.HubId, peer, shiftedParticipant, "character-shift");
                                                    }

                                                    _logger.Log(new
                                                    {
                                                        ts = RequestLogger.UtcNowIso(),
                                                        type = "hub-characterid-shift",
                                                        peer = peer,
                                                        oldCharacterId = movementParticipant.CharacterId ?? string.Empty,
                                                        newCharacterId = movedCharacterId,
                                                        hubId = updatedHubId ?? string.Empty,
                                                    });

                                                    movementHubId = updatedHubId;
                                                    movementParticipant = shiftedParticipant;

                                                    if (currentHubInstance != null && !IsNullOrWhiteSpace(movementParticipant != null ? movementParticipant.CharacterId : null))
                                                    {
                                                        CareerSlot shiftedSlot = null;
                                                        if (_userStore != null && !IsNullOrWhiteSpace(activeIdentityHash))
                                                        {
                                                            try
                                                            {
                                                                shiftedSlot = _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false);
                                                            }
                                                            catch
                                                            {
                                                                shiftedSlot = null;
                                                            }
                                                        }

                                                        var shiftedSnapshot = BuildMappedPlayerCharacterSnapshotForHub(
                                                            activeIdentityGuid,
                                                            movedCharacterId,
                                                            movementParticipant != null ? movementParticipant.CharacterName : activeCharacterName,
                                                            shiftedSlot);
                                                        _portedHubInstanceManager.RemoveCharacterFromHub(currentHubInstance, previousCharacterId);
                                                        if (shiftedSnapshot != null)
                                                        {
                                                            currentHubInstance.AddCharacter(shiftedSnapshot);
                                                        }
                                                    }
                                                }

                                                var movementCharacterId = movedCharacterId;
                                                if (IsNullOrWhiteSpace(movementCharacterId))
                                                {
                                                    if (movementParticipant != null)
                                                    {
                                                        movementCharacterId = movementParticipant.CharacterId;
                                                    }
                                                }

                                                if (currentHubInstance == null && !IsNullOrWhiteSpace(movementHubId) && _portedHubInstanceManager != null)
                                                {
                                                    currentHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(movementHubId);
                                                }

                                                if (currentHubInstance != null && !IsNullOrWhiteSpace(movementCharacterId))
                                                {
                                                    currentHubInstance.QueueMoveRequest(movementCharacterId, new Vector2D(movedX, movedY));
                                                    currentHubInstanceId = currentHubInstance.HubId;
                                                }

                                                var firstMoveReadyActivated = TryActivateHubReadiness(peer, movementHubId, "first-move");
                                                if (firstMoveReadyActivated)
                                                {
                                                    cancelHubReadyFallback("first-move");
                                                }

                                                BroadcastHubMovement(movementHubId, peer, movementCharacterId, movedX, movedY);
                                            }
                                            else
                                            {
                                                HubPresenceRegistry.Participant previousParticipant;
                                                _hubPresenceRegistry.TryGetParticipantForPeer(peer, out previousParticipant);

                                                var hubIdForPresence = currentHubInstanceId;
                                                if (IsNullOrWhiteSpace(hubIdForPresence))
                                                {
                                                    _hubPresenceRegistry.TryGetHubIdForPeer(peer, out hubIdForPresence);
                                                }
                                                if (IsNullOrWhiteSpace(hubIdForPresence) && _userStore != null && !IsNullOrWhiteSpace(activeIdentityHash))
                                                {
                                                    try
                                                    {
                                                        var slot = _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false);
                                                        if (slot != null && !IsNullOrWhiteSpace(slot.HubId))
                                                        {
                                                            hubIdForPresence = slot.HubId;
                                                        }
                                                    }
                                                    catch
                                                    {
                                                    }
                                                }

                                                var characterIdForPresence = !IsNullOrWhiteSpace(movedCharacterId)
                                                    ? movedCharacterId
                                                    : ResolveHubCharacterIdentifier(null, activeIdentityGuid, activeCareerIndex, previousParticipant != null ? previousParticipant.CharacterId : null);

                                                RegisterOrUpdateHubPresenceWithDuplicateRetire(
                                                    peer,
                                                    activeIdentityGuid,
                                                    activeIdentityHash,
                                                    activeCareerIndex,
                                                    characterIdForPresence,
                                                    activeCharacterName,
                                                    hubIdForPresence,
                                                    movedX,
                                                    movedY,
                                                    "hub-enter-field-2");

                                                if (!IsNullOrWhiteSpace(hubIdForPresence))
                                                {
                                                    currentHubInstanceId = hubIdForPresence;
                                                    if (_portedHubInstanceManager != null)
                                                    {
                                                        currentHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(currentHubInstanceId);
                                                    }
                                                }

                                                HubPresenceRegistry.Participant currentParticipant;
                                                _hubPresenceRegistry.TryGetParticipantForPeer(peer, out currentParticipant);

                                                if (previousParticipant != null
                                                    && !IsNullOrWhiteSpace(previousParticipant.HubId)
                                                    && !IsNullOrWhiteSpace(previousParticipant.CharacterId)
                                                    && !string.Equals(previousParticipant.HubId, hubIdForPresence, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    BroadcastHubStateRemove(previousParticipant.HubId, peer, previousParticipant.CharacterId);
                                                }

                                                if (currentParticipant != null && !IsNullOrWhiteSpace(currentParticipant.HubId))
                                                {
                                                    var shouldBroadcastAdd = previousParticipant == null
                                                        || !string.Equals(previousParticipant.HubId, currentParticipant.HubId, StringComparison.OrdinalIgnoreCase)
                                                        || !string.Equals(previousParticipant.CharacterId, currentParticipant.CharacterId, StringComparison.OrdinalIgnoreCase);

                                                    if (shouldBroadcastAdd)
                                                    {
                                                        ClearHubAnnouncementsForPeerHub(peer, currentParticipant.HubId);
                                                    }

                                                    _logger.Log(new
                                                    {
                                                        ts = RequestLogger.UtcNowIso(),
                                                        type = "hub-enter",
                                                        peer = peer,
                                                        hubId = currentParticipant.HubId,
                                                        characterId = currentParticipant.CharacterId ?? string.Empty,
                                                        x = movedX,
                                                        y = movedY,
                                                        previousHubId = previousParticipant != null ? (previousParticipant.HubId ?? string.Empty) : string.Empty,
                                                        previousCharacterId = previousParticipant != null ? (previousParticipant.CharacterId ?? string.Empty) : string.Empty,
                                                        hubChanged = previousParticipant == null || !string.Equals(previousParticipant.HubId, currentParticipant.HubId, StringComparison.OrdinalIgnoreCase),
                                                    });

                                                    armHubReadyFallback(currentParticipant.HubId, currentParticipant.CharacterId, "hub-enter-field-2");
                                                }
                                            }
                                        }
                                    }
                                }
                                else if (shared.Value.FieldId == 3)
                                {
                                    cancelHubReadyFallback("hub-leave-field-3");
                                    HubPresenceRegistry.Participant leavingParticipant;
                                    _hubPresenceRegistry.TryGetParticipantForPeer(peer, out leavingParticipant);
                                    if (currentHubInstance != null && leavingParticipant != null && !IsNullOrWhiteSpace(leavingParticipant.CharacterId))
                                    {
                                        _portedHubInstanceManager.RemoveCharacterFromHub(currentHubInstance, leavingParticipant.CharacterId);
                                        currentHubInstance = null;
                                    }
                                    RemoveHubPresenceWithBroadcast(peer);
                                }
                            }

                            var isRegularConnect = shared.Value.ApMsgId == 1
                                && shared.Value.FieldId == 3
                                && PayloadContains(payloadStrings, "RegularConnect");

                            if (isRegularConnect && !sentRegularConnectReply)
                            {
                                var serverMsgNoBase = direct.Value.MsgNo;

                                // In theory, the client should call RequestToLogin on the entity id we introduced.
                                // In practice, if our introduce payload doesn't get applied as expected, it may keep
                                // using a different (often small) entity id. Adopt the id the client is actually using
                                // so subsequent Welcome/KeepAlive traffic targets the correct shared entity.
                                if (shared.Value.EntityId != 0UL && shared.Value.EntityId != gameClientEntityId)
                                {
                                    _logger.Log(new
                                    {
                                        ts = RequestLogger.UtcNowIso(),
                                        type = "aplay-gameclient-entityid-adopted",
                                        peer = peer,
                                        previousEntityId = gameClientEntityId,
                                        adoptedEntityId = shared.Value.EntityId,
                                    });

                                    gameClientEntityId = shared.Value.EntityId;
                                }

                                // RequestToLogin(sessionHash, deviceModel, loginMethod). The loginMethod is typically "RegularConnect".
                                // Enforced: the session hash must map to a known identity (minted via Steam/Authenticate).
                                var requestedSessionHash = payloadStrings.Count > 0 ? payloadStrings[0] : null;
                                string mappedIdentityHash = null;
                                Guid mappedIdentityGuid = Guid.Empty;
                                string rejectReason = null;

                                if (IsNullOrWhiteSpace(requestedSessionHash))
                                {
                                    rejectReason = "Missing session hash.";
                                }
                                else
                                {
                                    try
                                    {
                                        // Validate the session hash is a GUID (matches AccountSystem SessionHash).
                                        var _ = new Guid(requestedSessionHash);

                                        // Resolve identity from shared session map (preferred) or persisted user store sessions.
                                        if (_sessionIdentityMap != null && _sessionIdentityMap.TryGetIdentityForSession(requestedSessionHash, out mappedIdentityHash) && !IsNullOrWhiteSpace(mappedIdentityHash))
                                        {
                                            // ok
                                        }
                                        else if (_userStore != null && _userStore.TryGetIdentityForSession(requestedSessionHash, out mappedIdentityHash) && !IsNullOrWhiteSpace(mappedIdentityHash))
                                        {
                                            // ok
                                        }
                                        else
                                        {
                                            rejectReason = "Unknown session hash (no mapped identity).";
                                        }

                                        if (rejectReason == null)
                                        {
                                            try { mappedIdentityGuid = new Guid(mappedIdentityHash); }
                                            catch { rejectReason = "Mapped identity hash is invalid."; }
                                        }
                                    }
                                    catch
                                    {
                                        rejectReason = "Invalid session hash.";
                                    }
                                }

                                SleepWithStop(stopEvent, 250);

                                if (!sentAccountIntro)
                                {
                                    var accountIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes(AccountEntityId), BitConverter.GetBytes((ushort)3), BitConverter.GetBytes(0));
                                    var accountIntroCore = BuildCoreDirectSystem(1, accountIntroRaw, serverMsgNoBase + 1);
                                    SendRawFrame(stream, peer, PrefixLength(accountIntroCore), "sent AP introduce shared entity (type=3 account communication object, id=" + AccountEntityId + ")");
                                    sentAccountIntro = true;
                                }

                                var gameClientOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(gameClientEntityId, GameClientConnectionTypeId), serverMsgNoBase + 2);
                                SendRawFrame(stream, peer, PrefixLength(gameClientOwnerCore), "sent AP shared-entity set-owner (entity=" + gameClientEntityId + ")");

                                var accountOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(AccountEntityId, 3), serverMsgNoBase + 3);
                                SendRawFrame(stream, peer, PrefixLength(accountOwnerCore), "sent AP shared-entity set-owner (entity=" + AccountEntityId + ")");

                                if (rejectReason != null)
                                {
                                    var rejectPayload = BuildUtf16StringPayload(rejectReason);
                                    var rejectCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameClientEntityId, 5, rejectPayload), serverMsgNoBase + 4);
                                    SendRawFrame(stream, peer, PrefixLength(rejectCore), "sent GameClientConnection RejectLogin in response to RegularConnect");
                                    connectionClosed.Set();
                                    return;
                                }

                                activeIdentityHash = mappedIdentityHash;
                                activeIdentityGuid = mappedIdentityGuid;
                                RegisterGameClientEntityIdForIdentity(activeIdentityGuid, gameClientEntityId, peer);

                                var careerSummary = BuildCareerSummaryJson(_userStore != null ? _userStore.GetCareers(activeIdentityHash) : null);
                                var welcomePayload = BuildGameClientWelcomePayload(AccountEntityId, careerSummary);
                                var welcomeCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameClientEntityId, 4, welcomePayload), serverMsgNoBase + 4);
                                SendRawFrame(stream, peer, PrefixLength(welcomeCore), "sent GameClientConnection Welcome in response to RegularConnect");
                                sentRegularConnectReply = true;

                                var keepAliveCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameClientEntityId, 6, new byte[0]), serverMsgNoBase + 5);
                                SendRawFrame(stream, peer, PrefixLength(keepAliveCore), "sent GameClientConnection KeepAlive after Welcome");

                                // The client expects periodic keep-alives; otherwise it may drop the socket shortly after
                                // entering an idle state in the hub ("you have been disconnected").
                                if (!keepAliveLoopStarted)
                                {
                                    keepAliveLoopStarted = true;
                                    ThreadPool.QueueUserWorkItem(delegate
                                    {
                                        while (!stopEvent.WaitOne(0) && !connectionClosed.WaitOne(0))
                                        {
                                            SleepWithStop(stopEvent, 2000);
                                            if (stopEvent.WaitOne(0) || connectionClosed.WaitOne(0))
                                            {
                                                break;
                                            }

                                            try
                                            {
                                                var msgNo = unchecked((ulong)Interlocked.Increment(ref keepAliveMsgNo));
                                                var periodicKeepAliveCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameClientEntityId, 6, new byte[0]), msgNo);
                                                SendRawFrame(stream, peer, PrefixLength(periodicKeepAliveCore), "sent GameClientConnection KeepAlive (periodic)");
                                            }
                                            catch
                                            {
                                                connectionClosed.Set();
                                                break;
                                            }
                                        }
                                    });
                                }
                            }

                            var isCareerBootstrapRequest = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 2
                                && (shared.Value.FieldId == 10 || shared.Value.FieldId == 11);

                            var isLeaveCurrentCareer = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 2
                                && shared.Value.FieldId == 12;

                            var isDeactivateCareer = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 2
                                && shared.Value.FieldId == 13;

                            var isCreateCareer = shared.Value.FieldId == 10;

                            if (isCareerBootstrapRequest && !sentEnterCareerUpdate)
                            {
                                int? requestedIndex = ParseInt32Payload(shared.Value.Data);
                                var careerIndex = requestedIndex.HasValue ? requestedIndex.Value : 0;
                                if (careerIndex < 0)
                                {
                                    careerIndex = 0;
                                }

                                var serverMsgNoBase = direct.Value.MsgNo + 9;

                                if (!sentMetaGameplayIntro)
                                {
                                    var metaGameplayIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes((ulong)3), BitConverter.GetBytes((ushort)8), BitConverter.GetBytes(0));
                                    var metaGameplayIntroCore = BuildCoreDirectSystem(1, metaGameplayIntroRaw, serverMsgNoBase);
                                    SendRawFrame(stream, peer, PrefixLength(metaGameplayIntroCore), "sent AP introduce shared entity (type=8 meta gameplay communication object, id=3)");

                                    var metaGameplayOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(3, 8), serverMsgNoBase + 1);
                                    SendRawFrame(stream, peer, PrefixLength(metaGameplayOwnerCore), "sent AP shared-entity set-owner (entity=3)");

                                    sentMetaGameplayIntro = true;
                                }

                                if (!sentHubIntro)
                                {
                                    var hubIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes((ulong)4), BitConverter.GetBytes((ushort)11), BitConverter.GetBytes(0));
                                    var hubIntroCore = BuildCoreDirectSystem(1, hubIntroRaw, serverMsgNoBase + 2);
                                    SendRawFrame(stream, peer, PrefixLength(hubIntroCore), "sent AP introduce shared entity (type=11 hub communication object, id=4)");

                                    var hubOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(4, 11), serverMsgNoBase + 3);
                                    SendRawFrame(stream, peer, PrefixLength(hubOwnerCore), "sent AP shared-entity set-owner (entity=4)");

                                    sentHubIntro = true;
                                }

                                // Enforced: identity must have been established during RegularConnect (RequestToLogin).
                                if (IsNullOrWhiteSpace(activeIdentityHash) || activeIdentityGuid == Guid.Empty)
                                {
                                    var rejectPayload = BuildUtf16StringPayload("Not logged in.");
                                    var rejectCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameClientEntityId, 5, rejectPayload), serverMsgNoBase + 4);
                                    SendRawFrame(stream, peer, PrefixLength(rejectCore), "sent GameClientConnection RejectLogin (EnterCareer before login)");
                                    connectionClosed.Set();
                                    return;
                                }

                                var identityHash = activeIdentityHash;
                                var identityGuid = activeIdentityGuid;

                                CareerSlot slot = null;
                                if (_userStore != null)
                                {
                                    slot = _userStore.GetOrCreateCareer(identityHash, careerIndex, isCreateCareer);

                                    // EnterCareer on an empty slot should still materialize a usable career.
                                    if (!slot.IsOccupied && !isCreateCareer)
                                    {
                                        slot.IsOccupied = true;
                                        if (IsNullOrWhiteSpace(slot.CharacterName))
                                        {
                                            slot.CharacterName = "OfflineRunner";
                                        }
                                    }

                                    // After selecting/entering a career, treat it as committed (not pending creation).
                                    if (!isCreateCareer && slot.PendingPersistenceCreation)
                                    {
                                        slot.PendingPersistenceCreation = false;
                                    }

                                    _userStore.UpsertCareer(identityHash, slot);

                                    // Track last selected slot so HTTP PlayerActivity updates can attribute character name.
                                    _userStore.SetLastCareerIndex(identityHash, careerIndex);
                                }

                                var characterName = slot != null && !IsNullOrWhiteSpace(slot.CharacterName)
                                    ? slot.CharacterName
                                    : (isCreateCareer ? "NewRunner" : "OfflineRunner");

                                activeCareerIndex = careerIndex;
                                activeCharacterName = characterName;

                                // Seed per-connection story tracking from persisted career state so mandatory missions
                                // (especially the prologue) don't restart after a LocalService/game relaunch.
                                completedStoryMissions.Clear();
                                if (slot != null && slot.MainCampaignMissionStates != null)
                                {
                                    foreach (var kvp in slot.MainCampaignMissionStates)
                                    {
                                        if (IsNullOrWhiteSpace(kvp.Key) || IsNullOrWhiteSpace(kvp.Value))
                                        {
                                            continue;
                                        }
                                        if (string.Equals(kvp.Value, "Completed", StringComparison.OrdinalIgnoreCase))
                                        {
                                            completedStoryMissions.Add(kvp.Key);
                                        }
                                    }
                                }

                                var pendingCreation = slot != null ? slot.PendingPersistenceCreation : isCreateCareer;
                                var zippedCareerInfo = slot != null
                                    ? _careerInfoGenerator.GetZippedCareerInfo(identityGuid, careerIndex, slot)
                                    : _careerInfoGenerator.GetZippedCareerInfo(identityGuid, careerIndex, characterName, pendingCreation);

                                var accountWelcomePayload = BuildAccountWelcomePayload(careerIndex, zippedCareerInfo, 3);
                                var accountWelcomeCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 2, 14, accountWelcomePayload), serverMsgNoBase + 4);
                                SendRawFrame(stream, peer, PrefixLength(accountWelcomeCore), "sent AccountCommunicationObject Welcome after EnterCareer");

                                var updatePayload = BuildUtf16StringPayload(BuildCareerSummaryJson(_userStore != null ? _userStore.GetCareers(identityHash) : null));
                                var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 2, 16, updatePayload), serverMsgNoBase + 5);
                                SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries after EnterCareer");

                                var metaSnapshotPayload = BuildUtf16StringPayload(zippedCareerInfo);
                                var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), serverMsgNoBase + 6);
                                SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient");

                                var henchmanCollectionPayload = BuildUtf16StringPayload(SerializeDefaultHenchmanCollection());
                                var henchmanCollectionCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 27, henchmanCollectionPayload), serverMsgNoBase + 7);
                                SendRawFrame(stream, peer, PrefixLength(henchmanCollectionCore), "sent MetaGameplayCommunicationObject SendHenchmanCollectionToClient");

                                var characterIdentifier = slot != null && !IsNullOrWhiteSpace(slot.CharacterIdentifier)
                                    ? slot.CharacterIdentifier
                                    : (identityGuid.ToString() + ":" + careerIndex.ToString());
                                var hubId = slot != null && !IsNullOrWhiteSpace(slot.HubId) ? slot.HubId : DefaultHubId;

                                var hubStatePayload = BuildPortedHubStatePayloadForSlot(
                                    slot,
                                    identityGuid,
                                    careerIndex,
                                    false,
                                    currentHubInstance,
                                    out currentHubInstanceId,
                                    out currentHubInstance);
                                cachedHubStatePayload = hubStatePayload;
                                RegisterOrUpdateHubPresenceWithDuplicateRetire(
                                    peer,
                                    activeIdentityGuid,
                                    activeIdentityHash,
                                    activeCareerIndex,
                                    characterIdentifier,
                                    activeCharacterName,
                                    currentHubInstanceId,
                                    0f,
                                    0f,
                                    "career-enter-bootstrap");
                                armHubReadyFallback(currentHubInstanceId, characterIdentifier, "career-enter-bootstrap");
                                // IMPORTANT: Don't push the hub instance unsolicited here.
                                // The client can receive it before the metagameplay UI exists and/or before a hub request
                                // is queued; in that case LocalHubInstanceController will ignore it. Instead we respond to
                                // explicit hub requests (MetaGameplay entity=3 field 1/2).

                                var creationInfoJson = "{\"PendingPersistenceCreation\":" + (pendingCreation ? "true" : "false") + ",\"DataVersionChanged\":false}";
                                var creationInfoPayload = BuildUtf16StringPayload(creationInfoJson);
                                cachedCreationInfoPayload = creationInfoPayload;
                                var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, creationInfoPayload), serverMsgNoBase + 9);
                                if (!ShouldSuppressDuplicateHubPush(peer, true, creationInfoPayload))
                                {
                                    SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged");
                                }

                                // Fire-and-forget: don’t block the main socket read loop with sleeps.
                                // Blocking here can delay processing of the client’s immediate next APlay calls
                                // (notably MetaGameplay field 6 ChangeCharacter).
                                ThreadPool.QueueUserWorkItem(delegate
                                {
                                    for (var resendAttempt = 1; resendAttempt <= 3; resendAttempt++)
                                    {
                                        if (stopEvent.WaitOne(0) || connectionClosed.WaitOne(0))
                                        {
                                            break;
                                        }

                                        SleepWithStop(stopEvent, 2000);
                                        if (stopEvent.WaitOne(0) || connectionClosed.WaitOne(0))
                                        {
                                            break;
                                        }

                                        // Once the client is actively requesting hub state, creation-info resends become noise and can race UI reload.
                                        if (Interlocked.Read(ref metaRequestHubSeen) > 0)
                                        {
                                            break;
                                        }

                                        try
                                        {
                                            var delayedMsgNo = serverMsgNoBase + 9UL + (ulong)(resendAttempt * 2);
                                            var delayedCreationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, creationInfoPayload), delayedMsgNo);
                                            if (!ShouldSuppressDuplicateHubPush(peer, true, creationInfoPayload))
                                            {
                                                SendRawFrame(stream, peer, PrefixLength(delayedCreationInfoCore), "resent MetaGameplayCommunicationObject CreationInfoChanged (delayed attempt " + resendAttempt + ")");
                                            }
                                        }
                                        catch
                                        {
                                            break;
                                        }
                                    }
                                });

                                sentEnterCareerUpdate = true;
                            }

                            if (isLeaveCurrentCareer)
                            {
                                cancelHubReadyFallback("leave-current-career");
                                RemoveHubPresenceWithBroadcast(peer);
                                currentHubInstanceId = null;

                                // Minimal behavior: acknowledge by re-sending current career summaries.
                                // This keeps the client UI in sync without needing a full career-state machine.
                                var msgNoBase = direct.Value.MsgNo + 20;
                                var updatePayload = BuildUtf16StringPayload(BuildCareerSummaryJson(_userStore != null && !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetCareers(activeIdentityHash) : null));
                                var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 2, 16, updatePayload), msgNoBase + 1);
                                SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries after LeaveCurrentCareer");

                                // The client can leave one career and then enter another without reconnecting.
                                // If we keep sentEnterCareerUpdate=true, we will ignore the next career bootstrap
                                // request (field 10/11) and the UI will hang on the loading screen.
                                sentEnterCareerUpdate = false;
                            }

                            if (isDeactivateCareer)
                            {
                                // Client requests deleting/deactivating a career slot.
                                // shared.Value.Data is expected to be int32 index.
                                var idx = ParseInt32Payload(shared.Value.Data);
                                var slotIndex = idx.HasValue ? idx.Value : 0;
                                if (slotIndex < 0)
                                {
                                    slotIndex = 0;
                                }

                                if (_userStore != null)
                                {
                                    if (!IsNullOrWhiteSpace(activeIdentityHash))
                                    {
                                        _userStore.DeactivateCareerSlot(activeIdentityHash, slotIndex, DefaultHubId);
                                    }
                                }

                                if (activeCareerIndex == slotIndex)
                                {
                                    activeCareerIndex = 0;
                                    activeCharacterName = "OfflineRunner";
                                }

                                var msgNoBase = direct.Value.MsgNo + 30;
                                var careers = _userStore != null && !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetCareers(activeIdentityHash) : null;
                                var summaryJson = BuildCareerSummaryJson(careers);

                                // IMPORTANT: CareerSelectionViewModel cancels the wait dialog only on CareerDeactivated.
                                // That is AccountCommunicationObject field 15, not UpdateCareerSummaries.
                                var careerDeactivatedPayload = BuildUtf16StringPayload(summaryJson);
                                var careerDeactivatedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 2, 15, careerDeactivatedPayload), msgNoBase + 1);
                                SendRawFrame(stream, peer, PrefixLength(careerDeactivatedCore), "sent AccountCommunicationObject CareerDeactivated after DeactivateCareer");

                                var updatePayload = BuildUtf16StringPayload(summaryJson);
                                var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 2, 16, updatePayload), msgNoBase + 2);
                                SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries after DeactivateCareer");

                                // Also nudge metagame creation-info to non-pending if we have an active payload cached.
                                if (cachedCreationInfoPayload != null)
                                {
                                    var creationInfoJson = "{\"PendingPersistenceCreation\":false,\"DataVersionChanged\":false}";
                                    cachedCreationInfoPayload = BuildUtf16StringPayload(creationInfoJson);
                                    var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, cachedCreationInfoPayload), msgNoBase + 2);
                                    SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged after DeactivateCareer");
                                }
                            }

                            // MetaGameplayCommunicationObject callFields with no payload.
                            // See client __MetaGameplayCommunicationObject.cs:
                            // - field 5: GetMetagameplayDataSnapshot
                            // - field 10: GetHenchmanCollection
                            var isGetMetagameplayDataSnapshot = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 5;
                            if (isGetMetagameplayDataSnapshot)
                            {
                                // Keep this lightweight: just re-send the latest snapshot for the active slot.
                                var requestMsgNoBase = direct.Value.MsgNo + 90;
                                try
                                {
                                    CareerSlot slot = null;
                                    if (_userStore != null)
                                    {
                                        if (!IsNullOrWhiteSpace(activeIdentityHash))
                                        {
                                            slot = _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false);
                                        }
                                    }

                                    var zippedCareerInfo = (slot != null)
                                        ? _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, activeCareerIndex, slot)
                                        : _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, activeCareerIndex, activeCharacterName, false);

                                    var metaSnapshotPayload = BuildUtf16StringPayload(zippedCareerInfo);
                                    var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), requestMsgNoBase + 1);
                                    SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient in response to GetMetagameplayDataSnapshot");
                                }
                                catch
                                {
                                }
                            }

                            var isGetHenchmanCollection = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 10;
                            if (isGetHenchmanCollection)
                            {
                                var requestMsgNoBase = direct.Value.MsgNo + 100;
                                try
                                {
                                    var henchmanCollectionPayload = BuildUtf16StringPayload(SerializeDefaultHenchmanCollection());
                                    var henchmanCollectionCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 27, henchmanCollectionPayload), requestMsgNoBase + 1);
                                    SendRawFrame(stream, peer, PrefixLength(henchmanCollectionCore), "sent MetaGameplayCommunicationObject SendHenchmanCollectionToClient in response to GetHenchmanCollection");
                                }
                                catch
                                {
                                }
                            }

                            var isMetaGameplayMessage = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && (shared.Value.FieldId == 1 || shared.Value.FieldId == 6 || shared.Value.FieldId == 7)
                                && payloadStrings.Count > 0;

                            var isMetaGameplayWrappedMessage = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 1
                                && payloadStrings.Count > 0;

                            // The character editor sends CharacterChangeCollection through MetaGameplayCommunicationObject.ChangeCharacter(...)
                            // which goes out on APlay field 6 (see client __MetaGameplayCommunicationObject.ChangeCharacter -> entity.callField(6)).
                            var isMetaGameplayChangeCharacter = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 6
                                && payloadStrings.Count > 0;

                            // Skill/talent purchases are sent via MetaGameplayCommunicationObject.ChangeSkillTrees(...)
                            // which goes out on APlay field 7 (see client __MetaGameplayCommunicationObject.ChangeSkillTrees -> entity.callField(7)).
                            var isMetaGameplayChangeSkillTrees = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 7
                                && payloadStrings.Count > 0;

                            // Hub shop purchases/sales are sent via MetaGameplayCommunicationObject.ChangeItemPosessions(...)
                            // which goes out on APlay field 8 (see client DesignedClient.cs field 8).
                            var isMetaGameplayChangeItemPosessions = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 8
                                && payloadStrings.Count > 0;

                            // Coop mission start (group-based) does not come through MetaGameplayCommunicationObject.Message.
                            // Instead, the client sends a UTF-16 string payload like:
                            //   CoopGroup<guid>_On_<mapName>S, ["<account>:0", ...], <mapName>, []
                            // If we don't respond with StartMissionAccepted/StartMissionForClients, the UI will sit at
                            // "Waiting for Game Server..." forever.
                            var coopIdentifier = hasPrepareMatchPayload
                                ? prepareMatchIdentifier
                                : (payloadStrings.Count > 0 ? payloadStrings[0] : null);
                            var isCoopMissionStart = !IsNullOrWhiteSpace(coopIdentifier)
                                && coopIdentifier.StartsWith("CoopGroup", StringComparison.OrdinalIgnoreCase)
                                && coopIdentifier.IndexOf("_On_", StringComparison.OrdinalIgnoreCase) >= 0;

                            if (isCoopMissionStart)
                            {
                                var coopGroupName = coopIdentifier;

                                List<ParsedHenchmanSelection> coopParsedSelections = null;
                                var selectedHenchmanParseSource = "none";
                                if (hasPrepareMatchPayload && !IsNullOrWhiteSpace(prepareMatchSelectedHenchmen))
                                {
                                    coopParsedSelections = TryExtractCoopPayloadHenchmanSelections(prepareMatchSelectedHenchmen);
                                    if (coopParsedSelections != null && coopParsedSelections.Count > 0)
                                    {
                                        selectedHenchmanParseSource = "preparematch-selected";
                                    }
                                }

                                string mapName = null;
                                if (hasPrepareMatchPayload && !IsNullOrWhiteSpace(prepareMatchMapName))
                                {
                                    mapName = prepareMatchMapName;
                                }
                                else if (payloadStrings.Count >= 3 && !IsNullOrWhiteSpace(payloadStrings[2]))
                                {
                                    mapName = payloadStrings[2];
                                }
                                else
                                {
                                    var onIdx = coopGroupName.IndexOf("_On_", StringComparison.OrdinalIgnoreCase);
                                    if (onIdx >= 0)
                                    {
                                        mapName = coopGroupName.Substring(onIdx + 4);
                                        if (!IsNullOrWhiteSpace(mapName) && mapName.EndsWith("S", StringComparison.Ordinal))
                                        {
                                            mapName = mapName.Substring(0, mapName.Length - 1);
                                        }
                                    }
                                }

                                if (IsNullOrWhiteSpace(mapName))
                                {
                                    mapName = "1_010_Prologue";
                                }

                                currentMissionMapName = mapName;

                                _logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "coop-mission-start",
                                    peer = peer,
                                    apMsgId = shared.Value.ApMsgId,
                                    entityId = shared.Value.EntityId,
                                    fieldId = shared.Value.FieldId,
                                    coopGroupName = coopGroupName,
                                    mapName = mapName,
                                    memberList = hasPrepareMatchPayload
                                        ? prepareMatchPlayers
                                        : (payloadStrings.Count > 1 ? payloadStrings[1] : null),
                                    selectedHenchmanParseSource = selectedHenchmanParseSource,
                                    selectedHenchmenRawLength = hasPrepareMatchPayload && prepareMatchSelectedHenchmen != null
                                        ? prepareMatchSelectedHenchmen.Length
                                        : 0,
                                    henchSelectionCount = coopParsedSelections != null ? coopParsedSelections.Count : 0,
                                });

                                currentCoopGroupName = coopGroupName;
                                RegisterCoopMissionParticipant(coopGroupName, peer, stream);

                                lock (_coopMissionLock)
                                {
                                    Dictionary<Guid, List<ParsedHenchmanSelection>> byIdentity;
                                    if (!_coopMissionHenchSelections.TryGetValue(coopGroupName, out byIdentity) || byIdentity == null)
                                    {
                                        byIdentity = new Dictionary<Guid, List<ParsedHenchmanSelection>>();
                                        _coopMissionHenchSelections[coopGroupName] = byIdentity;
                                    }

                                    if (coopParsedSelections != null && coopParsedSelections.Count > 0)
                                    {
                                        byIdentity[activeIdentityGuid] = new List<ParsedHenchmanSelection>(coopParsedSelections);
                                    }
                                    else
                                    {
                                        byIdentity.Remove(activeIdentityGuid);
                                    }
                                }

                                var requestMsgNoBase = direct.Value.MsgNo + 250;

                                if (!sentMissionEntityIntros)
                                {
                                    var gameworldIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes((ulong)gameworldEntityId), BitConverter.GetBytes(gameworldCommunicationObjectTypeId), BitConverter.GetBytes(0));
                                    var gameworldIntroCore = BuildCoreDirectSystem(1, gameworldIntroRaw, requestMsgNoBase + 1);
                                    SendRawFrame(stream, peer, PrefixLength(gameworldIntroCore), "sent AP introduce shared entity (type=7 gameworld communication object, id=5)");

                                    var missionInstanceIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes((ulong)missionInstanceEntityId), BitConverter.GetBytes(missionInstanceCommunicationObjectTypeId), BitConverter.GetBytes(0));
                                    var missionInstanceIntroCore = BuildCoreDirectSystem(1, missionInstanceIntroRaw, requestMsgNoBase + 2);
                                    SendRawFrame(stream, peer, PrefixLength(missionInstanceIntroCore), "sent AP introduce shared entity (type=9 mission instance communication object, id=6)");

                                    var missionCommandIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes((ulong)missionCommandEntityId), BitConverter.GetBytes(missionCommandCommunicationObjectTypeId), BitConverter.GetBytes(0));
                                    var missionCommandIntroCore = BuildCoreDirectSystem(1, missionCommandIntroRaw, requestMsgNoBase + 3);
                                    SendRawFrame(stream, peer, PrefixLength(missionCommandIntroCore), "sent AP introduce shared entity (type=10 mission command communication object, id=7)");

                                    var gameworldOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(gameworldEntityId, gameworldCommunicationObjectTypeId), requestMsgNoBase + 4);
                                    SendRawFrame(stream, peer, PrefixLength(gameworldOwnerCore), "sent AP shared-entity set-owner (entity=5)");

                                    var missionInstanceOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(missionInstanceEntityId, missionInstanceCommunicationObjectTypeId), requestMsgNoBase + 5);
                                    SendRawFrame(stream, peer, PrefixLength(missionInstanceOwnerCore), "sent AP shared-entity set-owner (entity=6)");

                                    var missionCommandOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(missionCommandEntityId, missionCommandCommunicationObjectTypeId), requestMsgNoBase + 6);
                                    SendRawFrame(stream, peer, PrefixLength(missionCommandOwnerCore), "sent AP shared-entity set-owner (entity=7)");

                                    sentMissionEntityIntros = true;
                                }

                                var seed0 = 0x11111111u;
                                var seed1 = 0x22222222u;
                                var seed2 = 0x33333333u;
                                var seed3 = 0x44444444u;

                                var compressedMatchConfiguration = _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeCharacterName, gameClientEntityId);

                                // Best-effort: build a two-human coop roster based on the member list the client sends.
                                // If parsing fails, fall back to a single-player roster (better than blocking mission start).
                                if (_userStore != null)
                                {
                                    try
                                    {
                                        var memberListRaw = payloadStrings.Count > 1 ? payloadStrings[1] : null;
                                        var memberGuids = ParseGuidsFromLooseText(memberListRaw, 8);
                                        if (memberGuids == null || memberGuids.Length == 0)
                                        {
                                            memberGuids = new Guid[] { activeIdentityGuid };
                                        }
                                        if (!ContainsGuid(memberGuids, activeIdentityGuid))
                                        {
                                            var extended = new Guid[memberGuids.Length + 1];
                                            Array.Copy(memberGuids, 0, extended, 0, memberGuids.Length);
                                            extended[extended.Length - 1] = activeIdentityGuid;
                                            memberGuids = extended;
                                        }

                                        // Client parties are practically capped (observed up to 4). Don't hard-fail if more are listed.
                                        var maxHumans = 4;
                                        if (memberGuids.Length > maxHumans)
                                        {
                                            var truncated = new Guid[maxHumans];
                                            Array.Copy(memberGuids, 0, truncated, 0, maxHumans);
                                            memberGuids = truncated;
                                        }

                                        if (memberGuids.Length >= 2)
                                        {
                                            // Stable ordering so both clients generate the same blob.
                                            Array.Sort(memberGuids, GuidStringOrdinalComparer.Instance);

                                            Guid leaderAccountId;
                                            if (CoopGroupHostRegistry.TryGetLeader(coopGroupName, out leaderAccountId))
                                            {
                                                memberGuids = OrderGuidsWithLeaderFirst(memberGuids, leaderAccountId);
                                            }

                                            var identityGuids = memberGuids;
                                            var careerIndices = new int[identityGuids.Length];
                                            var slots = new CareerSlot[identityGuids.Length];
                                            var playerIds = new ulong[identityGuids.Length];
                                            var selectedHenchmenPerPlayer = new PlayerCharacterSnapshot[identityGuids.Length][];

                                            Dictionary<Guid, List<ParsedHenchmanSelection>> coopSelectionsByIdentity = null;
                                            lock (_coopMissionLock)
                                            {
                                                Dictionary<Guid, List<ParsedHenchmanSelection>> tmp;
                                                if (_coopMissionHenchSelections.TryGetValue(coopGroupName, out tmp) && tmp != null)
                                                {
                                                    coopSelectionsByIdentity = new Dictionary<Guid, List<ParsedHenchmanSelection>>();
                                                    foreach (var kv in tmp)
                                                    {
                                                        coopSelectionsByIdentity[kv.Key] = kv.Value != null
                                                            ? new List<ParsedHenchmanSelection>(kv.Value)
                                                            : null;
                                                    }
                                                }
                                            }

                                            // Allow a brief rendezvous window so both clients' StartCoop payloads can land,
                                            // carrying one selection each. This avoids creating the shared sim from only
                                            // the first-arriving participant's data.
                                            var waitUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
                                            while (DateTime.UtcNow < waitUntilUtc)
                                            {
                                                var allHaveSelection = true;
                                                for (var i = 0; i < identityGuids.Length; i++)
                                                {
                                                    if (coopSelectionsByIdentity == null)
                                                    {
                                                        allHaveSelection = false;
                                                        break;
                                                    }

                                                    List<ParsedHenchmanSelection> parsed;
                                                    if (!coopSelectionsByIdentity.TryGetValue(identityGuids[i], out parsed) || parsed == null || parsed.Count == 0)
                                                    {
                                                        allHaveSelection = false;
                                                        break;
                                                    }
                                                }

                                                if (allHaveSelection)
                                                {
                                                    break;
                                                }

                                                SleepWithStop(stopEvent, 50);

                                                lock (_coopMissionLock)
                                                {
                                                    Dictionary<Guid, List<ParsedHenchmanSelection>> tmp;
                                                    if (_coopMissionHenchSelections.TryGetValue(coopGroupName, out tmp) && tmp != null)
                                                    {
                                                        coopSelectionsByIdentity = new Dictionary<Guid, List<ParsedHenchmanSelection>>();
                                                        foreach (var kv in tmp)
                                                        {
                                                            coopSelectionsByIdentity[kv.Key] = kv.Value != null
                                                                ? new List<ParsedHenchmanSelection>(kv.Value)
                                                                : null;
                                                        }
                                                    }
                                                }
                                            }

                                            for (var i = 0; i < identityGuids.Length; i++)
                                            {
                                                var guid = identityGuids[i];
                                                var hash = guid.ToString();
                                                var idx = guid == activeIdentityGuid ? activeCareerIndex : _userStore.GetLastCareerIndex(hash);
                                                if (idx < 0) idx = 0;
                                                careerIndices[i] = idx;
                                                slots[i] = _userStore.GetOrCreateCareer(hash, idx, false);

                                                ulong mappedEntityId;
                                                if (!TryGetGameClientEntityIdForIdentity(guid, out mappedEntityId) || mappedEntityId == 0UL)
                                                {
                                                    // If another client hasn't logged in yet, fall back to a deterministic non-zero id.
                                                    mappedEntityId = ComputeFnv1a64(guid.ToByteArray());
                                                    if (mappedEntityId == 0UL)
                                                    {
                                                        mappedEntityId = (ulong)(i + 1);
                                                    }
                                                }
                                                playerIds[i] = mappedEntityId;

                                                List<ParsedHenchmanSelection> parsedSelections;
                                                if (coopSelectionsByIdentity != null
                                                    && coopSelectionsByIdentity.TryGetValue(guid, out parsedSelections)
                                                    && parsedSelections != null
                                                    && parsedSelections.Count > 0)
                                                {
                                                    SerializeDefaultHenchmanCollection();
                                                    var snapshots = CachedHenchmanCollectionSnapshots;
                                                    if (snapshots != null && snapshots.Count > 0)
                                                    {
                                                        var ownerKarma = slots[i] != null ? slots[i].Karma : 0;
                                                        var ownerSpentKarma = slots[i] != null ? slots[i].SpentKarma : 0;
                                                        var ownerNuyen = slots[i] != null ? slots[i].Nuyen : 0;

                                                        var resolved = new List<PlayerCharacterSnapshot>();
                                                        for (var si = 0; si < parsedSelections.Count; si++)
                                                        {
                                                            var selection = parsedSelections[si];
                                                            if (selection.HenchmanId < 0 || selection.HenchmanId >= snapshots.Count)
                                                            {
                                                                continue;
                                                            }

                                                            var src = snapshots[selection.HenchmanId];
                                                            var clone = CloneHenchSnapshotForMission(src, guid, si, ownerKarma, ownerSpentKarma, ownerNuyen);
                                                            if (clone != null)
                                                            {
                                                                resolved.Add(clone);
                                                            }
                                                        }

                                                        if (resolved.Count > 0)
                                                        {
                                                            selectedHenchmenPerPlayer[i] = resolved.ToArray();
                                                        }
                                                    }
                                                }
                                            }

                                            compressedMatchConfiguration = _matchConfigurationGenerator.GetCompressedCoopMatchConfiguration(mapName, identityGuids, careerIndices, slots, playerIds, selectedHenchmenPerPlayer);
                                        }
                                        else
                                        {
                                            var activeSlot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                                            if (activeSlot != null)
                                            {
                                                compressedMatchConfiguration = _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeSlot, null, gameClientEntityId);
                                            }
                                        }
                                    }
                                    catch
                                    {
                                        // Ignore; mission start will proceed with the default single-player config.
                                    }
                                }

                                // Coop missions must share one authoritative simulation across all peers.
                                // If each TCP connection has its own sim, neither side will ever observe the other
                                // player exhausting actions, so the team never ends and AI turns never start.
                                CoopMissionSessionState coopSession;
                                var cancelCompletedCoopStart = false;
                                lock (_coopMissionLock)
                                {
                                    if (!_coopMissionSessions.TryGetValue(coopGroupName, out coopSession) || coopSession == null)
                                    {
                                        // Only cancel for completed maps when we'd have to create a brand-new coop session.
                                        // If a session already exists, late/jittered duplicate starts should reuse it instead
                                        // of kicking one player back to hub.
                                        if (IsMissionCompletedForCareer(activeIdentityHash, activeCareerIndex, mapName, completedStoryMissions))
                                        {
                                            cancelCompletedCoopStart = true;
                                        }
                                        else
                                        {
                                            coopSession = new CoopMissionSessionState(coopGroupName);
                                            _coopMissionSessions[coopGroupName] = coopSession;
                                        }
                                    }
                                }

                                if (cancelCompletedCoopStart)
                                {
                                    var cancelledCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 24, new byte[0]), requestMsgNoBase);
                                    SendRawFrame(stream, peer, PrefixLength(cancelledCore), "sent MetaGameplayCommunicationObject StartMissionCancelled (coop mission already completed)");
                                    continue;
                                }

                                simulationSessionSync = coopSession.SyncRoot;

                                lock (coopSession.SyncRoot)
                                {
                                    if (coopSession.Simulation != null
                                        && !IsNullOrWhiteSpace(coopSession.CompressedMatchConfiguration)
                                        && string.Equals(coopSession.MapName, mapName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        // Reuse existing mission session.
                                        simulationSession = coopSession.Simulation;
                                        seed0 = coopSession.Seed0;
                                        seed1 = coopSession.Seed1;
                                        seed2 = coopSession.Seed2;
                                        seed3 = coopSession.Seed3;
                                        compressedMatchConfiguration = coopSession.CompressedMatchConfiguration;

                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "sim",
                                            peer = peer,
                                            status = "coop-reuse",
                                            mapName = mapName,
                                            coopGroupName = coopGroupName,
                                        });
                                    }
                                    else
                                    {
                                        try
                                        {
                                            var storyLineForLoot = "Main Campaign";
                                            var chapterForLoot = 0;
                                            if (_userStore != null)
                                            {
                                                try
                                                {
                                                    var slotForLoot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                                                    if (slotForLoot != null)
                                                    {
                                                        chapterForLoot = slotForLoot.MainCampaignCurrentChapter;
                                                    }
                                                }
                                                catch
                                                {
                                                }
                                            }

                                            coopSession.MapName = mapName;
                                            coopSession.Seed0 = seed0;
                                            coopSession.Seed1 = seed1;
                                            coopSession.Seed2 = seed2;
                                            coopSession.Seed3 = seed3;
                                            coopSession.CompressedMatchConfiguration = compressedMatchConfiguration;

                                            // New run -> reset coop loot snapshot/tracking.
                                            coopSession.LootSnapshot = null;
                                            if (coopSession.LootAppliedToParticipants == null)
                                            {
                                                coopSession.LootAppliedToParticipants = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                                            }
                                            else
                                            {
                                                coopSession.LootAppliedToParticipants.Clear();
                                            }

                                            coopSession.Simulation = ServerSimulationSession.Create(
                                                _logger,
                                                peer,
                                                _options.StaticDataDir,
                                                _options.StreamingAssetsDir,
                                                mapName,
                                                compressedMatchConfiguration,
                                                seed0,
                                                seed1,
                                                seed2,
                                                seed3,
                                                storyLineForLoot,
                                                chapterForLoot,
                                                _options != null && _options.EnableAiLogic);
                                            simulationSession = coopSession.Simulation;

                                            _logger.Log(new
                                            {
                                                ts = RequestLogger.UtcNowIso(),
                                                type = "sim",
                                                peer = peer,
                                                status = "coop-created",
                                                mapName = mapName,
                                                coopGroupName = coopGroupName,
                                            });

                                            MissionRuntimeRegistry.MarkCoopMissionStarted(coopGroupName);
                                        }
                                        catch (Exception ex)
                                        {
                                            coopSession.Simulation = null;
                                            simulationSession = null;
                                            _logger.Log(new
                                            {
                                                ts = RequestLogger.UtcNowIso(),
                                                type = "sim",
                                                peer = peer,
                                                status = "failed",
                                                mapName = mapName,
                                                coopGroupName = coopGroupName,
                                                message = ex.Message,
                                            });
                                        }
                                    }
                                }

                                var startMissionAcceptedPayload = Concat(
                                    BitConverter.GetBytes(1L),
                                    BitConverter.GetBytes(seed0),
                                    BitConverter.GetBytes(seed1),
                                    BitConverter.GetBytes(seed2),
                                    BitConverter.GetBytes(seed3),
                                    BuildUtf16StringPayload(compressedMatchConfiguration),
                                    BitConverter.GetBytes((ulong)gameworldEntityId),
                                    BitConverter.GetBytes((ulong)missionInstanceEntityId),
                                    BitConverter.GetBytes((ulong)missionCommandEntityId));

                                var startMissionAcceptedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 23, startMissionAcceptedPayload), requestMsgNoBase + 7);
                                SendRawFrame(stream, peer, PrefixLength(startMissionAcceptedCore), "sent MetaGameplayCommunicationObject StartMissionAccepted (coop map=" + mapName + ")");

                                SleepWithStop(stopEvent, 6000);
                                var startMissionForClientsCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, missionInstanceEntityId, 0, new byte[0]), requestMsgNoBase + 8);
                                SendRawFrame(stream, peer, PrefixLength(startMissionForClientsCore), "sent MissionInstanceCommunicationObject StartMissionForClients (coop)");

                                continue;
                            }

                            if (_userStore != null && isMetaGameplayChangeSkillTrees)
                            {
                                var rawMessage = payloadStrings[0];
                                if (!IsNullOrWhiteSpace(rawMessage)
                                    && (rawMessage.IndexOf("SkillTreeChanges", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("SkillTreeTechnichalName", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("SkillTechnichalName", StringComparison.Ordinal) >= 0))
                                {
                                    SkillTreeChanges requestedChanges = null;
                                    try
                                    {
                                        requestedChanges = JsonFxSerializerProvider.Current.Deserialize<SkillTreeChanges>(rawMessage);
                                    }
                                    catch
                                    {
                                        requestedChanges = null;
                                    }

                                    if (requestedChanges == null)
                                    {
                                        continue;
                                    }

                                    var slotIndex = activeCareerIndex;
                                    if (slotIndex < 0)
                                    {
                                        slotIndex = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetLastCareerIndex(activeIdentityHash) : 0;
                                    }

                                    var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, slotIndex, false) : null;
                                    if (slot != null)
                                    {
                                        var appliedSkillChanges = _skillPurchaseService.Apply(slot, requestedChanges);

                                        if (appliedSkillChanges.Persisted)
                                        {
                                            try { _userStore.UpsertCareer(activeIdentityHash, slot); } catch { }
                                        }

                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "skilltree-change",
                                            peer = peer,
                                            careerIndex = slotIndex,
                                            applyReset = appliedSkillChanges.ApplyReset,
                                            purchases = requestedChanges.Purchases != null ? requestedChanges.Purchases.Length : 0,
                                            applied = appliedSkillChanges.AppliedCount,
                                            persisted = appliedSkillChanges.Persisted,
                                            karmaBefore = appliedSkillChanges.KarmaBefore,
                                            karmaRefunded = appliedSkillChanges.KarmaRefunded,
                                            karmaCostApplied = appliedSkillChanges.KarmaSpent,
                                            karmaAfter = slot.Karma,
                                        });

                                        if (appliedSkillChanges.ShouldNotifyClient)
                                        {
                                            try
                                            {
                                                var msgNoBase = direct.Value.MsgNo + 2;
                                                var skillTreeChangedJson = JsonFxSerializerProvider.Current.Serialize<SkillTreeChanges>(appliedSkillChanges.AppliedChanges);
                                                var skillChangedPayload = BuildUtf16StringPayload(skillTreeChangedJson);
                                                var skillChangedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 34, skillChangedPayload), msgNoBase);
                                                SendRawFrame(stream, peer, PrefixLength(skillChangedCore), "sent MetaGameplayCommunicationObject SkillTreeChanged in response to ChangeSkillTrees");
                                            }
                                            catch
                                            {
                                            }
                                        }
                                    }
                                }
                            }

                            if (_userStore != null && isMetaGameplayChangeItemPosessions)
                            {
                                var rawMessage = payloadStrings[0];
                                if (!IsNullOrWhiteSpace(rawMessage)
                                    && rawMessage.IndexOf("ItemPossessionChanges", StringComparison.Ordinal) >= 0)
                                {
                                    ItemPossessionChanges requestedChanges = null;
                                    try
                                    {
                                        requestedChanges = JsonFxSerializerProvider.Current.Deserialize<ItemPossessionChanges>(rawMessage);
                                    }
                                    catch
                                    {
                                        requestedChanges = null;
                                    }

                                    if (requestedChanges == null)
                                    {
                                        continue;
                                    }

                                    var slotIndex = activeCareerIndex;
                                    if (slotIndex < 0)
                                    {
                                        slotIndex = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetLastCareerIndex(activeIdentityHash) : 0;
                                    }

                                    var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, slotIndex, false) : null;
                                    if (slot != null)
                                    {
                                        var appliedShopChanges = _shopInventoryService.Apply(slot, requestedChanges);

                                        if (appliedShopChanges.Persisted)
                                        {
                                            try { _userStore.UpsertCareer(activeIdentityHash, slot); } catch { }
                                        }

                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "item-possession-change",
                                            peer = peer,
                                            careerIndex = slotIndex,
                                            shopKeeper = requestedChanges.ShopKeeper,
                                            itemChanges = requestedChanges.ItemChanges != null ? requestedChanges.ItemChanges.Length : 0,
                                            applied = appliedShopChanges.ShopChanges != null && appliedShopChanges.ShopChanges.AppliedChanges != null ? appliedShopChanges.ShopChanges.AppliedChanges.Length : 0,
                                            failed = appliedShopChanges.ShopChanges != null && appliedShopChanges.ShopChanges.Failed,
                                            nuyenBefore = appliedShopChanges.NuyenBefore,
                                            totalNuyenChange = appliedShopChanges.ShopChanges != null ? appliedShopChanges.ShopChanges.TotalNuyenChange : 0,
                                            nuyen = slot.Nuyen,
                                        });

                                        // Push authoritative inventory + wallet to the client.
                                        // DesignedClient.cs fields:
                                        // - 31: InventoryChanged(serializedInventory, serializedShopChanges)
                                        // - 32: WalletChanged(serializedWallet)
                                        try
                                        {
                                            var msgNoBase = direct.Value.MsgNo + 20;
                                            var serializedInventory = SerializeInventoryFromSlot(slot);
                                            var serializedShopChanges = InventorySerializer.SerializeShopItemChanges(appliedShopChanges.ShopChanges);

                                            var inventoryChangedPayload = BuildUtf16StringPayload(serializedInventory, serializedShopChanges);
                                            var inventoryChangedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 31, inventoryChangedPayload), msgNoBase + 1);
                                            SendRawFrame(stream, peer, PrefixLength(inventoryChangedCore), "sent MetaGameplayCommunicationObject InventoryChanged in response to ChangeItemPosessions");

                                            var serializedWallet = SerializeWalletForSlot(slot);
                                            var walletChangedPayload = BuildUtf16StringPayload(serializedWallet);
                                            var walletChangedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 32, walletChangedPayload), msgNoBase + 2);
                                            SendRawFrame(stream, peer, PrefixLength(walletChangedCore), "sent MetaGameplayCommunicationObject WalletChanged in response to ChangeItemPosessions");
                                        }
                                        catch
                                        {
                                        }

                                        // Also push an updated metagameplay snapshot so reload flows stay consistent.
                                        try
                                        {
                                            var msgNoBase = direct.Value.MsgNo + 30;
                                            var zipped = _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, slotIndex, slot);
                                            var metaSnapshotPayload = BuildUtf16StringPayload(zipped);
                                            var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), msgNoBase + 1);
                                            SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient after ChangeItemPosessions");
                                        }
                                        catch
                                        {
                                        }
                                    }
                                }
                            }

                            // Character changes can arrive via the MetaGameplay entity even if the decoded
                            // ApMsgId/EntityId/FieldId don't match our current expectations.
                            // If we have a decoded JSON payload and it looks like a CharacterChangeCollection,
                            // apply it unconditionally.
                            if (_userStore != null && payloadStrings.Count > 0 && PayloadContains(payloadStrings, "CharacterChangeCollection"))
                            {
                                var rawChange = payloadStrings[0];
                                // Reuse the existing handler by entering the same codepath.
                                // (We don't require the shared header to decode as MetaGameplay field 6 here.)
                                var rawMessage = rawChange;

                                var parsedChange = TryDeserializeJsonDict(rawMessage);

                                var hasAnyRelevantChange = rawMessage != null
                                    && (rawMessage.IndexOf("\"NewName\"", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("SkinTextureIndexChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("BackgroundStoryChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("BodyChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("PortraitChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("VoiceSetChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("WantsBackgroundChangeChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("InventoryChanges", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("PrimaryWeaponChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("SecondaryWeaponChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("ArmorChange", StringComparison.Ordinal) >= 0);

                                if (hasAnyRelevantChange)
                                {
                                    var slotIndex = activeCareerIndex;
                                    if (slotIndex < 0)
                                    {
                                        slotIndex = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetLastCareerIndex(activeIdentityHash) : 0;
                                    }

                                    var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, slotIndex, false) : null;
                                    if (slot != null)
                                    {
                                        var wasPendingPersistenceCreation = slot.PendingPersistenceCreation;
                                        var changed = false;
                                        var shouldSendCorrection = false;
                                        string ignoredPortraitOld = null;
                                        string ignoredPortraitNew = null;

                                        if (slot.EquippedItems == null)
                                        {
                                            slot.EquippedItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                        }

                                        // Prefer structured parsing (avoids key collisions like WantsBackgroundChangeChange.New).
                                        var newName = (parsedChange != null)
                                            ? GetStringValue(GetDictValue(parsedChange, "NameChange"), "NewName")
                                            : ExtractJsonStringValue(rawMessage, "NewName");
                                        if (!IsNullOrWhiteSpace(newName) && !string.Equals(slot.CharacterName, newName, StringComparison.Ordinal))
                                        {
                                            slot.CharacterName = newName;
                                            changed = true;
                                        }

                                        if (!slot.IsOccupied)
                                        {
                                            slot.IsOccupied = true;
                                            changed = true;
                                        }

                                        if (parsedChange != null)
                                        {
                                            var skinChange = GetDictValue(parsedChange, "SkinTextureIndexChange");
                                            var skin = GetInt32Value(skinChange, "NewIndex", -1);
                                            if (skin >= 0 && slot.SkinTextureIndex != skin)
                                            {
                                                slot.SkinTextureIndex = skin;
                                                changed = true;
                                            }

                                            var storyChange = GetDictValue(parsedChange, "BackgroundStoryChange");
                                            var story = GetUInt64Value(storyChange, "NewStory", 0UL);
                                            if (story != 0UL && slot.BackgroundStory != story)
                                            {
                                                slot.BackgroundStory = story;
                                                changed = true;
                                            }

                                            var bodyChange = GetDictValue(parsedChange, "BodyChange");
                                            if (bodyChange != null)
                                            {
                                                var meta = GetUInt64Value(bodyChange, "NewMetatype", 0UL);
                                                var gender = GetUInt64Value(bodyChange, "NewGender", 0UL);
                                                if (meta != 0UL && gender != 0UL)
                                                {
                                                    ulong bodytype;
                                                    if (TryResolveBodytypeId(meta, gender, out bodytype) && bodytype != 0UL)
                                                    {
                                                        if (slot.Bodytype != bodytype)
                                                        {
                                                            slot.Bodytype = bodytype;
                                                            changed = true;
                                                        }
                                                    }
                                                }
                                            }

                                            var portraitChange = GetDictValue(parsedChange, "PortraitChange");
                                            var newPortrait = GetStringValue(portraitChange, "NewPortrait");
                                            if (!IsNullOrWhiteSpace(newPortrait) && !string.Equals(slot.PortraitPath, newPortrait, StringComparison.Ordinal))
                                            {
                                                // Empirically, the client can send a CharacterChangeCollection with only a PortraitChange
                                                // during hub transitions (e.g., after the first mission), which resets the portrait to a
                                                // default UI value. We treat portraits as immutable after initial creation unless the slot
                                                // has never had a portrait set.
                                                var allowPortraitUpdate = slot.PendingPersistenceCreation || IsNullOrWhiteSpace(slot.PortraitPath);
                                                if (allowPortraitUpdate)
                                                {
                                                    slot.PortraitPath = newPortrait;
                                                    // Keep the legacy summary portrait in sync.
                                                    slot.Portrait = newPortrait;
                                                    changed = true;

                                                    // If BodyChange is missing (common in some flows), infer the bodytype from the portrait.
                                                    if (slot.Bodytype == 0UL)
                                                    {
                                                        ulong inferredMeta;
                                                        ulong inferredGender;
                                                        if (TryInferMetatypeAndGenderFromPortrait(newPortrait, out inferredMeta, out inferredGender))
                                                        {
                                                            ulong inferredBodytype;
                                                            if (TryResolveBodytypeId(inferredMeta, inferredGender, out inferredBodytype) && inferredBodytype != 0UL)
                                                            {
                                                                slot.Bodytype = inferredBodytype;
                                                                changed = true;
                                                            }
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    ignoredPortraitOld = GetStringValue(portraitChange, "OldPortrait");
                                                    ignoredPortraitNew = newPortrait;
                                                    shouldSendCorrection = true;
                                                }
                                            }

                                            // Final fallback: if we still have no bodytype but do have a portrait, infer from current portrait.
                                            if (slot.Bodytype == 0UL && !IsNullOrWhiteSpace(slot.PortraitPath))
                                            {
                                                ulong inferredMeta;
                                                ulong inferredGender;
                                                if (TryInferMetatypeAndGenderFromPortrait(slot.PortraitPath, out inferredMeta, out inferredGender))
                                                {
                                                    ulong inferredBodytype;
                                                    if (TryResolveBodytypeId(inferredMeta, inferredGender, out inferredBodytype) && inferredBodytype != 0UL)
                                                    {
                                                        slot.Bodytype = inferredBodytype;
                                                        changed = true;
                                                    }
                                                }
                                            }

                                            var voiceChange = GetDictValue(parsedChange, "VoiceSetChange");
                                            var newVoice = GetStringValue(voiceChange, "NewVoiceSet");
                                            if (!IsNullOrWhiteSpace(newVoice) && !string.Equals(slot.Voiceset, newVoice, StringComparison.Ordinal))
                                            {
                                                slot.Voiceset = newVoice;
                                                changed = true;
                                            }

                                            var wantsChange = GetDictValue(parsedChange, "WantsBackgroundChangeChange");
                                            var wantsNew = GetStringValue(wantsChange, "New");
                                            bool wantsBool;
                                            if (!IsNullOrWhiteSpace(wantsNew) && bool.TryParse(wantsNew, out wantsBool) && slot.WantsBackgroundChange != wantsBool)
                                            {
                                                slot.WantsBackgroundChange = wantsBool;
                                                changed = true;
                                            }

                                            // Loadout changes (weapons/armor) from character editor.
                                            // The editor uses CharacterChangeCollection.{PrimaryWeaponChange,SecondaryWeaponChange,ArmorChange}.
                                            var primaryWeaponChange = GetDictValue(parsedChange, "PrimaryWeaponChange");
                                            if (primaryWeaponChange != null)
                                            {
                                                var newWeapon = GetDictValue(primaryWeaponChange, "NewWeapon");
                                                var newItemId = GetStringValue(newWeapon, "ItemId");
                                                var newInvKey = GetInt32Value(newWeapon, "InventoryKey", 0);
                                                if (!IsNullOrWhiteSpace(newItemId)
                                                    && (!string.Equals(slot.PrimaryWeaponItemId, newItemId, StringComparison.Ordinal)
                                                        || slot.PrimaryWeaponInventoryKey != newInvKey))
                                                {
                                                    slot.PrimaryWeaponItemId = newItemId;
                                                    slot.PrimaryWeaponInventoryKey = newInvKey;
                                                    changed = true;
                                                }
                                            }

                                            var secondaryWeaponChange = GetDictValue(parsedChange, "SecondaryWeaponChange");
                                            if (secondaryWeaponChange != null)
                                            {
                                                var newWeapon = GetDictValue(secondaryWeaponChange, "NewWeapon");
                                                var newItemId = GetStringValue(newWeapon, "ItemId");
                                                var newInvKey = GetInt32Value(newWeapon, "InventoryKey", 1);
                                                if (!IsNullOrWhiteSpace(newItemId)
                                                    && (!string.Equals(slot.SecondaryWeaponItemId, newItemId, StringComparison.Ordinal)
                                                        || slot.SecondaryWeaponInventoryKey != newInvKey))
                                                {
                                                    slot.SecondaryWeaponItemId = newItemId;
                                                    slot.SecondaryWeaponInventoryKey = newInvKey;
                                                    changed = true;
                                                }
                                            }

                                            var armorChange = GetDictValue(parsedChange, "ArmorChange");
                                            if (armorChange != null)
                                            {
                                                // EquipArmor uses OldArmor/NewArmor.
                                                var newArmor = GetDictValue(armorChange, "NewArmor") ?? GetDictValue(armorChange, "NewItem");
                                                var newItemId = GetStringValue(newArmor, "ItemId");
                                                var newInvKey = GetInt32Value(newArmor, "InventoryKey", 2);
                                                if (!IsNullOrWhiteSpace(newItemId)
                                                    && (!string.Equals(slot.ArmorItemId, newItemId, StringComparison.Ordinal)
                                                        || slot.ArmorInventoryKey != newInvKey))
                                                {
                                                    slot.ArmorItemId = newItemId;
                                                    slot.ArmorInventoryKey = newInvKey;
                                                    changed = true;
                                                }
                                            }

                                            var inv = GetArrayValue(parsedChange, "InventoryChanges");
                                            if (inv != null && inv.Length > 0)
                                            {
                                                for (var i = 0; i < inv.Length; i++)
                                                {
                                                    var entry = inv[i] as IDictionary;
                                                    if (entry == null)
                                                    {
                                                        continue;
                                                    }

                                                    ulong equipSlot = GetUInt64Value(entry, "Slot", 0UL);
                                                    if (equipSlot == 0UL)
                                                    {
                                                        continue;
                                                    }

                                                    var newItem = GetDictValue(entry, "NewItem");
                                                    var newItemId = GetStringValue(newItem, "ItemId");
                                                    var key = equipSlot.ToString(CultureInfo.InvariantCulture);
                                                    if (IsNullOrWhiteSpace(newItemId))
                                                    {
                                                        if (slot.EquippedItems.ContainsKey(key))
                                                        {
                                                            slot.EquippedItems.Remove(key);
                                                            changed = true;
                                                        }
                                                    }
                                                    else
                                                    {
                                                        string existing;
                                                        if (!slot.EquippedItems.TryGetValue(key, out existing) || !string.Equals(existing, newItemId, StringComparison.Ordinal))
                                                        {
                                                            slot.EquippedItems[key] = newItemId;
                                                            changed = true;
                                                        }
                                                    }
                                                }
                                            }
                                        }

                                        if (parsedChange == null)
                                        {
                                            // Legacy string-based fallback.
                                            if (rawMessage.IndexOf("SkinTextureIndexChange", StringComparison.Ordinal) >= 0)
                                            {
                                                int skin;
                                                if (TryParseInt32(ExtractJsonStringValue(rawMessage, "NewIndex"), out skin))
                                                {
                                                    if (slot.SkinTextureIndex != skin)
                                                    {
                                                        slot.SkinTextureIndex = skin;
                                                        changed = true;
                                                    }
                                                }
                                            }
                                            if (rawMessage.IndexOf("BackgroundStoryChange", StringComparison.Ordinal) >= 0)
                                            {
                                                ulong story;
                                                if (TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewStory"), out story))
                                                {
                                                    if (slot.BackgroundStory != story)
                                                    {
                                                        slot.BackgroundStory = story;
                                                        changed = true;
                                                    }
                                                }
                                            }
                                            if (rawMessage.IndexOf("BodyChange", StringComparison.Ordinal) >= 0)
                                            {
                                                ulong meta;
                                                ulong gender;
                                                if (TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewMetatype"), out meta)
                                                    && TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewGender"), out gender))
                                                {
                                                    ulong bodytype;
                                                    if (TryResolveBodytypeId(meta, gender, out bodytype) && bodytype != 0UL)
                                                    {
                                                        if (slot.Bodytype != bodytype)
                                                        {
                                                            slot.Bodytype = bodytype;
                                                            changed = true;
                                                        }
                                                    }
                                                }
                                            }

                                            if (rawMessage.IndexOf("PortraitChange", StringComparison.Ordinal) >= 0)
                                            {
                                                var newPortrait = ExtractJsonStringValue(rawMessage, "NewPortrait");
                                                if (!IsNullOrWhiteSpace(newPortrait) && !string.Equals(slot.PortraitPath, newPortrait, StringComparison.Ordinal))
                                                {
                                                    slot.PortraitPath = newPortrait;
                                                    slot.Portrait = newPortrait;
                                                    changed = true;
                                                }
                                            }

                                            if (rawMessage.IndexOf("VoiceSetChange", StringComparison.Ordinal) >= 0)
                                            {
                                                var newVoice = ExtractJsonStringValue(rawMessage, "NewVoiceSet");
                                                if (!IsNullOrWhiteSpace(newVoice) && !string.Equals(slot.Voiceset, newVoice, StringComparison.Ordinal))
                                                {
                                                    slot.Voiceset = newVoice;
                                                    changed = true;
                                                }
                                            }
                                        }

                                        if (changed || shouldSendCorrection)
                                        {
                                            if (!changed && shouldSendCorrection)
                                            {
                                                _logger.Log(new
                                                {
                                                    ts = RequestLogger.UtcNowIso(),
                                                    type = "career-change-collection-ignored",
                                                    peer = peer,
                                                    careerIndex = slotIndex,
                                                    reason = "portrait-update-after-creation",
                                                    oldPortrait = ignoredPortraitOld,
                                                    newPortrait = ignoredPortraitNew,
                                                    currentPortrait = slot.PortraitPath,
                                                });
                                            }

                                            if (changed && slot.PendingPersistenceCreation)
                                            {
                                                slot.PendingPersistenceCreation = false;
                                            }

                                            // If we just committed a brand new career creation, reset story progress to the
                                            // expected starting state. This helps the client compute the next mandatory mission
                                            // (prologue) after the intro splash.
                                            //
                                            // IMPORTANT: The user may create a new runner in an already-occupied slot. In that
                                            // case we must clear any previous campaign progress (e.g., prologue marked Completed),
                                            // otherwise the client will attempt to start the prologue and we will cancel it as
                                            // "mission-completed", producing the in-game "Server aborted mission" popup.
                                            if (changed && wasPendingPersistenceCreation)
                                            {
                                                slot.MainCampaignCurrentChapter = 0;

                                                // Starting cash/karma for a brand new runner.
                                                slot.Nuyen = 0;

                                                // Starting karma for a brand new runner.
                                                slot.Karma = 0;
                                                slot.SpentKarma = 0;

                                                // Reset main campaign progression for a brand new runner.
                                                slot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                                slot.MainCampaignMissionStates["1_010_Prologue"] = StoryMissionstate.Available.ToString();

                                                // Reset interaction tracking so early-hub markers/dialog behave correctly.
                                                if (slot.MainCampaignInteractedNpcs != null && slot.MainCampaignInteractedNpcs.Count > 0)
                                                {
                                                    slot.MainCampaignInteractedNpcs = new List<string>();
                                                }

                                                // Also clear per-connection completion tracking so the mission start isn't blocked.
                                                completedStoryMissions.Clear();

                                                if (IsNullOrWhiteSpace(slot.HubId))
                                                {
                                                    slot.HubId = DefaultHubId;
                                                }
                                            }

                                            if (changed)
                                            {
                                                if (!IsNullOrWhiteSpace(activeIdentityHash))
                                                {
                                                    _userStore.UpsertCareer(activeIdentityHash, slot);
                                                }
                                            }

                                            if (changed)
                                            {
                                                _logger.Log(new
                                                {
                                                    ts = RequestLogger.UtcNowIso(),
                                                    type = "career-change-collection-applied",
                                                    peer = peer,
                                                    careerIndex = slotIndex,
                                                    characterName = slot.CharacterName,
                                                    portraitPath = slot.PortraitPath,
                                                    voiceset = slot.Voiceset,
                                                    wantsBackgroundChange = slot.WantsBackgroundChange,
                                                    equippedItemsCount = slot.EquippedItems != null ? slot.EquippedItems.Count : 0,
                                                    bodytype = slot.Bodytype,
                                                    skinTextureIndex = slot.SkinTextureIndex,
                                                    backgroundStory = slot.BackgroundStory,
                                                });
                                            }

                                            if (!IsNullOrWhiteSpace(slot.CharacterName))
                                            {
                                                activeCharacterName = slot.CharacterName;
                                            }

                                            string refreshedHubId;
                                            cachedHubStatePayload = BuildPortedHubStatePayloadForSlot(
                                                slot,
                                                activeIdentityGuid,
                                                slotIndex,
                                                false,
                                                currentHubInstance,
                                                out refreshedHubId,
                                                out currentHubInstance);
                                            currentHubInstanceId = refreshedHubId;

                                            var msgNoBase = direct.Value.MsgNo + 2;
                                            var summaryJson = BuildCareerSummaryJson(!IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetCareers(activeIdentityHash) : null);
                                            var updatePayload = BuildUtf16StringPayload(summaryJson);
                                            var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 2, 16, updatePayload), msgNoBase);
                                            SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries after CharacterChangeCollection");

                                            try
                                            {
                                                var zipped = _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, slotIndex, slot);
                                                var metaSnapshotPayload = BuildUtf16StringPayload(zipped);
                                                var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), msgNoBase + 1);
                                                SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient after CharacterChangeCollection");
                                            }
                                            catch
                                            {
                                            }

                                            // After character creation/customization commits (pending-creation becomes false), the client
                                            // expects updated creation-info + hub handoff; otherwise it can stall before starting the prologue.
                                            if (!slot.PendingPersistenceCreation && cachedHubStatePayload != null)
                                            {
                                                try
                                                {
                                                    var pendingJson = "{\"PendingPersistenceCreation\":" + (slot.PendingPersistenceCreation ? "true" : "false") + ",\"DataVersionChanged\":false}";
                                                    cachedCreationInfoPayload = BuildUtf16StringPayload(pendingJson);
                                                    var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, cachedCreationInfoPayload), msgNoBase + 3);
                                                    SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged after CharacterChangeCollection");
                                                }
                                                catch
                                                {
                                                }

                                                // Some client flows also expect a dedicated CharacterChanged event after ChangeCharacter,
                                                // not only a full metagame snapshot.
                                                try
                                                {
                                                    var characterIdentifier = !IsNullOrWhiteSpace(slot.CharacterIdentifier)
                                                        ? slot.CharacterIdentifier
                                                        : (activeIdentityGuid.ToString() + ":" + slotIndex.ToString(CultureInfo.InvariantCulture));
                                                    var pcs = BuildPlayerCharacterSnapshotForSlot(characterIdentifier, slot.CharacterName, slot);
                                                    var serializedPcs = PCSSerializer.SerializePlayerCharacterSnapshot(pcs);
                                                    var pcsPayload = BuildUtf16StringPayload(serializedPcs);
                                                    var pcsCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 33, pcsPayload), msgNoBase + 4);
                                                    SendRawFrame(stream, peer, PrefixLength(pcsCore), "sent MetaGameplayCommunicationObject CharacterChanged after CharacterChangeCollection");
                                                }
                                                catch
                                                {
                                                }
                                            }

                                            // After committing a new career, proactively broadcast story progress so the client
                                            // has a concrete "Main Campaign" chapter 0 + prologue mission state.
                                            if (changed && wasPendingPersistenceCreation)
                                            {
                                                var arm = Interlocked.Increment(ref postCreateArmGeneration);
                                                var commitMsgNo = direct.Value.MsgNo;
                                                var baselineSend = Interlocked.Read(ref metaSendMessageSeen);
                                                var baselineSetState = Interlocked.Read(ref metaSetStoryMissionStateSeen);
                                                var baselineStart = Interlocked.Read(ref metaStartSingleplayerMissionSeen);

                                                ThreadPool.QueueUserWorkItem(delegate
                                                {
                                                    // Give the UI a moment to finish swapping screens.
                                                    SleepWithStop(stopEvent, 1200);
                                                    if (stopEvent.WaitOne(0) || connectionClosed.WaitOne(0))
                                                    {
                                                        return;
                                                    }

                                                    try
                                                    {
                                                        var postCreateMsgNoBase = commitMsgNo + 40;

                                                        var chapterChangeJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.ChapterChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"Main Campaign\",\"NewChapterIndex\":0}";
                                                        var chapterPayload = BuildUtf16StringPayload(chapterChangeJson);
                                                        var chapterCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, chapterPayload), postCreateMsgNoBase + 1);
                                                        SendRawFrame(stream, peer, PrefixLength(chapterCore), "sent MetaGameplayCommunicationObject StoryprogressChanged (ChapterChange 0) after career creation commit");
                                                    }
                                                    catch
                                                    {
                                                    }

                                                    try
                                                    {
                                                        var missionChangeJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.MissionStateChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"Main Campaign\",\"Mission\":\"1_010_Prologue\",\"NewState\":\"Available\"}";
                                                        var missionPayload = BuildUtf16StringPayload(missionChangeJson);
                                                        var missionCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, missionPayload), commitMsgNo + 42);
                                                        SendRawFrame(stream, peer, PrefixLength(missionCore), "sent MetaGameplayCommunicationObject StoryprogressChanged (MissionStateChange Available) after career creation commit");
                                                    }
                                                    catch
                                                    {
                                                    }

                                                    // Watchdog: if intro finishes but no mission-start messages are sent, log once.
                                                    SleepWithStop(stopEvent, 10000);
                                                    if (stopEvent.WaitOne(0) || connectionClosed.WaitOne(0))
                                                    {
                                                        return;
                                                    }

                                                    if (Interlocked.Read(ref postCreateArmGeneration) != arm)
                                                    {
                                                        return;
                                                    }

                                                    var sendNow = Interlocked.Read(ref metaSendMessageSeen);
                                                    var setNow = Interlocked.Read(ref metaSetStoryMissionStateSeen);
                                                    var startNow = Interlocked.Read(ref metaStartSingleplayerMissionSeen);
                                                    if (sendNow <= baselineSend && setNow <= baselineSetState && startNow <= baselineStart)
                                                    {
                                                        _logger.Log(new
                                                        {
                                                            ts = RequestLogger.UtcNowIso(),
                                                            type = "post-create-watchdog",
                                                            peer = peer,
                                                            note = "No MetaGameplay SendMessage observed after career creation commit; intro/mandatory-mission flow likely not triggered.",
                                                            sendMessages = sendNow,
                                                            setStoryMissionState = setNow,
                                                            startSingleplayerMission = startNow,
                                                        });
                                                    }
                                                });
                                            }
                                        }
                                    }
                                }
                            }

                            // MetaGameplayCommunicationObject.RequestStoryHubFor(...) is callField(2) on entity 3.
                            // The client expects a hub instance (field 37) in response; unsolicited hub pushes can be ignored.
                            var isMetaGameplayRequestStoryHubFor = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 2;
                            if (isMetaGameplayRequestStoryHubFor && cachedHubStatePayload != null)
                            {
                                string requestedHostCharacterId;
                                Guid requestedHostAccountId;
                                string routedHubId = null;
                                string routedHubSource = string.Empty;
                                CareerSlot routedSlot = null;

                                if (_userStore != null && !IsNullOrWhiteSpace(activeIdentityHash))
                                {
                                    try
                                    {
                                        routedSlot = _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false);
                                    }
                                    catch
                                    {
                                        routedSlot = null;
                                    }
                                }

                                if (TryParseRequestStoryHubForPayload(shared.Value.Data, out requestedHostCharacterId, out requestedHostAccountId))
                                {
                                    if (requestedHostAccountId != Guid.Empty && requestedHostAccountId == activeIdentityGuid)
                                    {
                                        routedHubId = routedSlot != null && !IsNullOrWhiteSpace(routedSlot.HubId)
                                            ? routedSlot.HubId
                                            : DefaultHubId;
                                        routedHubSource = "self-career";
                                    }
                                    else if (requestedHostAccountId != Guid.Empty && _hubPresenceRegistry.TryGetHubIdForAccount(requestedHostAccountId, out routedHubId))
                                    {
                                        routedHubSource = "host-account";
                                    }
                                    else if (!IsNullOrWhiteSpace(requestedHostCharacterId) && _hubPresenceRegistry.TryGetHubIdForCharacter(requestedHostCharacterId, out routedHubId))
                                    {
                                        routedHubSource = "host-character";
                                    }
                                }

                                if (IsNullOrWhiteSpace(routedHubId) && !IsNullOrWhiteSpace(currentHubInstanceId))
                                {
                                    routedHubId = currentHubInstanceId;
                                    routedHubSource = "current-peer";
                                }

                                if (!IsNullOrWhiteSpace(routedHubId))
                                {
                                    HubPresenceRegistry.Participant previousParticipant;
                                    _hubPresenceRegistry.TryGetParticipantForPeer(peer, out previousParticipant);

                                    var routedCharacterIdentifier = ResolveHubCharacterIdentifier(
                                        routedSlot,
                                        activeIdentityGuid,
                                        activeCareerIndex,
                                        previousParticipant != null ? previousParticipant.CharacterId : null);
                                    var routedCharacterName = routedSlot != null && !IsNullOrWhiteSpace(routedSlot.CharacterName)
                                        ? routedSlot.CharacterName
                                        : activeCharacterName;

                                    var transition = TryExecutePortedHubTransition(
                                        routedHubId,
                                        activeIdentityGuid,
                                        routedCharacterIdentifier,
                                        routedCharacterName,
                                        routedSlot,
                                        currentHubInstance);
                                    if (transition != null && transition.TargetHubInstance != null)
                                    {
                                        currentHubInstance = transition.TargetHubInstance;
                                        routedHubId = currentHubInstance.HubId;
                                    }

                                    var routedX = previousParticipant != null ? previousParticipant.X : 0f;
                                    var routedY = previousParticipant != null ? previousParticipant.Y : 0f;

                                    RegisterOrUpdateHubPresenceWithDuplicateRetire(
                                        peer,
                                        activeIdentityGuid,
                                        activeIdentityHash,
                                        activeCareerIndex,
                                        routedCharacterIdentifier,
                                        routedCharacterName,
                                        routedHubId,
                                        routedX,
                                        routedY,
                                        "request-story-hub-for");

                                    HubPresenceRegistry.Participant currentParticipant;
                                    _hubPresenceRegistry.TryGetParticipantForPeer(peer, out currentParticipant);

                                    var shouldBroadcastAdd = previousParticipant == null
                                        || !string.Equals(previousParticipant.HubId, routedHubId, StringComparison.OrdinalIgnoreCase)
                                        || !string.Equals(previousParticipant.CharacterId, routedCharacterIdentifier, StringComparison.OrdinalIgnoreCase);

                                    if (previousParticipant != null
                                        && !IsNullOrWhiteSpace(previousParticipant.HubId)
                                        && !IsNullOrWhiteSpace(previousParticipant.CharacterId)
                                        && !string.Equals(previousParticipant.HubId, routedHubId, StringComparison.OrdinalIgnoreCase))
                                    {
                                        BroadcastHubStateRemove(previousParticipant.HubId, peer, previousParticipant.CharacterId);
                                    }

                                    if (currentParticipant != null && !IsNullOrWhiteSpace(currentParticipant.HubId))
                                    {
                                        if (shouldBroadcastAdd)
                                        {
                                            ClearHubAnnouncementsForPeerHub(peer, currentParticipant.HubId);
                                        }

                                        armHubReadyFallback(currentParticipant.HubId, currentParticipant.CharacterId, "request-story-hub-for");

                                    }

                                    _logger.Log(new
                                    {
                                        ts = RequestLogger.UtcNowIso(),
                                        type = "hub-join-eval",
                                        path = "RequestStoryHubFor",
                                        peer = peer,
                                        previousHubId = previousParticipant != null ? (previousParticipant.HubId ?? string.Empty) : string.Empty,
                                        previousCharacterId = previousParticipant != null ? (previousParticipant.CharacterId ?? string.Empty) : string.Empty,
                                        currentHubId = currentParticipant != null ? (currentParticipant.HubId ?? string.Empty) : string.Empty,
                                        currentCharacterId = currentParticipant != null ? (currentParticipant.CharacterId ?? string.Empty) : string.Empty,
                                        shouldBroadcastAdd = shouldBroadcastAdd,
                                    });

                                    var serializedSharedHubState = currentHubInstance != null
                                        ? currentHubInstance.SerializedHubState()
                                        : BuildSerializedSharedHubStateOrFallback(routedHubId, routedCharacterIdentifier, routedCharacterName, routedSlot);
                                    cachedHubStatePayload = BuildMetaHubPushPayload(HubEntityId, serializedSharedHubState);
                                    currentHubInstanceId = routedHubId;

                                    _logger.Log(new
                                    {
                                        ts = RequestLogger.UtcNowIso(),
                                        type = "hub-route-storyhubfor",
                                        peer = peer,
                                        hostCharacterId = requestedHostCharacterId ?? string.Empty,
                                        hostAccountId = requestedHostAccountId != Guid.Empty ? requestedHostAccountId.ToString() : string.Empty,
                                        routedHubId = routedHubId,
                                        routeSource = routedHubSource,
                                    });
                                }

                                var requestMsgNoBase = direct.Value.MsgNo + 111;

                                try
                                {
                                    if (!ShouldSuppressDuplicateHubPush(peer, false, cachedHubStatePayload))
                                    {
                                        var hubStateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 37, cachedHubStatePayload), requestMsgNoBase + 1);
                                        SendRawFrame(stream, peer, PrefixLength(hubStateCore), "sent MetaGameplayCommunicationObject SendHubCommunicationObjectToClient in response to RequestStoryHubFor");
                                    }
                                }
                                catch
                                {
                                }

                                if (cachedCreationInfoPayload != null)
                                {
                                    try
                                    {
                                        if (!ShouldSuppressDuplicateHubPush(peer, true, cachedCreationInfoPayload))
                                        {
                                            var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, cachedCreationInfoPayload), requestMsgNoBase + 2);
                                            SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged in response to RequestStoryHubFor");
                                        }
                                    }
                                    catch
                                    {
                                    }
                                }

                            }

                            if (isMetaGameplayMessage)
                            {
                                var rawMessage = payloadStrings[0];

                                // Diagnostics: decode MetaGameplayCommunicationObject.SendMessage(...) payloads.
                                if (isMetaGameplayWrappedMessage && rawMessage != null)
                                {
                                    Interlocked.Increment(ref metaSendMessageSeen);

                                    string messageType = null;
                                    try
                                    {
                                        var msgDict = TryDeserializeJsonDict(rawMessage);
                                        var content = msgDict != null ? (msgDict.Contains("Content") ? msgDict["Content"] as IDictionary : null) : null;
                                        if (content != null)
                                        {
                                            messageType = GetStringValue(content, "TypeName");
                                            if (IsNullOrWhiteSpace(messageType))
                                            {
                                                // Fallback: use the first key as a rough hint.
                                                foreach (DictionaryEntry entry in content)
                                                {
                                                    if (entry.Key != null)
                                                    {
                                                        messageType = entry.Key.ToString();
                                                        break;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch
                                    {
                                        messageType = null;
                                    }

                                    _logger.Log(new
                                    {
                                        ts = RequestLogger.UtcNowIso(),
                                        type = "metagameplay-sendmessage",
                                        peer = peer,
                                        messageType = messageType ?? string.Empty,
                                        preview = rawMessage.Length > 240 ? rawMessage.Substring(0, 240) : rawMessage,
                                    });

                                    if (rawMessage.IndexOf("SetStoryMissionStateMessage", StringComparison.Ordinal) >= 0)
                                    {
                                        Interlocked.Increment(ref metaSetStoryMissionStateSeen);
                                    }
                                    if (rawMessage.IndexOf("StartSingleplayerMissionMessage", StringComparison.Ordinal) >= 0)
                                    {
                                        Interlocked.Increment(ref metaStartSingleplayerMissionSeen);
                                    }
                                    if (rawMessage.IndexOf("RequestCurrentStorylineHubMessage", StringComparison.Ordinal) >= 0)
                                    {
                                        Interlocked.Increment(ref metaRequestHubSeen);
                                    }
                                }

                                // The client requests hub state via a wrapped message:
                                // MetaGameplayCommunicationAdapter.RequestCurrentStorylineHub() -> SendMessage(new RequestCurrentStorylineHubMessage())
                                // We must respond by pushing hub state + creation-info; otherwise the UI can get stuck waiting.
                                if (isMetaGameplayWrappedMessage
                                    && rawMessage != null
                                    && rawMessage.IndexOf("RequestCurrentStorylineHubMessage", StringComparison.Ordinal) >= 0
                                    && cachedHubStatePayload != null)
                                {
                                    if (!IsNullOrWhiteSpace(currentHubInstanceId))
                                    {
                                        HubPresenceRegistry.Participant previousParticipant;
                                        _hubPresenceRegistry.TryGetParticipantForPeer(peer, out previousParticipant);

                                        CareerSlot currentSlot = null;
                                        if (_userStore != null && !IsNullOrWhiteSpace(activeIdentityHash))
                                        {
                                            try
                                            {
                                                currentSlot = _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false);
                                            }
                                            catch
                                            {
                                                currentSlot = null;
                                            }
                                        }

                                        var currentCharacterIdentifier = ResolveHubCharacterIdentifier(
                                            currentSlot,
                                            activeIdentityGuid,
                                            activeCareerIndex,
                                            previousParticipant != null ? previousParticipant.CharacterId : null);
                                        var currentCharacterName = currentSlot != null && !IsNullOrWhiteSpace(currentSlot.CharacterName)
                                            ? currentSlot.CharacterName
                                            : activeCharacterName;

                                        var effectiveHubId = currentHubInstanceId;
                                        Guid followHostAccountId;
                                        string followHostHubId;
                                        if (PartyHubFollowRegistry.TryGetHostForMember(activeIdentityGuid, out followHostAccountId)
                                            && followHostAccountId != Guid.Empty
                                            && followHostAccountId != activeIdentityGuid
                                            && _hubPresenceRegistry.TryGetHubIdForAccount(followHostAccountId, out followHostHubId)
                                            && !IsNullOrWhiteSpace(followHostHubId))
                                        {
                                            effectiveHubId = followHostHubId;
                                        }
                                        else if (currentSlot != null && !IsNullOrWhiteSpace(currentSlot.HubId))
                                        {
                                            effectiveHubId = currentSlot.HubId;
                                        }

                                        var transition = TryExecutePortedHubTransition(
                                            effectiveHubId,
                                            activeIdentityGuid,
                                            currentCharacterIdentifier,
                                            currentCharacterName,
                                            currentSlot,
                                            currentHubInstance);
                                        if (transition != null && transition.TargetHubInstance != null)
                                        {
                                            currentHubInstance = transition.TargetHubInstance;
                                            effectiveHubId = currentHubInstance.HubId;
                                        }

                                        currentHubInstanceId = effectiveHubId;

                                        var currentX = previousParticipant != null ? previousParticipant.X : 0f;
                                        var currentY = previousParticipant != null ? previousParticipant.Y : 0f;

                                        RegisterOrUpdateHubPresenceWithDuplicateRetire(
                                            peer,
                                            activeIdentityGuid,
                                            activeIdentityHash,
                                            activeCareerIndex,
                                            currentCharacterIdentifier,
                                            currentCharacterName,
                                            effectiveHubId,
                                            currentX,
                                            currentY,
                                            "request-current-storyline-hub");

                                        HubPresenceRegistry.Participant currentParticipant;
                                        _hubPresenceRegistry.TryGetParticipantForPeer(peer, out currentParticipant);

                                        var shouldBroadcastAdd = previousParticipant == null
                                            || !string.Equals(previousParticipant.HubId, effectiveHubId, StringComparison.OrdinalIgnoreCase)
                                            || !string.Equals(previousParticipant.CharacterId, currentCharacterIdentifier, StringComparison.OrdinalIgnoreCase);

                                        if (previousParticipant != null
                                            && !IsNullOrWhiteSpace(previousParticipant.HubId)
                                            && !IsNullOrWhiteSpace(previousParticipant.CharacterId)
                                            && !string.Equals(previousParticipant.HubId, effectiveHubId, StringComparison.OrdinalIgnoreCase))
                                        {
                                            BroadcastHubStateRemove(previousParticipant.HubId, peer, previousParticipant.CharacterId);
                                        }

                                        if (currentParticipant != null && !IsNullOrWhiteSpace(currentParticipant.HubId))
                                        {
                                            if (shouldBroadcastAdd)
                                            {
                                                ClearHubAnnouncementsForPeerHub(peer, currentParticipant.HubId);
                                            }

                                            armHubReadyFallback(currentParticipant.HubId, currentParticipant.CharacterId, "request-current-storyline-hub");

                                        }

                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "hub-join-eval",
                                            path = "RequestCurrentStorylineHubMessage",
                                            peer = peer,
                                            previousHubId = previousParticipant != null ? (previousParticipant.HubId ?? string.Empty) : string.Empty,
                                            previousCharacterId = previousParticipant != null ? (previousParticipant.CharacterId ?? string.Empty) : string.Empty,
                                            currentHubId = currentParticipant != null ? (currentParticipant.HubId ?? string.Empty) : string.Empty,
                                            currentCharacterId = currentParticipant != null ? (currentParticipant.CharacterId ?? string.Empty) : string.Empty,
                                            shouldBroadcastAdd = shouldBroadcastAdd,
                                        });

                                        var serializedSharedHubState = currentHubInstance != null
                                            ? currentHubInstance.SerializedHubState()
                                            : BuildSerializedSharedHubStateOrFallback(effectiveHubId, currentCharacterIdentifier, currentCharacterName, currentSlot);
                                        cachedHubStatePayload = BuildMetaHubPushPayload(HubEntityId, serializedSharedHubState);
                                    }

                                    var requestMsgNoBase = direct.Value.MsgNo + 110;

                                    if (!ShouldSuppressDuplicateHubPush(peer, false, cachedHubStatePayload))
                                    {
                                        var hubStateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 37, cachedHubStatePayload), requestMsgNoBase + 1);
                                        SendRawFrame(stream, peer, PrefixLength(hubStateCore), "sent MetaGameplayCommunicationObject SendHubCommunicationObjectToClient in response to RequestCurrentStorylineHubMessage");
                                    }

                                    if (cachedCreationInfoPayload != null)
                                    {
                                        if (!ShouldSuppressDuplicateHubPush(peer, true, cachedCreationInfoPayload))
                                        {
                                            var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, cachedCreationInfoPayload), requestMsgNoBase + 2);
                                            SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged in response to RequestCurrentStorylineHubMessage");
                                        }
                                    }

                                }

                                // Character creation / customization sends a CharacterChangeCollection that includes
                                // NameChange: { OldName: "NewRunner", NewName: "ShadowZero" }.
                                // Persist that NewName to the active career slot so it survives restarts.
                                if (_userStore != null
                                    && rawMessage != null
                                    && (isMetaGameplayChangeCharacter || rawMessage.IndexOf("CharacterChangeCollection", StringComparison.Ordinal) >= 0))
                                {
                                    var hasAnyRelevantChange = rawMessage.IndexOf("\"NewName\"", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("SkinTextureIndexChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("BackgroundStoryChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("BodyChange", StringComparison.Ordinal) >= 0;

                                    if (hasAnyRelevantChange)
                                    {
                                        var slotIndex = activeCareerIndex;
                                        if (slotIndex < 0)
                                        {
                                            slotIndex = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetLastCareerIndex(activeIdentityHash) : 0;
                                        }

                                        var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, slotIndex, false) : null;
                                        if (slot != null)
                                        {
                                            var changed = false;

                                            var newName = ExtractJsonStringValue(rawMessage, "NewName");
                                            if (!IsNullOrWhiteSpace(newName) && !string.Equals(slot.CharacterName, newName, StringComparison.Ordinal))
                                            {
                                                slot.CharacterName = newName;
                                                changed = true;
                                            }

                                            if (!slot.IsOccupied)
                                            {
                                                slot.IsOccupied = true;
                                                changed = true;
                                            }

                                            // Apply appearance changes if present.
                                            if (rawMessage.IndexOf("SkinTextureIndexChange", StringComparison.Ordinal) >= 0)
                                            {
                                                int skin;
                                                if (TryParseInt32(ExtractJsonStringValue(rawMessage, "NewIndex"), out skin))
                                                {
                                                    if (slot.SkinTextureIndex != skin)
                                                    {
                                                        slot.SkinTextureIndex = skin;
                                                        changed = true;
                                                    }
                                                }
                                            }
                                            if (rawMessage.IndexOf("BackgroundStoryChange", StringComparison.Ordinal) >= 0)
                                            {
                                                ulong story;
                                                if (TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewStory"), out story))
                                                {
                                                    if (slot.BackgroundStory != story)
                                                    {
                                                        slot.BackgroundStory = story;
                                                        changed = true;
                                                    }
                                                }
                                            }
                                            if (rawMessage.IndexOf("BodyChange", StringComparison.Ordinal) >= 0)
                                            {
                                                ulong meta;
                                                ulong gender;
                                                if (TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewMetatype"), out meta)
                                                    && TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewGender"), out gender))
                                                {
                                                    ulong bodytype;
                                                    if (TryResolveBodytypeId(meta, gender, out bodytype) && bodytype != 0UL)
                                                    {
                                                        if (slot.Bodytype != bodytype)
                                                        {
                                                            slot.Bodytype = bodytype;
                                                            changed = true;
                                                        }
                                                    }
                                                }
                                            }

                                            if (changed)
                                            {
                                                // After a successful creation/edit flow, treat it as committed.
                                                if (slot.PendingPersistenceCreation)
                                                {
                                                    slot.PendingPersistenceCreation = false;
                                                }
                                                if (!IsNullOrWhiteSpace(activeIdentityHash))
                                                {
                                                    _userStore.UpsertCareer(activeIdentityHash, slot);
                                                }

                                                if (!IsNullOrWhiteSpace(slot.CharacterName))
                                                {
                                                    activeCharacterName = slot.CharacterName;
                                                }

                                                // Refresh cached hub payload (used later when client requests hub state).
                                                string refreshedHubId;
                                                cachedHubStatePayload = BuildPortedHubStatePayloadForSlot(
                                                    slot,
                                                    activeIdentityGuid,
                                                    slotIndex,
                                                    false,
                                                    currentHubInstance,
                                                    out refreshedHubId,
                                                    out currentHubInstance);
                                                currentHubInstanceId = refreshedHubId;

                                                // Nudge client UI lists.
                                                var msgNoBase = direct.Value.MsgNo + 2;
                                                var summaryJson = BuildCareerSummaryJson(!IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetCareers(activeIdentityHash) : null);
                                                var updatePayload = BuildUtf16StringPayload(summaryJson);
                                                var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 2, 16, updatePayload), msgNoBase);
                                                SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries after CharacterChangeCollection");

                                                // Send an updated metagame snapshot so the client doesn't revert to earlier defaults.
                                                try
                                                {
                                                    var zipped = _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, slotIndex, slot);
                                                    var metaSnapshotPayload = BuildUtf16StringPayload(zipped);
                                                    var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), msgNoBase + 1);
                                                    SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient after CharacterChangeCollection");
                                                }
                                                catch
                                                {
                                                }
                                            }
                                        }
                                    }
                                }

                                // Client-to-server request to update story progression.
                                // In the real game this updates the authoritative metagame state and is broadcast back.
                                // Important subtlety: the client UI updates from *server* StoryprogressChanged (MissionStateChange),
                                // not from its own outgoing SetStoryMissionStateMessage.
                                if (TryHandleSetStoryMissionStateMessage(
                                    isMetaGameplayWrappedMessage,
                                    rawMessage,
                                    direct.Value.MsgNo,
                                    stream,
                                    peer,
                                    completedStoryMissions,
                                    activeIdentityHash,
                                    activeIdentityGuid,
                                    activeCareerIndex,
                                    ref currentHubInstanceId,
                                    ref currentHubInstance,
                                    ref cachedHubStatePayload,
                                    cachedCreationInfoPayload))
                                {
                                }

                                // Persist NPC interactions so dialog/new-marker state behaves like retail on relaunch.
                                if (isMetaGameplayWrappedMessage && rawMessage != null && rawMessage.IndexOf("InteractedWithNpcMessage", StringComparison.Ordinal) >= 0)
                                {
                                    var storylineId = ExtractJsonStringValue(rawMessage, "StorylineId");
                                    var npcId = ExtractJsonStringValue(rawMessage, "NpcId");
                                    if (_userStore != null && !IsNullOrWhiteSpace(npcId) && string.Equals(storylineId, "Main Campaign", StringComparison.OrdinalIgnoreCase))
                                    {
                                        try
                                        {
                                            var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                                            if (slot != null)
                                            {
                                                if (slot.MainCampaignInteractedNpcs == null)
                                                {
                                                    slot.MainCampaignInteractedNpcs = new List<string>();
                                                }
                                                if (!slot.MainCampaignInteractedNpcs.Contains(npcId))
                                                {
                                                    slot.MainCampaignInteractedNpcs.Add(npcId);
                                                    _userStore.UpsertCareer(activeIdentityHash, slot);
                                                }
                                            }
                                        }
                                        catch
                                        {
                                        }
                                    }

                                    // Echoing is optional; the client already updates locally before sending.
                                    var echoPayload = BuildUtf16StringPayload(rawMessage);
                                    var echoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 1, echoPayload), direct.Value.MsgNo + 4);
                                    SendRawFrame(stream, peer, PrefixLength(echoCore), "echoed MetaGameplayCommunicationObject Message (InteractedWithNpcMessage)");
                                }

                                if (isMetaGameplayWrappedMessage && rawMessage != null && rawMessage.IndexOf("StartSingleplayerMissionMessage", StringComparison.Ordinal) >= 0)
                                {
                                    var mapName = ExtractJsonStringValue(rawMessage, "MapName");
                                    if (IsNullOrWhiteSpace(mapName))
                                    {
                                        mapName = "1_010_Prologue";
                                    }

                                    var parsedSelections = TryExtractHenchmanSelections(rawMessage);

                                    currentMissionMapName = mapName;

                                    if (IsMissionCompletedForCareer(activeIdentityHash, activeCareerIndex, mapName, completedStoryMissions))
                                    {
                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "metagameplay",
                                            peer = peer,
                                            action = "start-mission-cancelled",
                                            reason = "mission-completed",
                                            mapName = mapName,
                                        });

                                        // The client is now waiting for either StartMissionAccepted or StartMissionCancelled.
                                        // If we do neither, it will remain stuck in a mission-start-in-progress state.
                                        var nudgeMsgNoBase = direct.Value.MsgNo + 250;
                                        var cancelledCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 24, new byte[0]), nudgeMsgNoBase);
                                        SendRawFrame(stream, peer, PrefixLength(cancelledCore), "sent MetaGameplayCommunicationObject StartMissionCancelled (mission already completed)");

                                        // Nudge the client back to the hub state we already advertise.
                                        if (cachedHubStatePayload != null && cachedCreationInfoPayload != null)
                                        {
                                            var hubStateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 37, cachedHubStatePayload), nudgeMsgNoBase + 1);
                                            SendRawFrame(stream, peer, PrefixLength(hubStateCore), "sent MetaGameplayCommunicationObject SendHubCommunicationObjectToClient (mission already completed)");

                                            var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, cachedCreationInfoPayload), nudgeMsgNoBase + 2);
                                            SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged (mission already completed)");
                                        }
                                        continue;
                                    }

                                    var requestMsgNoBase = direct.Value.MsgNo + 250;

                                    if (!sentMissionEntityIntros)
                                    {
                                        var gameworldIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes((ulong)gameworldEntityId), BitConverter.GetBytes(gameworldCommunicationObjectTypeId), BitConverter.GetBytes(0));
                                        var gameworldIntroCore = BuildCoreDirectSystem(1, gameworldIntroRaw, requestMsgNoBase + 1);
                                        SendRawFrame(stream, peer, PrefixLength(gameworldIntroCore), "sent AP introduce shared entity (type=7 gameworld communication object, id=5)");

                                        var missionInstanceIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes((ulong)missionInstanceEntityId), BitConverter.GetBytes(missionInstanceCommunicationObjectTypeId), BitConverter.GetBytes(0));
                                        var missionInstanceIntroCore = BuildCoreDirectSystem(1, missionInstanceIntroRaw, requestMsgNoBase + 2);
                                        SendRawFrame(stream, peer, PrefixLength(missionInstanceIntroCore), "sent AP introduce shared entity (type=9 mission instance communication object, id=6)");

                                        var missionCommandIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes((ulong)missionCommandEntityId), BitConverter.GetBytes(missionCommandCommunicationObjectTypeId), BitConverter.GetBytes(0));
                                        var missionCommandIntroCore = BuildCoreDirectSystem(1, missionCommandIntroRaw, requestMsgNoBase + 3);
                                        SendRawFrame(stream, peer, PrefixLength(missionCommandIntroCore), "sent AP introduce shared entity (type=10 mission command communication object, id=7)");

                                        var gameworldOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(gameworldEntityId, gameworldCommunicationObjectTypeId), requestMsgNoBase + 4);
                                        SendRawFrame(stream, peer, PrefixLength(gameworldOwnerCore), "sent AP shared-entity set-owner (entity=5)");

                                        var missionInstanceOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(missionInstanceEntityId, missionInstanceCommunicationObjectTypeId), requestMsgNoBase + 5);
                                        SendRawFrame(stream, peer, PrefixLength(missionInstanceOwnerCore), "sent AP shared-entity set-owner (entity=6)");

                                        var missionCommandOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(missionCommandEntityId, missionCommandCommunicationObjectTypeId), requestMsgNoBase + 6);
                                        SendRawFrame(stream, peer, PrefixLength(missionCommandOwnerCore), "sent AP shared-entity set-owner (entity=7)");

                                        sentMissionEntityIntros = true;
                                    }

                                    var seed0 = 0x11111111u;
                                    var seed1 = 0x22222222u;
                                    var seed2 = 0x33333333u;
                                    var seed3 = 0x44444444u;

                                    PlayerCharacterSnapshot[] selectedHenchmen = null;
                                    if (parsedSelections != null && parsedSelections.Count > 0)
                                    {
                                        // Ensure the hench cache is warm so we can map HenchmanId -> snapshot.
                                        SerializeDefaultHenchmanCollection();

                                        var snapshots = CachedHenchmanCollectionSnapshots;
                                        if (snapshots != null && snapshots.Count > 0)
                                        {
                                            var ownerKarma = 0;
                                            var ownerSpentKarma = 0;
                                            var ownerNuyen = 0;
                                            CareerSlot slotForWallet = null;
                                            if (_userStore != null)
                                            {
                                                try
                                                {
                                                    slotForWallet = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                                                }
                                                catch
                                                {
                                                    slotForWallet = null;
                                                }
                                            }
                                            if (slotForWallet != null)
                                            {
                                                ownerKarma = slotForWallet.Karma;
                                                ownerSpentKarma = slotForWallet.SpentKarma;
                                                ownerNuyen = slotForWallet.Nuyen;
                                            }

                                            var resolved = new List<PlayerCharacterSnapshot>();
                                            for (var i = 0; i < parsedSelections.Count; i++)
                                            {
                                                var selection = parsedSelections[i];

                                                // Selection points into the collection we sent to the client.
                                                if (selection.HenchmanId < 0 || selection.HenchmanId >= snapshots.Count)
                                                {
                                                    continue;
                                                }

                                                var src = snapshots[selection.HenchmanId];
                                                var clone = CloneHenchSnapshotForMission(src, activeIdentityGuid, i, ownerKarma, ownerSpentKarma, ownerNuyen);
                                                if (clone != null)
                                                {
                                                    resolved.Add(clone);
                                                }
                                            }

                                            if (resolved.Count > 0)
                                            {
                                                selectedHenchmen = resolved.ToArray();
                                            }

                                            _logger.Log(new
                                            {
                                                ts = RequestLogger.UtcNowIso(),
                                                type = "mission-start",
                                                peer = peer,
                                                mapName = mapName,
                                                henchSelectionCount = parsedSelections.Count,
                                                henchResolvedCount = selectedHenchmen != null ? selectedHenchmen.Length : 0,
                                                henchCollectionCreationIndex = CachedHenchmanCollectionCreationIndex,
                                                henchSelectionCreationIndex = parsedSelections != null && parsedSelections.Count > 0 ? (int?)parsedSelections[0].CollectionCreationIndex : null,
                                            });
                                        }
                                    }

                                    var compressedMatchConfiguration = (selectedHenchmen != null && selectedHenchmen.Length > 0)
                                        ? _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeCharacterName, selectedHenchmen, gameClientEntityId)
                                        : _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeCharacterName, gameClientEntityId);
                                    if (_userStore != null)
                                    {
                                        try
                                        {
                                            var activeSlot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                                            if (activeSlot != null)
                                            {
                                                if (selectedHenchmen != null && selectedHenchmen.Length > 0)
                                                {
                                                    compressedMatchConfiguration = _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeSlot, selectedHenchmen, gameClientEntityId);
                                                }
                                                else
                                                {
                                                    compressedMatchConfiguration = _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeSlot, null, gameClientEntityId);
                                                }
                                            }
                                        }
                                        catch
                                        {
                                        }
                                    }

                                    // Create / reset the authoritative simulation for this mission.
                                    // This uses StaticDataLoader.CreateForClient against the extracted JSON tree under LocalServiceRoot/static-data.
                                    try
                                    {
                                        var storyLineForLoot = "Main Campaign";
                                        var chapterForLoot = 0;
                                        if (_userStore != null)
                                        {
                                            try
                                            {
                                                var slotForLoot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                                                if (slotForLoot != null)
                                                {
                                                    chapterForLoot = slotForLoot.MainCampaignCurrentChapter;
                                                }
                                            }
                                            catch
                                            {
                                            }
                                        }

                                        simulationSession = ServerSimulationSession.Create(
                                            _logger,
                                            peer,
                                            _options.StaticDataDir,
                                            _options.StreamingAssetsDir,
                                            mapName,
                                            compressedMatchConfiguration,
                                            seed0,
                                            seed1,
                                            seed2,
                                            seed3,
                                            storyLineForLoot,
                                            chapterForLoot,
                                            _options != null && _options.EnableAiLogic);

                                        if (simulationSession != null)
                                        {
                                            MissionRuntimeRegistry.MarkSoloMissionStarted(peer);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        simulationSession = null;
                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "sim",
                                            peer = peer,
                                            status = "failed",
                                            mapName = mapName,
                                            message = ex.Message,
                                        });
                                    }

                                    var startMissionAcceptedPayload = Concat(
                                        BitConverter.GetBytes(1L),
                                        BitConverter.GetBytes(seed0),
                                        BitConverter.GetBytes(seed1),
                                        BitConverter.GetBytes(seed2),
                                        BitConverter.GetBytes(seed3),
                                        BuildUtf16StringPayload(compressedMatchConfiguration),
                                        BitConverter.GetBytes((ulong)gameworldEntityId),
                                        BitConverter.GetBytes((ulong)missionInstanceEntityId),
                                        BitConverter.GetBytes((ulong)missionCommandEntityId));

                                    var startMissionAcceptedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 23, startMissionAcceptedPayload), requestMsgNoBase + 7);
                                    SendRawFrame(stream, peer, PrefixLength(startMissionAcceptedCore), "sent MetaGameplayCommunicationObject StartMissionAccepted (map=" + mapName + ")");

                                    SleepWithStop(stopEvent, 6000);
                                    var startMissionForClientsCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, missionInstanceEntityId, 0, new byte[0]), requestMsgNoBase + 8);
                                    SendRawFrame(stream, peer, PrefixLength(startMissionForClientsCore), "sent MissionInstanceCommunicationObject StartMissionForClients");
                                }
                            }

                            var isMissionCommandCall = shared.Value.ApMsgId == 1 && shared.Value.EntityId == missionCommandEntityId;
                            if (isMissionCommandCall)
                            {
                                HandleMissionCommandCall(
                                    stream,
                                    peer,
                                    shared.Value.FieldId,
                                    shared.Value.Data,
                                    direct.Value.MsgNo,
                                    gameworldEntityId,
                                    gameClientEntityId,
                                    activeIdentityHash,
                                    activeIdentityGuid,
                                    activeCareerIndex,
                                    activeCharacterName,
                                    currentMissionMapName,
                                    completedStoryMissions,
                                    ref currentCoopGroupName,
                                    ref simulationSession,
                                    ref simulationSessionSync,
                                    ref cachedHubStatePayload,
                                    cachedCreationInfoPayload);
                            }
                        }

                        TryFlushPendingCharacterStatePushes(activeIdentityGuid, activeIdentityHash, activeCareerIndex, peer, stream);

                        var chunk = ReadChunk(stream);
                        if (chunk.Length == 0)
                        {
                            if (simulationSession != null && IsNullOrWhiteSpace(currentCoopGroupName))
                            {
                                MissionRuntimeRegistry.MarkSoloMissionEnded(peer);
                            }

                            cancelHubReadyFallback("socket-closed");
                            connectionClosed.Set();
                            HubPresenceRegistry.Participant disconnectedParticipant;
                            _hubPresenceRegistry.TryGetParticipantForPeer(peer, out disconnectedParticipant);
                            if (currentHubInstance != null && disconnectedParticipant != null && !IsNullOrWhiteSpace(disconnectedParticipant.CharacterId))
                            {
                                _portedHubInstanceManager.RemoveCharacterFromHub(currentHubInstance, disconnectedParticipant.CharacterId);
                                currentHubInstance = null;
                            }
                            RemoveHubPresenceWithBroadcast(peer);
                            _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "aplay-conn", peer = peer, note = "socket closed" });
                            break;
                        }

                        buffer.AddRange(chunk);
                    }

                    if (!IsNullOrWhiteSpace(currentCoopGroupName))
                    {
                        UnregisterCoopMissionParticipant(currentCoopGroupName, peer);
                    }

                    cancelHubReadyFallback("connection-teardown");
                    HubPresenceRegistry.Participant teardownParticipant;
                    _hubPresenceRegistry.TryGetParticipantForPeer(peer, out teardownParticipant);
                    if (currentHubInstance != null && teardownParticipant != null && !IsNullOrWhiteSpace(teardownParticipant.CharacterId))
                    {
                        _portedHubInstanceManager.RemoveCharacterFromHub(currentHubInstance, teardownParticipant.CharacterId);
                        currentHubInstance = null;
                    }
                    RemoveHubPresenceWithBroadcast(peer);
                    UnregisterHubPeerStream(peer, stream);
                }
            }
        }

        private void RegisterCoopMissionParticipant(string coopGroupName, string peer, NetworkStream stream)
        {
            if (IsNullOrWhiteSpace(coopGroupName) || stream == null)
            {
                return;
            }

            lock (_coopMissionLock)
            {
                List<CoopMissionParticipant> list;
                if (!_coopMissionParticipants.TryGetValue(coopGroupName, out list) || list == null)
                {
                    list = new List<CoopMissionParticipant>();
                    _coopMissionParticipants[coopGroupName] = list;
                }

                // Remove existing entries for this peer (reconnects).
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] == null || string.Equals(list[i].Peer, peer, StringComparison.OrdinalIgnoreCase))
                    {
                        list.RemoveAt(i);
                    }
                }

                list.Add(new CoopMissionParticipant(peer, stream));
            }
        }

        private void UnregisterCoopMissionParticipant(string coopGroupName, string peer)
        {
            if (IsNullOrWhiteSpace(coopGroupName) || IsNullOrWhiteSpace(peer))
            {
                return;
            }

            lock (_coopMissionLock)
            {
                List<CoopMissionParticipant> list;
                if (!_coopMissionParticipants.TryGetValue(coopGroupName, out list) || list == null)
                {
                    return;
                }

                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] == null || string.Equals(list[i].Peer, peer, StringComparison.OrdinalIgnoreCase))
                    {
                        list.RemoveAt(i);
                    }
                }

                if (list.Count == 0)
                {
                    _coopMissionParticipants.Remove(coopGroupName);
                    _coopMissionHenchSelections.Remove(coopGroupName);

                    CoopMissionSessionState session;
                    if (_coopMissionSessions.TryGetValue(coopGroupName, out session) && session != null)
                    {
                        _coopMissionSessions.Remove(coopGroupName);
                        try
                        {
                            lock (session.SyncRoot)
                            {
                                if (session.Simulation != null)
                                {
                                    session.Simulation.Stop();
                                    session.Simulation = null;
                                    MissionRuntimeRegistry.MarkCoopMissionEnded(coopGroupName);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private void BroadcastToCoopMissionPeers(string coopGroupName, string senderPeer, byte[] decoded, string note)
        {
            if (IsNullOrWhiteSpace(coopGroupName) || decoded == null || decoded.Length == 0)
            {
                return;
            }

            CoopMissionParticipant[] targets = null;
            lock (_coopMissionLock)
            {
                List<CoopMissionParticipant> list;
                if (!_coopMissionParticipants.TryGetValue(coopGroupName, out list) || list == null || list.Count == 0)
                {
                    return;
                }
                targets = list.ToArray();
            }

            for (var i = 0; i < targets.Length; i++)
            {
                var t = targets[i];
                if (t == null || t.Stream == null)
                {
                    continue;
                }
                if (!IsNullOrWhiteSpace(senderPeer) && string.Equals(t.Peer, senderPeer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    SendRawFrame(t.Stream, t.Peer, decoded, note);
                }
                catch
                {
                    // Drop broken streams on next unregister; avoid throwing in main loop.
                }
            }
        }

        private void HandleHttpProbe(NetworkStream stream, string peer, byte[] first)
        {
            var firstLine = Encoding.ASCII.GetString(first).Split(new[] { "\r\n" }, 2, StringSplitOptions.None)[0];
            var advertisedServerAddress = TryGetConfiguredAPlayServerAddress() ?? string.Format("127.0.0.1:{0}", _options.APlayPort);
            var body = firstLine != null && firstLine.IndexOf("/servers", StringComparison.OrdinalIgnoreCase) >= 0
                ? Encoding.ASCII.GetBytes(advertisedServerAddress)
                : Encoding.ASCII.GetBytes("OK");

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.0 200 OK\r\n"
                + "Content-Type: text/plain; charset=utf-8\r\n"
                + "Connection: close\r\n"
                + "Content-Length: " + body.Length + "\r\n\r\n");

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "aplay-http-reply",
                requestLine = firstLine,
                body = Encoding.ASCII.GetString(body),
                contentLength = body.Length,
            });

            stream.Write(response, 0, response.Length);
            stream.Write(body, 0, body.Length);
        }

        private string TryGetConfiguredAPlayServerAddress()
        {
            // The game hits the APlay endpoint as an HTTP probe ("/servers*.txt") and expects the response
            // to contain the address it should connect to. For non-local hosting, this must match the
            // served BaseConfiguration.ServerAddress value.
            try
            {
                if (_options == null || IsNullOrWhiteSpace(_options.ConfigDir))
                {
                    return null;
                }

                var configPath = Path.Combine(_options.ConfigDir, "config.xml");
                if (!File.Exists(configPath))
                {
                    return null;
                }

                var doc = new XmlDocument();
                doc.Load(configPath);

                // config.xml doesn't currently use a default XML namespace, but be resilient anyway.
                var node = doc.SelectSingleNode("//ServerAddress") ?? doc.SelectSingleNode("//*[local-name()='ServerAddress']");
                var value = node != null ? (node.InnerText ?? string.Empty).Trim() : null;
                if (IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                // Be tolerant of accidental scheme/path additions (e.g. "http://host:5055/").
                if (value.IndexOf("://", StringComparison.Ordinal) > 0)
                {
                    Uri uri;
                    if (Uri.TryCreate(value, UriKind.Absolute, out uri) && !IsNullOrWhiteSpace(uri.Host))
                    {
                        var port = uri.IsDefaultPort ? _options.APlayPort : uri.Port;
                        return string.Format("{0}:{1}", uri.Host, port);
                    }
                }

                return value;
            }
            catch
            {
                return null;
            }
        }

        private void SendRawFrame(NetworkStream stream, string peer, byte[] decoded, string note)
        {
            var frameBytes = Encoding.ASCII.GetBytes(Convert.ToBase64String(decoded) + "\0");
            try
            {
                lock (stream)
                {
                    stream.Write(frameBytes, 0, frameBytes.Length);
                }
            }
            catch (ObjectDisposedException)
            {
                _logger.LogLow(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "aplay-frame-send-disconnected",
                    peer = peer,
                    note = note,
                    reason = "stream-disposed",
                });
                return;
            }
            catch (IOException ioex)
            {
                var socketErrorCode = string.Empty;
                var socketEx = ioex.InnerException as SocketException;
                if (socketEx != null)
                {
                    socketErrorCode = socketEx.SocketErrorCode.ToString();
                }

                _logger.LogLow(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "aplay-frame-send-disconnected",
                    peer = peer,
                    note = note,
                    reason = "io-exception",
                    message = ioex.Message,
                    socketErrorCode = socketErrorCode,
                });
                return;
            }
            catch (SocketException sex)
            {
                _logger.LogLow(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "aplay-frame-send-disconnected",
                    peer = peer,
                    note = note,
                    reason = "socket-exception",
                    message = sex.Message,
                    socketErrorCode = sex.SocketErrorCode.ToString(),
                });
                return;
            }

            var payload = new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "aplay-frame-sent",
                peer = peer,
                frame = Encoding.ASCII.GetString(frameBytes, 0, frameBytes.Length - 1),
                decodedLen = decoded.Length,
                decodedHex = ToHexString(decoded, 0, decoded.Length).ToLowerInvariant(),
                note = note,
            };

            // Keep-alives are high-frequency and drown out useful signal.
            if (note != null && note.IndexOf("KeepAlive", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _logger.LogLow(payload);
            }
            else
            {
                _logger.Log(payload);
            }
        }

        private static bool LooksLikeHttp(byte[] bytes)
        {
            return StartsWithAscii(bytes, "GET ") || StartsWithAscii(bytes, "POST ") || StartsWithAscii(bytes, "HEAD ");
        }

        private static bool StartsWithAscii(byte[] bytes, string prefix)
        {
            if (bytes == null || prefix == null)
            {
                return false;
            }
            var p = Encoding.ASCII.GetBytes(prefix);
            return StartsWith(bytes, p);
        }

        private static bool StartsWith(byte[] bytes, byte[] prefix)
        {
            if (bytes == null || prefix == null || bytes.Length < prefix.Length)
            {
                return false;
            }
            for (var i = 0; i < prefix.Length; i++)
            {
                if (bytes[i] != prefix[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryExtractNullTerminatedFrame(List<byte> buffer, out byte[] frame)
        {
            var nullIndex = buffer.IndexOf(0);
            if (nullIndex < 0)
            {
                frame = new byte[0];
                return false;
            }

            frame = buffer.Take(nullIndex).ToArray();
            buffer.RemoveRange(0, nullIndex + 1);
            return true;
        }

        private static byte[] ReadChunk(NetworkStream stream)
        {
            var buffer = new byte[4096];
            try
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return new byte[0];
                }
                if (read == buffer.Length)
                {
                    return buffer;
                }
                var slice = new byte[read];
                Buffer.BlockCopy(buffer, 0, slice, 0, read);
                return slice;
            }
            catch
            {
                return new byte[0];
            }
        }

        private static byte[] PrefixLength(byte[] payload)
        {
            return Concat(BitConverter.GetBytes(payload.Length), payload);
        }

        private static byte[] BuildCoreDirectSystem(uint serverId, byte[] raw, ulong msgNo)
        {
            return Concat(new byte[] { 0x02 }, BitConverter.GetBytes(serverId), BitConverter.GetBytes(raw.Length), raw, BitConverter.GetBytes(msgNo));
        }

        private static byte[] BuildApSharedFieldEvent(byte apMsgId, ulong entityId, ushort fieldId, byte[] data)
        {
            return Concat(new[] { apMsgId }, BitConverter.GetBytes(entityId), BitConverter.GetBytes(fieldId), BitConverter.GetBytes(data.Length), data);
        }

        private static byte[] BuildApSharedEntitySetOwner(ulong entityId, ushort typeId)
        {
            return Concat(new byte[] { 7 }, BitConverter.GetBytes(entityId), BitConverter.GetBytes(typeId));
        }

        private static byte[] BuildGameClientWelcomePayload(ulong accountRefId, string careerSummary)
        {
            return Concat(BitConverter.GetBytes(accountRefId), BuildUtf16StringPayload(careerSummary));
        }

        private static byte[] BuildAccountWelcomePayload(int index, string zippedCareerInfo, ulong metaGameplayRef)
        {
            return Concat(BitConverter.GetBytes(index), BitConverter.GetBytes(metaGameplayRef), BuildUtf16StringPayload(zippedCareerInfo), BuildApDatePayload(DateTimeOffset.UtcNow));
        }

        private static byte[] BuildMetaHubPushPayload(ulong hubRefId, string serializedState)
        {
            return Concat(BitConverter.GetBytes(hubRefId), BuildUtf16StringPayload(serializedState));
        }

        private static byte[] BuildApDatePayload(DateTimeOffset dt)
        {
            var utc = dt.ToUniversalTime();
            return Concat(
                new[] { (byte)utc.Day },
                new[] { (byte)utc.Month },
                BitConverter.GetBytes((ushort)utc.Year),
                new[] { (byte)utc.Hour },
                new[] { (byte)utc.Minute },
                new[] { (byte)utc.Second },
                BitConverter.GetBytes((ushort)utc.Millisecond));
        }

        private static string BuildCareerSummaryJson(List<CareerSlot> careers)
        {
            // JSON array of objects: { Name, Portrait, Index, IsOccupied }
            // Keep the shape identical to the previous hardcoded stub.
            var slots = new Dictionary<int, CareerSlot>();
            if (careers != null)
            {
                for (var i = 0; i < careers.Count; i++)
                {
                    var s = careers[i];
                    if (s == null)
                    {
                        continue;
                    }
                    slots[s.Index] = s;
                }
            }

            var sb = new StringBuilder();
            sb.Append("[");
            for (var idx = 0; idx < 6; idx++)
            {
                CareerSlot s;
                if (!slots.TryGetValue(idx, out s) || s == null)
                {
                    s = new CareerSlot();
                    s.Index = idx;
                    s.IsOccupied = false;
                    s.CharacterName = string.Empty;
                    s.Portrait = string.Empty;
                }

                // Career selection UI expects a non-empty portrait path for occupied careers.
                // Older persisted slots (and our initial defaults) can have an empty Portrait/PortraitPath.
                var portrait = s.Portrait;
                if (IsNullOrWhiteSpace(portrait))
                {
                    portrait = s.PortraitPath;
                }
                if (IsNullOrWhiteSpace(portrait) && s.IsOccupied)
                {
                    portrait = PlayerCharacterDefaultValues.PortraitPath;
                }

                if (idx > 0)
                {
                    sb.Append(",");
                }

                sb.Append("{\"Name\":\"");
                sb.Append(JsonEscape(s.CharacterName));
                sb.Append("\",\"Portrait\":\"");
                sb.Append(JsonEscape(portrait));
                sb.Append("\",\"Index\":");
                sb.Append(idx.ToString());
                sb.Append(",\"IsOccupied\":");
                sb.Append(s.IsOccupied ? "true" : "false");
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static string BuildDefaultCareerSummary()
        {
            return BuildCareerSummaryJson(null);
        }

        private static string JsonEscape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static int? ParseInt32Payload(byte[] data)
        {
            if (data == null || data.Length < 4)
            {
                return null;
            }
            return ReadInt32LE(data, 0);
        }

        private static List<string> ParseUtf16StringPayload(byte[] data)
        {
            var output = new List<string>();
            var pos = 0;
            while (pos + 4 <= data.Length && output.Count < 8)
            {
                var strlen = ReadInt32LE(data, pos);
                pos += 4;
                if (strlen < 0)
                {
                    break;
                }

                // Protect against integer overflow on (strlen * 2) and bogus lengths.
                // strlen is the number of UTF-16 code units, so the byte length must fit inside the remaining buffer.
                var remaining = data.Length - pos;
                if (strlen > (remaining / 2))
                {
                    break;
                }

                var byteLenLong = (long)strlen * 2L;
                if (byteLenLong < 0 || byteLenLong > int.MaxValue)
                {
                    break;
                }

                var byteLen = (int)byteLenLong;
                if (byteLen == 0)
                {
                    output.Add(string.Empty);
                    continue;
                }

                output.Add(Encoding.Unicode.GetString(data, pos, byteLen));
                pos += byteLen;
            }
            return output;
        }

        private static bool TryParsePrepareMatchPayload(
            byte[] data,
            out string matchIdentifier,
            out string players,
            out bool coop,
            out string mapName,
            out string selectedHenchmen)
        {
            matchIdentifier = null;
            players = null;
            coop = false;
            mapName = null;
            selectedHenchmen = null;

            if (data == null || data.Length < 13)
            {
                return false;
            }

            var pos = 0;
            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out matchIdentifier))
            {
                return false;
            }

            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out players))
            {
                return false;
            }

            if (pos >= data.Length)
            {
                return false;
            }

            coop = data[pos++] != 0;

            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out mapName))
            {
                return false;
            }

            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out selectedHenchmen))
            {
                return false;
            }

            return true;
        }

        private static bool TryReadUtf16LengthPrefixedString(byte[] data, ref int pos, out string value)
        {
            value = null;
            if (data == null || pos < 0 || pos + 4 > data.Length)
            {
                return false;
            }

            var strlen = ReadInt32LE(data, pos);
            pos += 4;
            if (strlen < 0)
            {
                return false;
            }

            var remaining = data.Length - pos;
            if (strlen > (remaining / 2))
            {
                return false;
            }

            var byteLenLong = (long)strlen * 2L;
            if (byteLenLong < 0 || byteLenLong > int.MaxValue)
            {
                return false;
            }

            var byteLen = (int)byteLenLong;
            value = byteLen == 0 ? string.Empty : Encoding.Unicode.GetString(data, pos, byteLen);
            pos += byteLen;
            return true;
        }

        private static byte[] BuildUtf16StringPayload(params string[] values)
        {
            var chunks = new List<byte[]>();
            foreach (var value in values)
            {
                var encoded = Encoding.Unicode.GetBytes(value ?? string.Empty);
                chunks.Add(BitConverter.GetBytes(encoded.Length / 2));
                chunks.Add(encoded);
            }
            return Concat(chunks.ToArray());
        }

        private static CoreDirectSystem? ParseCoreDirectSystem(byte[] corePayload)
        {
            if (corePayload.Length < 17 || corePayload[0] != 3)
            {
                return null;
            }

            var pos = 1;
            uint serverId;
            int rawLen;
            ulong msgNo;
            if (!TryReadUInt32LE(corePayload, ref pos, out serverId)) return null;
            if (!TryReadInt32LE(corePayload, ref pos, out rawLen)) return null;
            if (rawLen < 0 || pos + rawLen + 8 > corePayload.Length) return null;

            var raw = new byte[rawLen];
            Buffer.BlockCopy(corePayload, pos, raw, 0, rawLen);
            pos += rawLen;
            if (!TryReadUInt64LE(corePayload, ref pos, out msgNo)) return null;
            return new CoreDirectSystem(serverId, raw, msgNo);
        }

        private static ApSharedFieldEvent? ParseApSharedFieldEvent(byte[] raw)
        {
            if (raw.Length < 15)
            {
                return null;
            }

            var pos = 0;
            var apMsgId = raw[pos++];
            ulong entityId;
            ushort fieldId;
            int dataLen;
            if (!TryReadUInt64LE(raw, ref pos, out entityId)) return null;
            if (!TryReadUInt16LE(raw, ref pos, out fieldId)) return null;
            if (!TryReadInt32LE(raw, ref pos, out dataLen)) return null;
            if (dataLen < 0 || pos + dataLen > raw.Length) return null;
            var data = new byte[dataLen];
            Buffer.BlockCopy(raw, pos, data, 0, dataLen);
            return new ApSharedFieldEvent(apMsgId, entityId, fieldId, data);
        }

        private static bool TryReadInt32LE(byte[] data, ref int offset, out int value)
        {
            if (offset + 4 > data.Length)
            {
                value = 0;
                return false;
            }
            value = ReadInt32LE(data, offset);
            offset += 4;
            return true;
        }

        private static bool TryReadUInt16LE(byte[] data, ref int offset, out ushort value)
        {
            if (offset + 2 > data.Length)
            {
                value = 0;
                return false;
            }
            value = BitConverter.ToUInt16(data, offset);
            offset += 2;
            return true;
        }

        private static bool TryReadUInt32LE(byte[] data, ref int offset, out uint value)
        {
            if (offset + 4 > data.Length)
            {
                value = 0;
                return false;
            }
            value = BitConverter.ToUInt32(data, offset);
            offset += 4;
            return true;
        }

        private static bool TryReadUInt64LE(byte[] data, ref int offset, out ulong value)
        {
            if (offset + 8 > data.Length)
            {
                value = 0;
                return false;
            }
            value = BitConverter.ToUInt64(data, offset);
            offset += 8;
            return true;
        }

        private static bool TryReadGameClientRef(byte[] data, int offset, out ushort refType, out ulong refId, out int nextOffset)
        {
            refType = 0;
            refId = 0;
            nextOffset = offset;
            if (data == null || offset + 10 > data.Length)
            {
                return false;
            }
            refType = BitConverter.ToUInt16(data, offset);
            refId = BitConverter.ToUInt64(data, offset + 2);
            nextOffset = offset + 10;
            return true;
        }

        private static int ReadInt32LE(byte[] data, int offset)
        {
            return BitConverter.ToInt32(data, offset);
        }

        private static byte[] Concat(params byte[][] chunks)
        {
            var total = 0;
            for (var i = 0; i < chunks.Length; i++)
            {
                total += chunks[i] != null ? chunks[i].Length : 0;
            }
            var result = new byte[total];
            var offset = 0;
            for (var i = 0; i < chunks.Length; i++)
            {
                var chunk = chunks[i];
                if (chunk == null || chunk.Length == 0)
                {
                    continue;
                }
                Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }
            return result;
        }

        private static void SleepWithStop(ManualResetEvent stopEvent, int milliseconds)
        {
            var remaining = milliseconds;
            while (remaining > 0 && !stopEvent.WaitOne(0))
            {
                var step = Math.Min(200, remaining);
                Thread.Sleep(step);
                remaining -= step;
            }
        }

        private static bool PayloadContains(List<string> strings, string needle)
        {
            if (strings == null || needle == null)
            {
                return false;
            }
            for (var i = 0; i < strings.Count; i++)
            {
                var s = strings[i] ?? string.Empty;
                if (s.IndexOf(needle, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string TryExtractUtf16JsonObject(byte[] data)
        {
            if (data == null || data.Length < 2)
            {
                return null;
            }

            // Look for the UTF-16 LE bytes for '{' (0x7B 0x00).
            var start = -1;
            for (var i = 0; i + 1 < data.Length; i++)
            {
                if (data[i] == 0x7B && data[i + 1] == 0x00)
                {
                    start = i;
                    break;
                }
            }
            if (start < 0)
            {
                return null;
            }

            var len = data.Length - start;
            if ((len % 2) != 0)
            {
                len--; // keep UTF-16 alignment
            }
            if (len <= 0)
            {
                return null;
            }

            var s = Encoding.Unicode.GetString(data, start, len);
            if (IsNullOrWhiteSpace(s))
            {
                return null;
            }

            // Trim to the last '}' to avoid trailing binary fields.
            var end = s.LastIndexOf('}');
            if (end >= 0)
            {
                s = s.Substring(0, end + 1);
            }
            s = s.Trim('\0', ' ', '\r', '\n', '\t');
            return s;
        }

        private static string ExtractJsonStringValue(string json, string key)
        {
            if (IsNullOrWhiteSpace(json) || IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }

            idx = json.IndexOf(':', idx);
            if (idx < 0)
            {
                return null;
            }

            idx++;
            while (idx < json.Length && char.IsWhiteSpace(json[idx]))
            {
                idx++;
            }
            if (idx >= json.Length)
            {
                return null;
            }

            // Handle both "key":"value" and "key":123 numeric tokens (as seen in CharacterChangeCollection).
            if (json[idx] == '"')
            {
                var end = json.IndexOf('"', idx + 1);
                if (end < 0)
                {
                    return null;
                }
                return json.Substring(idx + 1, end - idx - 1);
            }

            var start = idx;
            while (idx < json.Length)
            {
                var ch = json[idx];
                if (ch == ',' || ch == '}' || ch == ']')
                {
                    break;
                }
                if (char.IsWhiteSpace(ch))
                {
                    break;
                }
                idx++;
            }
            if (idx <= start)
            {
                return null;
            }
            return json.Substring(start, idx - start).Trim();
        }

        private static string SafeGetIdentifierExtension(PlayerCharacterSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            var id = snapshot.CharacterIdentifier;
            if (IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var idx = id.IndexOf(':');
            if (idx < 0 || idx + 1 >= id.Length)
            {
                return null;
            }

            return id.Substring(idx + 1);
        }

        private static bool IsPrologueMissionName(string missionName)
        {
            if (IsNullOrWhiteSpace(missionName))
            {
                return false;
            }

            // Observed / possible identifiers:
            // - "1_010_Prologue" (used by our fallback + some metagame messages)
            // - "S010_Prologue" (matches StreamingAssets/levels folder)
            // - any mission name containing "Prologue" as a safe heuristic
            var trimmed = missionName.Trim();
            if (trimmed.IndexOf("Prologue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (string.Equals(trimmed, "1_010_Prologue", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(trimmed, "S010_Prologue", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private static bool TryParseInt32(string value, out int result)
        {
            result = 0;
            if (IsNullOrWhiteSpace(value))
            {
                return false;
            }
            return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryParseUInt64(string value, out ulong result)
        {
            result = 0UL;
            if (IsNullOrWhiteSpace(value))
            {
                return false;
            }
            return ulong.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }

        private static bool ContainsGuid(Guid[] values, Guid value)
        {
            if (values == null)
            {
                return false;
            }
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] == value)
                {
                    return true;
                }
            }
            return false;
        }

        private static Guid[] OrderGuidsWithLeaderFirst(Guid[] values, Guid leader)
        {
            if (values == null || values.Length <= 1)
            {
                return values;
            }
            if (leader == Guid.Empty)
            {
                return values;
            }

            var leaderIndex = -1;
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] == leader)
                {
                    leaderIndex = i;
                    break;
                }
            }
            if (leaderIndex <= 0)
            {
                // -1 = leader not present; 0 = already first.
                return values;
            }

            // Preserve relative order of all other values.
            var ordered = new Guid[values.Length];
            ordered[0] = leader;
            var writeIdx = 1;
            for (var i = 0; i < values.Length; i++)
            {
                if (i == leaderIndex)
                {
                    continue;
                }
                ordered[writeIdx++] = values[i];
            }
            return ordered;
        }

        private static Guid[] ParseGuidsFromLooseText(string text, int max)
        {
            if (IsNullOrWhiteSpace(text) || max <= 0)
            {
                return new Guid[0];
            }

            var list = new List<Guid>();
            var s = text.Trim();

            // Look for GUID patterns like xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.
            // The coop member list often looks like: ["<guid>:0", "<guid>:0"]
            for (var i = 0; i + 36 <= s.Length; i++)
            {
                if (s[i + 8] != '-' || s[i + 13] != '-' || s[i + 18] != '-' || s[i + 23] != '-')
                {
                    continue;
                }

                var candidate = s.Substring(i, 36);
                try
                {
                    var g = new Guid(candidate);
                    var exists = false;
                    for (var j = 0; j < list.Count; j++)
                    {
                        if (list[j] == g)
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        list.Add(g);
                        if (list.Count >= max)
                        {
                            break;
                        }
                    }
                    i += 35;
                }
                catch
                {
                    // Not a GUID; keep scanning.
                }
            }

            return list.ToArray();
        }

        private sealed class GuidStringOrdinalComparer : IComparer<Guid>
        {
            public static readonly GuidStringOrdinalComparer Instance = new GuidStringOrdinalComparer();

            public int Compare(Guid x, Guid y)
            {
                return string.CompareOrdinal(x.ToString(), y.ToString());
            }
        }

        private static IPAddress ResolveBindAddress(string host)
        {
            if (string.IsNullOrEmpty(host) || host == "0.0.0.0" || host == "+")
            {
                return IPAddress.Any;
            }
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return IPAddress.Loopback;
            }
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                return ip;
            }
            return IPAddress.Any;
        }

        private static byte[] HexToBytes(string hex)
        {
            if (hex == null)
            {
                return new byte[0];
            }
            hex = hex.Trim();
            if (hex.Length % 2 != 0)
            {
                throw new ArgumentException("hex must have even length");
            }
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)((FromHexNibble(hex[i * 2]) << 4) | FromHexNibble(hex[i * 2 + 1]));
            }
            return bytes;
        }

        private static int FromHexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
            if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
            throw new ArgumentException("invalid hex char");
        }

        private static string ToHexString(byte[] bytes, int offset, int count)
        {
            if (bytes == null)
            {
                return string.Empty;
            }
            var sb = new StringBuilder(count * 2);
            for (var i = 0; i < count; i++)
            {
                sb.Append(bytes[offset + i].ToString("x2"));
            }
            return sb.ToString();
        }

        private struct CoreDirectSystem
        {
            public readonly uint ServerId;
            public readonly byte[] Raw;
            public readonly ulong MsgNo;

            public CoreDirectSystem(uint serverId, byte[] raw, ulong msgNo)
            {
                ServerId = serverId;
                Raw = raw;
                MsgNo = msgNo;
            }
        }

        private struct ApSharedFieldEvent
        {
            public readonly byte ApMsgId;
            public readonly ulong EntityId;
            public readonly ushort FieldId;
            public readonly byte[] Data;

            public ApSharedFieldEvent(byte apMsgId, ulong entityId, ushort fieldId, byte[] data)
            {
                ApMsgId = apMsgId;
                EntityId = entityId;
                FieldId = fieldId;
                Data = data;
            }
        }

        private sealed class CoopMissionParticipant
        {
            public readonly string Peer;
            public readonly NetworkStream Stream;

            public CoopMissionParticipant(string peer, NetworkStream stream)
            {
                Peer = peer;
                Stream = stream;
            }
        }

        private sealed class HubPeerTarget
        {
            public readonly string Peer;
            public readonly NetworkStream Stream;

            public HubPeerTarget(string peer, NetworkStream stream)
            {
                Peer = peer;
                Stream = stream;
            }
        }
    }
}
