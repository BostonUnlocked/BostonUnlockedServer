using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private const string DefaultHubId = "Act01_HUB_02";
        private const string FallbackSerializedHubState = "CwAAAEgAVQBCAF8AcwBjAGUAbgBlAF8AMQALAAAASABVAEIAXwBzAGMAZQBuAGUAXwAxAAA=";

        private static readonly object HenchmanCollectionCacheLock = new object();
        private static string CachedSerializedHenchmanCollection;
        private static DateTime CachedSerializedHenchmanCollectionLastWriteUtc;
        private static int CachedHenchmanCollectionCreationIndex;
        private static List<PlayerCharacterSnapshot> CachedHenchmanCollectionSnapshots;

        private static readonly object ShopPriceCacheLock = new object();
        private static readonly Dictionary<string, Dictionary<string, int>> CachedShopPrices = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        private static DateTime CachedShopPricesLastWriteUtc;
        private static string CachedShopPricesPath;

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

        private static bool TryResolveShopPrice(string shopKeeper, string itemId, out int price)
        {
            price = 0;
            if (IsNullOrWhiteSpace(shopKeeper) || IsNullOrWhiteSpace(itemId))
            {
                return false;
            }

            try
            {
                lock (ShopPriceCacheLock)
                {
                    var staticDataPath = TryFindMetagameplayStaticDataPath();
                    if (IsNullOrWhiteSpace(staticDataPath) || !File.Exists(staticDataPath))
                    {
                        return false;
                    }

                    var lastWriteUtc = File.GetLastWriteTimeUtc(staticDataPath);
                    if (!string.Equals(CachedShopPricesPath, staticDataPath, StringComparison.OrdinalIgnoreCase)
                        || CachedShopPricesLastWriteUtc != lastWriteUtc)
                    {
                        CachedShopPrices.Clear();
                        CachedShopPricesPath = staticDataPath;
                        CachedShopPricesLastWriteUtc = lastWriteUtc;
                    }

                    Dictionary<string, int> shopMap;
                    if (!CachedShopPrices.TryGetValue(shopKeeper, out shopMap) || shopMap == null)
                    {
                        shopMap = ParseShopPricesFromMetagameplayJson(staticDataPath, shopKeeper);
                        CachedShopPrices[shopKeeper] = shopMap ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    }

                    if (shopMap != null && shopMap.TryGetValue(itemId, out price))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            price = 0;
            return false;
        }

        private static Dictionary<string, int> ParseShopPricesFromMetagameplayJson(string metagameplayJsonPath, string shopKeeper)
        {
            if (IsNullOrWhiteSpace(metagameplayJsonPath) || IsNullOrWhiteSpace(shopKeeper) || !File.Exists(metagameplayJsonPath))
            {
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var text = File.ReadAllText(metagameplayJsonPath);
                if (IsNullOrWhiteSpace(text))
                {
                    return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                var anchor = "\"InternalName\": \"" + shopKeeper + "\"";
                var start = text.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                var entriesKey = "\"ShopListEntries\"";
                var entriesStart = text.IndexOf(entriesKey, start, StringComparison.OrdinalIgnoreCase);
                if (entriesStart < 0)
                {
                    return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                var nextShop = text.IndexOf("\"InternalName\":", entriesStart + entriesKey.Length, StringComparison.OrdinalIgnoreCase);
                if (nextShop < 0)
                {
                    nextShop = text.Length;
                }

                var section = text.Substring(entriesStart, nextShop - entriesStart);
                var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                var pos = 0;
                while (pos >= 0 && pos < section.Length)
                {
                    var priceIdx = section.IndexOf("\"Price\":", pos, StringComparison.OrdinalIgnoreCase);
                    if (priceIdx < 0)
                    {
                        break;
                    }

                    var afterColon = section.IndexOf(':', priceIdx);
                    if (afterColon < 0)
                    {
                        break;
                    }

                    afterColon++;
                    while (afterColon < section.Length && char.IsWhiteSpace(section[afterColon])) afterColon++;

                    var endNum = afterColon;
                    while (endNum < section.Length && char.IsDigit(section[endNum])) endNum++;

                    int parsedPrice;
                    if (!int.TryParse(section.Substring(afterColon, endNum - afterColon), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedPrice))
                    {
                        pos = endNum;
                        continue;
                    }

                    var itemIdx = section.IndexOf("\"ItemId\":", endNum, StringComparison.OrdinalIgnoreCase);
                    if (itemIdx < 0)
                    {
                        break;
                    }

                    var itemColon = section.IndexOf(':', itemIdx);
                    if (itemColon < 0)
                    {
                        break;
                    }

                    var firstQuote = section.IndexOf('"', itemColon + 1);
                    if (firstQuote < 0)
                    {
                        break;
                    }

                    var secondQuote = section.IndexOf('"', firstQuote + 1);
                    if (secondQuote < 0)
                    {
                        break;
                    }

                    var itemId = section.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                    if (!IsNullOrWhiteSpace(itemId))
                    {
                        result[itemId] = parsedPrice;
                    }

                    pos = secondQuote + 1;
                }

                return result;
            }
            catch
            {
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
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
                wallet.Reset(CurrencyId.Karma, slot != null ? slot.Karma : 0, 0);
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

        private static PlayerCharacterSnapshot CloneHenchSnapshotForMission(PlayerCharacterSnapshot src, Guid ownerAccountGuid, int slotIndex, int ownerKarma, int ownerNuyen)
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
            clone.Wallet.Reset(CurrencyId.Karma, ownerKarma, 0);
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

        private static string SerializeHubStateOrFallback(string hubId, string characterIdentifier, string characterName, CareerSlot slot)
        {
            if (IsNullOrWhiteSpace(hubId))
            {
                return FallbackSerializedHubState;
            }

            try
            {
                var state = new HubState { HubId = hubId, Name = hubId };

                if (!IsNullOrWhiteSpace(characterIdentifier))
                {
                    var snapshot = new PlayerCharacterSnapshot();
                    snapshot.CharacterIdentifier = characterIdentifier;
                    snapshot.PlayerId = 1UL;
                    snapshot.DataVersion = 48;
                    snapshot.CharacterName = !IsNullOrWhiteSpace(characterName) ? characterName : PlayerCharacterDefaultValues.PlayerName;
                    snapshot.PortraitPath = (slot != null && !IsNullOrWhiteSpace(slot.PortraitPath)) ? slot.PortraitPath : PlayerCharacterDefaultValues.PortraitPath;
                    snapshot.Voiceset = (slot != null && !IsNullOrWhiteSpace(slot.Voiceset)) ? slot.Voiceset : PlayerCharacterDefaultValues.Voiceset;
                    snapshot.Bodytype = (slot != null && slot.Bodytype != 0UL) ? slot.Bodytype : PlayerCharacterDefaultValues.Bodytype;
                    snapshot.SkinTextureIndex = (slot != null) ? slot.SkinTextureIndex : PlayerCharacterDefaultValues.SkinTextureIndex;
                    snapshot.BackgroundStory = (slot != null && slot.BackgroundStory != 0UL) ? slot.BackgroundStory : PlayerCharacterDefaultValues.BackgroundStory;
                    snapshot.WantsBackgroundChange = slot != null && slot.WantsBackgroundChange;
                    if (snapshot.Wallet != null)
                    {
                        snapshot.Wallet.Reset(CurrencyId.Karma, slot != null ? slot.Karma : 0, 0);
                        snapshot.Wallet.Reset(CurrencyId.Nuyen, slot != null ? slot.Nuyen : 0, 0);
                    }

                    // Provide a minimal valid loadout so hub UI (e.g., shop inspectors) can resolve equipped weapons.
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

                    // Cosmetic equipment slots (hair/clothes/etc) chosen in character editor.
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

                    // The hub scene expects at least one player character to spawn.
                    state.Add(snapshot, new Vector2D(0f, 0f));
                }

                return HubSerializer.SerializeHubState(state);
            }
            catch
            {
                return FallbackSerializedHubState;
            }
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
                var nuyen = slot != null ? slot.Nuyen : 0;
                snapshot.Wallet.Reset(CurrencyId.Karma, karma, 0);
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

        private readonly LocalServiceOptions _options;
        private readonly RequestLogger _logger;
        private readonly LocalUserStore _userStore;
        private readonly ISessionIdentityMap _sessionIdentityMap;
        private readonly CareerInfoGenerator _careerInfoGenerator;
        private readonly MatchConfigurationGenerator _matchConfigurationGenerator;

        // APlay DirectSystem messages include an 8-byte message number the client may use for ordering/dedup.
        // For MetaGameplay pushes we must keep these monotonic even if the client repeats a request with a lower MsgNo.
        private long _metaGameplayOutMsgNoHighWatermark;

        private const int HubStateDedupWindowMs = 1500;
        private const int CreationInfoDedupWindowMs = 10000;
        private readonly object _hubPushDedupLock = new object();
        private readonly Dictionary<string, HubPushDedupState> _hubPushDedupByPeer = new Dictionary<string, HubPushDedupState>(StringComparer.OrdinalIgnoreCase);

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
            : this(options, logger, new LocalUserStore(options, logger), null)
        {
        }

        public APlayTcpStub(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore)
            : this(options, logger, userStore, null)
        {
        }

        public APlayTcpStub(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore, ISessionIdentityMap sessionIdentityMap)
        {
            _options = options;
            _logger = logger;
            _userStore = userStore ?? new LocalUserStore(options, logger);
            _sessionIdentityMap = sessionIdentityMap;
            _careerInfoGenerator = new CareerInfoGenerator(logger);
            _matchConfigurationGenerator = new MatchConfigurationGenerator(logger);
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

        private bool TryAdvanceMainCampaignIfEligible(string identityHash, CareerSlot slot, string peer, NetworkStream stream, string storylineName, ulong msgNoBase, ref byte[] cachedHubStatePayload, byte[] cachedCreationInfoPayload, bool nudgeClient)
        {
            if (slot == null)
            {
                return false;
            }

            StorylineInfo storyline;
            if (!TryGetStoryline(storylineName, out storyline) || storyline == null || storyline.Chapters == null || storyline.Chapters.Count == 0)
            {
                return false;
            }

            var currentIndex = slot.MainCampaignCurrentChapter;
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }
            if (currentIndex >= storyline.Chapters.Count)
            {
                currentIndex = storyline.Chapters.Count - 1;
            }

            var chapter = storyline.Chapters[currentIndex];
            if (chapter == null)
            {
                return false;
            }

            // Gate: only advance once all required missions are fully completed/claimed.
            // If we advance at ReadyToReceiveRewards, the finished mission drops out of the active chapter,
            // quest givers stop showing the (?) marker, and the player never redeems StoryRewards (e.g., nuyen).
            var states = slot.MainCampaignMissionStates ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < chapter.RequiredMissions.Count; i++)
            {
                var missionName = chapter.RequiredMissions[i];
                if (IsNullOrWhiteSpace(missionName))
                {
                    continue;
                }
                string raw;
                states.TryGetValue(missionName, out raw);
                var st = ParseStoryMissionStateOrDefault(raw, StoryMissionstate.Available);
                if (st < StoryMissionstate.Completed)
                {
                    return false;
                }
            }

            var nextIndex = currentIndex + 1;
            if (nextIndex >= storyline.Chapters.Count)
            {
                return false;
            }

            var next = storyline.Chapters[nextIndex];
            slot.MainCampaignCurrentChapter = nextIndex;
            if (slot.MainCampaignInteractedNpcs != null)
            {
                slot.MainCampaignInteractedNpcs.Clear();
            }

            // Update hub id to the next chapter's hub (retail server moves you along the campaign hubs).
            if (next != null && !IsNullOrWhiteSpace(next.Hub))
            {
                slot.HubId = next.Hub;
            }

            if (_userStore != null && !IsNullOrWhiteSpace(identityHash))
            {
                try { _userStore.UpsertCareer(identityHash, slot); } catch { }
            }

            // Broadcast ChapterChange via StoryprogressChanged.
            try
            {
                var chapterChangeJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.ChapterChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"" + storylineName + "\",\"NewChapterIndex\":" + nextIndex.ToString(CultureInfo.InvariantCulture) + "}";
                var payload = BuildUtf16StringPayload(chapterChangeJson);
                var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, payload), msgNoBase);
                SendRawFrame(stream, peer, PrefixLength(core), "sent MetaGameplayCommunicationObject StoryprogressChanged (ChapterChange " + nextIndex.ToString(CultureInfo.InvariantCulture) + ")");
            }
            catch
            {
            }

            // Refresh hub payload so subsequent RequestCurrentStorylineHub returns the correct hub.
            try
            {
                var hubId = !IsNullOrWhiteSpace(slot.HubId) ? slot.HubId : DefaultHubId;
                var characterIdentifier = !IsNullOrWhiteSpace(slot.CharacterIdentifier)
                    ? slot.CharacterIdentifier
                    : (Guid.NewGuid().ToString() + ":" + slot.Index.ToString(CultureInfo.InvariantCulture));
                cachedHubStatePayload = BuildMetaHubPushPayload(4, SerializeHubStateOrFallback(hubId, characterIdentifier, slot.CharacterName, slot));
            }
            catch
            {
            }

            // Nudge hub/client if requested and we already have cached payloads.
            if (nudgeClient && cachedHubStatePayload != null && cachedCreationInfoPayload != null)
            {
                try
                {
                    if (!ShouldSuppressDuplicateHubPush(peer, false, cachedHubStatePayload))
                    {
                        var hubStateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 37, cachedHubStatePayload), msgNoBase + 1);
                        SendRawFrame(stream, peer, PrefixLength(hubStateCore), "sent MetaGameplayCommunicationObject SendHubCommunicationObjectToClient after ChapterChange");
                    }
                }
                catch
                {
                }
            }

            return true;
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
                    HandleClient((TcpClient)state, stopEvent);
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
                    const int EndTeamTurnSkillId = 99997;

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

                    // Track simple story progression locally so DirectStart missions don't loop forever.
                    // Keyed by map name (e.g., "1_010_Prologue").
                    var completedStoryMissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string currentMissionMapName = null;

                    ServerSimulationSession simulationSession = null;

                    const ulong gameworldEntityId = 5;
                    const ulong missionInstanceEntityId = 6;
                    const ulong missionCommandEntityId = 7;

                    const ushort gameworldCommunicationObjectTypeId = 7;
                    const ushort missionInstanceCommunicationObjectTypeId = 9;
                    const ushort missionCommandCommunicationObjectTypeId = 10;

                    // Authoritative simulation session (created per mission) used to safely skip AI turns.

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

                            if (asciiFrame == "AQAAAAA=")
                            {
                                var responseDecoded = Concat(
                                    BitConverter.GetBytes(2),
                                    new byte[] { 0 },
                                    BitConverter.GetBytes((ulong)1),
                                    BitConverter.GetBytes((ulong)1),
                                    BitConverter.GetBytes((ulong)1));
                                SendRawFrame(stream, peer, responseDecoded, "sent core welcome");
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
                                    SendRawFrame(stream, peer, PrefixLength(CoreIntroduceGameClientPayload), "sent AP introduce shared entity (type=5 game client connection)");
                                    sentIntro = true;
                                }

                                if (!sentInit)
                                {
                                    SendRawFrame(stream, peer, PrefixLength(CoreInitPayload), "sent AP initialized in response to AP hello");
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

                            var isRegularConnect = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 1
                                && shared.Value.FieldId == 3
                                && PayloadContains(payloadStrings, "RegularConnect");

                            if (isRegularConnect && !sentRegularConnectReply)
                            {
                                var serverMsgNoBase = direct.Value.MsgNo;

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
                                    var accountIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes((ulong)2), BitConverter.GetBytes((ushort)3), BitConverter.GetBytes(0));
                                    var accountIntroCore = BuildCoreDirectSystem(1, accountIntroRaw, serverMsgNoBase + 1);
                                    SendRawFrame(stream, peer, PrefixLength(accountIntroCore), "sent AP introduce shared entity (type=3 account communication object, id=2)");
                                    sentAccountIntro = true;
                                }

                                var gameClientOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(1, 5), serverMsgNoBase + 2);
                                SendRawFrame(stream, peer, PrefixLength(gameClientOwnerCore), "sent AP shared-entity set-owner (entity=1)");

                                var accountOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(2, 3), serverMsgNoBase + 3);
                                SendRawFrame(stream, peer, PrefixLength(accountOwnerCore), "sent AP shared-entity set-owner (entity=2)");

                                if (rejectReason != null)
                                {
                                    var rejectPayload = BuildUtf16StringPayload(rejectReason);
                                    var rejectCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 1, 5, rejectPayload), serverMsgNoBase + 4);
                                    SendRawFrame(stream, peer, PrefixLength(rejectCore), "sent GameClientConnection RejectLogin in response to RegularConnect");
                                    connectionClosed.Set();
                                    return;
                                }

                                activeIdentityHash = mappedIdentityHash;
                                activeIdentityGuid = mappedIdentityGuid;

                                var careerSummary = BuildCareerSummaryJson(_userStore != null ? _userStore.GetCareers(activeIdentityHash) : null);
                                var welcomePayload = BuildGameClientWelcomePayload(2, careerSummary);
                                var welcomeCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 1, 4, welcomePayload), serverMsgNoBase + 4);
                                SendRawFrame(stream, peer, PrefixLength(welcomeCore), "sent GameClientConnection Welcome in response to RegularConnect");
                                sentRegularConnectReply = true;

                                var keepAliveCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 1, 6, new byte[0]), serverMsgNoBase + 5);
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
                                                var periodicKeepAliveCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 1, 6, new byte[0]), msgNo);
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
                                    var rejectCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 1, 5, rejectPayload), serverMsgNoBase + 4);
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

                                var hubStatePayload = BuildMetaHubPushPayload(4, SerializeHubStateOrFallback(hubId, characterIdentifier, characterName, slot));
                                cachedHubStatePayload = hubStatePayload;
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

                            if (_userStore != null && isMetaGameplayChangeSkillTrees)
                            {
                                var rawMessage = payloadStrings[0];
                                if (!IsNullOrWhiteSpace(rawMessage)
                                    && (rawMessage.IndexOf("SkillTreeChanges", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("SkillTreeTechnichalName", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("SkillTechnichalName", StringComparison.Ordinal) >= 0))
                                {
                                    var parsed = TryDeserializeJsonDict(rawMessage);

                                    // The client sends SkillLevel=0 in purchases; it expects the server to respond with a valid level.
                                    // If we echo SkillLevel=0 back, some client builds throw IndexOutOfRangeException in onSkillTreeChanged.
                                    var skillTreeChangedJson = rawMessage;
                                    var inferredSkillLevels = 0;
                                    try
                                    {
                                        var typed = JsonFxSerializerProvider.Current.Deserialize<SkillTreeChanges>(rawMessage);
                                        if (typed != null && typed.Purchases != null && typed.Purchases.Length > 0)
                                        {
                                            for (var i = 0; i < typed.Purchases.Length; i++)
                                            {
                                                var p = typed.Purchases[i];
                                                if (p == null)
                                                {
                                                    continue;
                                                }

                                                if (p.SkillLevel > 0)
                                                {
                                                    continue;
                                                }

                                                int inferred;
                                                if (TryInferSkillLevelFromTechnicalName(p.SkillTechnichalName, out inferred) && inferred > 0)
                                                {
                                                    p.SkillLevel = inferred;
                                                    inferredSkillLevels++;
                                                }
                                            }

                                            if (inferredSkillLevels > 0)
                                            {
                                                skillTreeChangedJson = JsonFxSerializerProvider.Current.Serialize<SkillTreeChanges>(typed);
                                            }
                                        }
                                    }
                                    catch
                                    {
                                        // If inference fails for any reason, fall back to the raw payload.
                                        skillTreeChangedJson = rawMessage;
                                        inferredSkillLevels = 0;
                                    }

                                    var applyReset = false;
                                    try
                                    {
                                        if (parsed != null && parsed.Contains("ApplyReset") && parsed["ApplyReset"] != null)
                                        {
                                            if (parsed["ApplyReset"] is bool)
                                            {
                                                applyReset = (bool)parsed["ApplyReset"];
                                            }
                                            else
                                            {
                                                applyReset = Convert.ToBoolean(parsed["ApplyReset"], CultureInfo.InvariantCulture);
                                            }
                                        }
                                    }
                                    catch
                                    {
                                        applyReset = false;
                                    }

                                    var purchases = parsed != null ? GetArrayValue(parsed, "Purchases") : null;

                                    var slotIndex = activeCareerIndex;
                                    if (slotIndex < 0)
                                    {
                                        slotIndex = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetLastCareerIndex(activeIdentityHash) : 0;
                                    }

                                    var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, slotIndex, false) : null;
                                    if (slot != null)
                                    {
                                        if (slot.SkillTreeDefinitions == null)
                                        {
                                            slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
                                        }

                                        var changed = false;
                                        var appliedCount = 0;

                                        if (applyReset)
                                        {
                                            if (slot.SkillTreeDefinitions.Count > 0)
                                            {
                                                slot.SkillTreeDefinitions.Clear();
                                                changed = true;
                                            }
                                        }

                                        if (purchases != null && purchases.Length > 0)
                                        {
                                            for (var i = 0; i < purchases.Length; i++)
                                            {
                                                var entry = purchases[i] as IDictionary;
                                                if (entry == null)
                                                {
                                                    continue;
                                                }

                                                var tree = GetStringValue(entry, "SkillTreeTechnichalName");
                                                var skill = GetStringValue(entry, "SkillTechnichalName");
                                                if (IsNullOrWhiteSpace(tree) || IsNullOrWhiteSpace(skill))
                                                {
                                                    continue;
                                                }

                                                string[] existing;
                                                if (!slot.SkillTreeDefinitions.TryGetValue(tree, out existing) || existing == null)
                                                {
                                                    slot.SkillTreeDefinitions[tree] = new string[] { skill };
                                                    changed = true;
                                                    appliedCount++;
                                                    continue;
                                                }

                                                var already = false;
                                                for (var j = 0; j < existing.Length; j++)
                                                {
                                                    if (string.Equals(existing[j], skill, StringComparison.Ordinal))
                                                    {
                                                        already = true;
                                                        break;
                                                    }
                                                }
                                                if (already)
                                                {
                                                    continue;
                                                }

                                                var updated = new string[existing.Length + 1];
                                                for (var j = 0; j < existing.Length; j++)
                                                {
                                                    updated[j] = existing[j];
                                                }
                                                updated[existing.Length] = skill;
                                                slot.SkillTreeDefinitions[tree] = updated;
                                                changed = true;
                                                appliedCount++;
                                            }
                                        }

                                        if (changed)
                                        {
                                            try { _userStore.UpsertCareer(activeIdentityHash, slot); } catch { }
                                        }

                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "skilltree-change",
                                            peer = peer,
                                            careerIndex = slotIndex,
                                            applyReset = applyReset,
                                            purchases = purchases != null ? purchases.Length : 0,
                                            applied = appliedCount,
                                            persisted = changed,
                                            inferredSkillLevels = inferredSkillLevels,
                                        });

                                        // Notify the client so it commits the purchase into its runtime snapshot.
                                        try
                                        {
                                            var msgNoBase = direct.Value.MsgNo + 2;
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

                            if (_userStore != null && isMetaGameplayChangeItemPosessions)
                            {
                                var rawMessage = payloadStrings[0];
                                if (!IsNullOrWhiteSpace(rawMessage)
                                    && rawMessage.IndexOf("ItemPossessionChanges", StringComparison.Ordinal) >= 0)
                                {
                                    var parsed = TryDeserializeJsonDict(rawMessage);
                                    var shopKeeper = parsed != null ? GetStringValue(parsed, "ShopKeeper") : null;
                                    var changes = parsed != null ? GetArrayValue(parsed, "ItemChanges") : null;

                                    var slotIndex = activeCareerIndex;
                                    if (slotIndex < 0)
                                    {
                                        slotIndex = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetLastCareerIndex(activeIdentityHash) : 0;
                                    }

                                    var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, slotIndex, false) : null;
                                    if (slot != null)
                                    {
                                        if (slot.ItemPossessions == null)
                                        {
                                            slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                        }

                                        var totalCost = 0;
                                        var totalRefund = 0;
                                        var applied = 0;
                                        var appliedItemChanges = new List<ItemChange>();

                                        if (changes != null && changes.Length > 0)
                                        {
                                            for (var i = 0; i < changes.Length; i++)
                                            {
                                                var entry = changes[i] as IDictionary;
                                                if (entry == null)
                                                {
                                                    continue;
                                                }

                                                var itemId = GetStringValue(entry, "ItemDefintionId");
                                                var delta = GetInt32Value(entry, "Delta", 0);
                                                var quality = GetInt32Value(entry, "Quality", 0);
                                                var flavour = GetInt32Value(entry, "Flavour", -1);
                                                if (IsNullOrWhiteSpace(itemId) || delta == 0)
                                                {
                                                    continue;
                                                }

                                                try
                                                {
                                                    appliedItemChanges.Add(new ItemChange(itemId, delta)
                                                    {
                                                        Quality = quality,
                                                        Flavour = flavour,
                                                    });
                                                }
                                                catch
                                                {
                                                }

                                                var packedKey = itemId + "|" + quality.ToString(CultureInfo.InvariantCulture) + "|" + flavour.ToString(CultureInfo.InvariantCulture);
                                                int existing;
                                                if (!slot.ItemPossessions.TryGetValue(packedKey, out existing))
                                                {
                                                    existing = 0;
                                                }

                                                var next = existing + delta;
                                                if (next <= 0)
                                                {
                                                    if (slot.ItemPossessions.ContainsKey(packedKey))
                                                    {
                                                        slot.ItemPossessions.Remove(packedKey);
                                                    }
                                                }
                                                else
                                                {
                                                    slot.ItemPossessions[packedKey] = next;
                                                }

                                                int price;
                                                if (delta > 0)
                                                {
                                                    if (TryResolveShopPrice(shopKeeper, itemId, out price) && price > 0)
                                                    {
                                                        try { totalCost = checked(totalCost + checked(price * delta)); } catch { totalCost = int.MaxValue; }
                                                    }
                                                }
                                                else
                                                {
                                                    // Conservative sell/refund heuristic if we know shop price.
                                                    if (TryResolveShopPrice(shopKeeper, itemId, out price) && price > 0)
                                                    {
                                                        var qty = -delta;
                                                        var refundEach = price / 2;
                                                        if (refundEach > 0)
                                                        {
                                                            try { totalRefund = checked(totalRefund + checked(refundEach * qty)); } catch { totalRefund = int.MaxValue; }
                                                        }
                                                    }
                                                }

                                                applied++;
                                            }
                                        }

                                        if (totalCost > 0)
                                        {
                                            try
                                            {
                                                slot.Nuyen = slot.Nuyen - totalCost;
                                            }
                                            catch
                                            {
                                                slot.Nuyen = 0;
                                            }
                                        }
                                        if (totalRefund > 0)
                                        {
                                            try
                                            {
                                                slot.Nuyen = slot.Nuyen + totalRefund;
                                            }
                                            catch
                                            {
                                                slot.Nuyen = int.MaxValue;
                                            }
                                        }
                                        if (slot.Nuyen < 0)
                                        {
                                            slot.Nuyen = 0;
                                        }

                                        try { _userStore.UpsertCareer(activeIdentityHash, slot); } catch { }

                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "item-possession-change",
                                            peer = peer,
                                            careerIndex = slotIndex,
                                            shopKeeper = shopKeeper,
                                            itemChanges = changes != null ? changes.Length : 0,
                                            applied = applied,
                                            cost = totalCost,
                                            refund = totalRefund,
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

                                            // Client expects *compressed* shop changes (InventorySerializer.DeserializeShopItemChanges)
                                            // not the raw JSON request payload.
                                            var shopItemChanges = new ShopItemChanges
                                            {
                                                Failed = false,
                                                TotalNuyenChange = (totalRefund > 0 ? totalRefund : 0) - (totalCost > 0 ? totalCost : 0),
                                                AppliedChanges = appliedItemChanges.ToArray(),
                                                NotAppliedChanges = new ItemChange[0],
                                            };
                                            var serializedShopChanges = InventorySerializer.SerializeShopItemChanges(shopItemChanges);

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

                                                // Starting cash for a brand new runner.
                                                slot.Nuyen = 20000;

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

                                            var hubId = !IsNullOrWhiteSpace(slot.HubId) ? slot.HubId : DefaultHubId;
                                            var characterIdentifier = !IsNullOrWhiteSpace(slot.CharacterIdentifier)
                                                ? slot.CharacterIdentifier
                                                : (activeIdentityGuid.ToString() + ":" + slotIndex.ToString());
                                            cachedHubStatePayload = BuildMetaHubPushPayload(4, SerializeHubStateOrFallback(hubId, characterIdentifier, slot.CharacterName, slot));

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
                                                var hubId = !IsNullOrWhiteSpace(slot.HubId) ? slot.HubId : DefaultHubId;
                                                var characterIdentifier = !IsNullOrWhiteSpace(slot.CharacterIdentifier)
                                                    ? slot.CharacterIdentifier
                                                    : (activeIdentityGuid.ToString() + ":" + slotIndex.ToString());
                                                cachedHubStatePayload = BuildMetaHubPushPayload(4, SerializeHubStateOrFallback(hubId, characterIdentifier, slot.CharacterName, slot));

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
                                if (isMetaGameplayWrappedMessage && rawMessage != null && rawMessage.IndexOf("SetStoryMissionStateMessage", StringComparison.Ordinal) >= 0)
                                {
                                    // Outgoing AP message numbers: keep them monotonic and in send-order.
                                    // The client can ignore out-of-order/duplicate msgNos.
                                    //
                                    // IMPORTANT: do NOT base this solely on the client-provided MsgNo.
                                    // The client can resend the same request with a lower MsgNo than what we've already sent
                                    // (e.g. repeated Accept/Claim dialogs). If we respond with lower msgNos, the client may ignore
                                    // the MissionStateChange and UI will keep offering the same action until restart.
                                    ulong outMsgNo = 0;
                                    try
                                    {
                                        var minOutMsgNo = direct.Value.MsgNo + 1;

                                        // Choose the next msgNo as max(lastSent+1, clientMsgNo+1).
                                        var lastSent = Interlocked.Read(ref _metaGameplayOutMsgNoHighWatermark);
                                        var lastSentU = lastSent > 0 ? (ulong)lastSent : 0UL;
                                        outMsgNo = lastSentU + 1UL;
                                        if (outMsgNo < minOutMsgNo)
                                        {
                                            outMsgNo = minOutMsgNo;
                                        }

                                        var missionName = ExtractJsonStringValue(rawMessage, "Mission");
                                        var targetState = ExtractJsonStringValue(rawMessage, "TargetState");
                                        if (!IsNullOrWhiteSpace(missionName) && !IsNullOrWhiteSpace(targetState))
                                        {
                                            var parsedTarget = ParseStoryMissionStateOrDefault(targetState, StoryMissionstate.Available);

                                        // Keep our in-memory completion check (used to cancel DirectStart missions that were already finished).
                                        // ReadyToReceiveRewards is also treated as "completed enough" for mandatory-mission logic.
                                        if (parsedTarget >= StoryMissionstate.ReadyToReceiveRewards)
                                        {
                                            completedStoryMissions.Add(missionName);
                                        }

                                        var shouldGrantStoryRewards = false;
                                        StoryMissionstate previousState = StoryMissionstate.Available;
                                        CareerSlot slotForStoryRewards = null;

                                        // Persist the requested state so it survives restarts.
                                        if (_userStore != null)
                                        {
                                            try
                                            {
                                                var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                                                if (slot != null)
                                                {
                                                    slotForStoryRewards = slot;

                                                    if (slot.MainCampaignMissionStates == null)
                                                    {
                                                        slot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                                    }

                                                    try
                                                    {
                                                        string existing;
                                                        if (slot.MainCampaignMissionStates.TryGetValue(missionName, out existing) && !IsNullOrWhiteSpace(existing))
                                                        {
                                                            previousState = ParseStoryMissionStateOrDefault(existing, StoryMissionstate.Available);
                                                        }
                                                    }
                                                    catch
                                                    {
                                                        previousState = StoryMissionstate.Available;
                                                    }

                                                    // Retail-like: story rewards are redeemed when moving ReadyToReceiveRewards -> Completed.
                                                    if (previousState == StoryMissionstate.ReadyToReceiveRewards && parsedTarget == StoryMissionstate.Completed)
                                                    {
                                                        shouldGrantStoryRewards = true;
                                                    }

                                                    // Store canonical enum names; our snapshot generator parses these.
                                                    slot.MainCampaignMissionStates[missionName] = parsedTarget.ToString();

                                                    _userStore.UpsertCareer(activeIdentityHash, slot);
                                                }
                                            }
                                            catch
                                            {
                                            }
                                        }

                                        // If the player just redeemed story rewards in the hub, apply them now.
                                        // The StoryRewardDialog content is client-driven (from mission definition), but wallet/inventory are authoritative server-side.
                                        int storyKarma = 0;
                                        int storyNuyen = 0;
                                        var storyItemChangesApplied = new List<ItemChange>();
                                        var grantedAnyStoryRewards = false;
                                        if (shouldGrantStoryRewards && slotForStoryRewards != null)
                                        {
                                            try
                                            {
                                                int found;
                                                if (TryResolveMissionStoryCurrencyReward(missionName, "Victory", "Karma", out found) && found != 0)
                                                {
                                                    storyKarma = found;
                                                }
                                                if (TryResolveMissionStoryCurrencyReward(missionName, "Victory", "Nuyen", out found) && found != 0)
                                                {
                                                    storyNuyen = found;
                                                }

                                                ItemChange[] storyItems;
                                                if (TryResolveMissionStoryItemChanges(missionName, "Victory", out storyItems) && storyItems != null && storyItems.Length > 0)
                                                {
                                                    if (slotForStoryRewards.ItemPossessions == null)
                                                    {
                                                        slotForStoryRewards.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                                    }

                                                    for (var i = 0; i < storyItems.Length; i++)
                                                    {
                                                        var c = storyItems[i];
                                                        if (c == null || IsNullOrWhiteSpace(c.ItemDefintionId) || c.Delta == 0)
                                                        {
                                                            continue;
                                                        }

                                                        try
                                                        {
                                                            storyItemChangesApplied.Add(new ItemChange(c.ItemDefintionId, c.Delta)
                                                            {
                                                                Quality = c.Quality,
                                                                Flavour = c.Flavour,
                                                            });
                                                        }
                                                        catch
                                                        {
                                                        }

                                                        var packedKey = c.ItemDefintionId + "|" + c.Quality.ToString(CultureInfo.InvariantCulture) + "|" + c.Flavour.ToString(CultureInfo.InvariantCulture);
                                                        int existing;
                                                        if (!slotForStoryRewards.ItemPossessions.TryGetValue(packedKey, out existing))
                                                        {
                                                            existing = 0;
                                                        }

                                                        var next = existing + c.Delta;
                                                        if (next <= 0)
                                                        {
                                                            if (slotForStoryRewards.ItemPossessions.ContainsKey(packedKey))
                                                            {
                                                                slotForStoryRewards.ItemPossessions.Remove(packedKey);
                                                            }
                                                        }
                                                        else
                                                        {
                                                            slotForStoryRewards.ItemPossessions[packedKey] = next;
                                                        }
                                                    }
                                                }

                                                if (storyKarma != 0)
                                                {
                                                    try
                                                    {
                                                        checked
                                                        {
                                                            slotForStoryRewards.Karma = slotForStoryRewards.Karma + storyKarma;
                                                        }
                                                    }
                                                    catch
                                                    {
                                                        slotForStoryRewards.Karma = int.MaxValue;
                                                    }
                                                }

                                                if (storyNuyen != 0)
                                                {
                                                    try
                                                    {
                                                        checked
                                                        {
                                                            slotForStoryRewards.Nuyen = slotForStoryRewards.Nuyen + storyNuyen;
                                                        }
                                                    }
                                                    catch
                                                    {
                                                        slotForStoryRewards.Nuyen = int.MaxValue;
                                                    }
                                                }

                                                grantedAnyStoryRewards = (storyKarma != 0 || storyNuyen != 0 || (storyItemChangesApplied != null && storyItemChangesApplied.Count > 0));
                                                if (grantedAnyStoryRewards)
                                                {
                                                    try { _userStore.UpsertCareer(slotForStoryRewards); } catch { }

                                                    _logger.Log(new
                                                    {
                                                        ts = RequestLogger.UtcNowIso(),
                                                        type = "story-reward",
                                                        peer = peer,
                                                        mission = missionName,
                                                        previousState = previousState.ToString(),
                                                        newState = parsedTarget.ToString(),
                                                        karmaDelta = storyKarma,
                                                        karmaTotal = slotForStoryRewards.Karma,
                                                        nuyenDelta = storyNuyen,
                                                        nuyenTotal = slotForStoryRewards.Nuyen,
                                                        itemChanges = storyItemChangesApplied != null ? storyItemChangesApplied.Count : 0,
                                                        careerIndex = activeCareerIndex,
                                                    });
                                                }
                                            }
                                            catch
                                            {
                                                storyKarma = 0;
                                                storyNuyen = 0;
                                                try { storyItemChangesApplied.Clear(); } catch { }
                                                grantedAnyStoryRewards = false;
                                            }
                                        }

                                        // CRITICAL: send MissionStateChange immediately so UI reacts (quest markers, claim option, etc).
                                        try
                                        {
                                            var storyProgressChangeJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.MissionStateChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"Main Campaign\",\"Mission\":\"" + missionName + "\",\"NewState\":\"" + parsedTarget.ToString() + "\"}";
                                            var storyProgressChangePayload = BuildUtf16StringPayload(storyProgressChangeJson);
                                            var storyProgressChangeCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, storyProgressChangePayload), outMsgNo++);
                                            SendRawFrame(stream, peer, PrefixLength(storyProgressChangeCore), "sent MetaGameplayCommunicationObject StoryprogressChanged (MissionStateChange " + parsedTarget.ToString() + ")");
                                        }
                                        catch
                                        {
                                        }

                                        // Tell the client what it earned so PlayerCharacterController pockets currency and UI updates.
                                        // Include StoryRewards item changes so the reward dialog + inventory stay consistent.
                                        if (grantedAnyStoryRewards && slotForStoryRewards != null)
                                        {
                                            try
                                            {
                                                var earnedCurrencies = new List<object>();
                                                if (storyKarma != 0)
                                                {
                                                    earnedCurrencies.Add(new Dictionary<string, object>
                                                    {
                                                        { "CurrencyId", "Karma" },
                                                        { "EarnedValue", storyKarma },
                                                    });
                                                }
                                                if (storyNuyen != 0)
                                                {
                                                    earnedCurrencies.Add(new Dictionary<string, object>
                                                    {
                                                        { "CurrencyId", "Nuyen" },
                                                        { "EarnedValue", storyNuyen },
                                                    });
                                                }

                                                object[] storyItemPayload = new object[0];
                                                if (storyItemChangesApplied != null && storyItemChangesApplied.Count > 0)
                                                {
                                                    var list = new List<object>();
                                                    for (var i = 0; i < storyItemChangesApplied.Count; i++)
                                                    {
                                                        var c = storyItemChangesApplied[i];
                                                        if (c == null || IsNullOrWhiteSpace(c.ItemDefintionId) || c.Delta == 0)
                                                        {
                                                            continue;
                                                        }
                                                        list.Add(new Dictionary<string, object>
                                                        {
                                                            { "ItemDefintionId", c.ItemDefintionId },
                                                            { "Delta", c.Delta },
                                                            { "Quality", c.Quality },
                                                            { "Flavour", c.Flavour },
                                                        });
                                                    }
                                                    storyItemPayload = list.ToArray();
                                                }

                                                var rewardJson = Json.Serialize(new Dictionary<string, object>
                                                {
                                                    { "GrantedUnlocks", new string[0] },
                                                    { "EarnedCurrencies", earnedCurrencies.ToArray() },
                                                    { "ItemChanges", storyItemPayload },
                                                });

                                                var rewardPayload = BuildUtf16StringPayload(rewardJson);
                                                var rewardCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 35, rewardPayload), outMsgNo++);
                                                SendRawFrame(stream, peer, PrefixLength(rewardCore), "sent MetaGameplayCommunicationObject GotMissionReward (StoryRewards redemption)");
                                            }
                                            catch
                                            {
                                            }
                                        }

                                        // Retail-like: advance chapter once current chapter's required missions are fully completed/claimed.
                                        // If the advancement triggers, reserve msgNos to avoid collisions with our own pushes.
                                        if (slotForStoryRewards != null)
                                        {
                                            try
                                            {
                                                var advanced = TryAdvanceMainCampaignIfEligible(activeIdentityHash, slotForStoryRewards, peer, stream, "Main Campaign", outMsgNo, ref cachedHubStatePayload, cachedCreationInfoPayload, true);
                                                if (advanced)
                                                {
                                                    outMsgNo = outMsgNo + 2;
                                                }
                                            }
                                            catch
                                            {
                                            }
                                        }

                                        // Always rebuild the cached hub payload from the latest persisted slot state (mission state changes + rewards).
                                        if (slotForStoryRewards != null)
                                        {
                                            try
                                            {
                                                var hubId = !IsNullOrWhiteSpace(slotForStoryRewards.HubId) ? slotForStoryRewards.HubId : DefaultHubId;
                                                var characterIdentifier = !IsNullOrWhiteSpace(slotForStoryRewards.CharacterIdentifier)
                                                    ? slotForStoryRewards.CharacterIdentifier
                                                    : (activeIdentityGuid.ToString() + ":" + activeCareerIndex.ToString());
                                                cachedHubStatePayload = BuildMetaHubPushPayload(4, SerializeHubStateOrFallback(hubId, characterIdentifier, slotForStoryRewards.CharacterName, slotForStoryRewards));
                                            }
                                            catch
                                            {
                                            }
                                        }

                                        // Send a fresh metagame snapshot after state changes/rewards so StorylineController rebuilds authoritatively.
                                        if (slotForStoryRewards != null)
                                        {
                                            try
                                            {
                                                var zipped = _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, activeCareerIndex, slotForStoryRewards);
                                                var metaSnapshotPayload = BuildUtf16StringPayload(zipped);
                                                var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), outMsgNo++);
                                                SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient after SetStoryMissionStateMessage");
                                            }
                                            catch
                                            {
                                            }
                                        }

                                        // In-session UX: accepting or completing/claiming a mission often needs an updated hub push
                                        // so quest giver markers / dialogs refresh without requiring a restart.
                                        if ((parsedTarget == StoryMissionstate.ReadyToPlay || parsedTarget == StoryMissionstate.Completed)
                                            && cachedHubStatePayload != null
                                            && cachedCreationInfoPayload != null)
                                        {
                                            try
                                            {
                                                if (!ShouldSuppressDuplicateHubPush(peer, false, cachedHubStatePayload))
                                                {
                                                    var hubStateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 37, cachedHubStatePayload), outMsgNo++);
                                                    SendRawFrame(stream, peer, PrefixLength(hubStateCore), "sent MetaGameplayCommunicationObject SendHubCommunicationObjectToClient after SetStoryMissionStateMessage");
                                                }
                                            }
                                            catch
                                            {
                                            }

                                            try
                                            {
                                                if (!ShouldSuppressDuplicateHubPush(peer, true, cachedCreationInfoPayload))
                                                {
                                                    var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, cachedCreationInfoPayload), outMsgNo++);
                                                    SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged after SetStoryMissionStateMessage");
                                                }
                                            }
                                            catch
                                            {
                                            }
                                        }

                                        }

                                        // Echo the original wrapped message so the client's SendMessage promise resolves.
                                        var echoPayload = BuildUtf16StringPayload(rawMessage);
                                        var echoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 1, echoPayload), outMsgNo++);
                                        SendRawFrame(stream, peer, PrefixLength(echoCore), "echoed MetaGameplayCommunicationObject Message (SetStoryMissionStateMessage)");
                                    }
                                    finally
                                    {
                                        // outMsgNo tracks the next-to-use value; update our high-watermark to the last used.
                                        // Avoid decreasing it if something else advanced it concurrently.
                                        if (outMsgNo > 0)
                                        {
                                            var lastUsed = outMsgNo - 1UL;
                                            while (true)
                                            {
                                                var observed = Interlocked.Read(ref _metaGameplayOutMsgNoHighWatermark);
                                                var observedU = observed > 0 ? (ulong)observed : 0UL;
                                                if (lastUsed <= observedU)
                                                {
                                                    break;
                                                }
                                                if (Interlocked.CompareExchange(ref _metaGameplayOutMsgNoHighWatermark, (long)lastUsed, observed) == observed)
                                                {
                                                    break;
                                                }
                                            }
                                        }
                                    }
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

                                    if (completedStoryMissions.Contains(mapName))
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
                                                var clone = CloneHenchSnapshotForMission(src, activeIdentityGuid, i, ownerKarma, ownerNuyen);
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
                                        ? _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeCharacterName, selectedHenchmen)
                                        : _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeCharacterName);
                                    if (_userStore != null)
                                    {
                                        try
                                        {
                                            var activeSlot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                                            if (activeSlot != null)
                                            {
                                                if (selectedHenchmen != null && selectedHenchmen.Length > 0)
                                                {
                                                    compressedMatchConfiguration = _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeSlot, selectedHenchmen);
                                                }
                                                else
                                                {
                                                    compressedMatchConfiguration = _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeSlot);
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
                                            chapterForLoot);
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
                                object missionLog = null;
                                byte[] followPathCore = null;
                                byte[] activateCore = null;

                                int? followPathAgentId = null;
                                int? followPathTargetX = null;
                                int? followPathTargetY = null;
                                int? activateSkillId = null;
                                int? activateAgentId = null;

                                var responseMsgNoBase = direct.Value.MsgNo + 1000;
                                var data = shared.Value.Data;

                                if (shared.Value.FieldId == 0)
                                {
                                    ushort refType;
                                    ulong refId;
                                    int offset;
                                    if (TryReadGameClientRef(data, 0, out refType, out refId, out offset))
                                    {
                                        missionLog = new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "aplay-mission-command",
                                            peer = peer,
                                            field = "MissionReady",
                                            gameClientRefType = refType,
                                            gameClientRefId = refId,
                                        };
                                    }
                                }

                                // MissionCommand LeaveMission (client clicks through mission end / exits mission)
                                if (shared.Value.FieldId == 1)
                                {
                                    ushort refType;
                                    ulong refId;
                                    int offset;
                                    if (TryReadGameClientRef(data, 0, out refType, out refId, out offset))
                                    {
                                        missionLog = new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "aplay-mission-command",
                                            peer = peer,
                                            field = "LeaveMission",
                                            gameClientRefType = refType,
                                            gameClientRefId = refId,
                                        };
                                    }

                                    // In our generated match configuration, the local human player is always ID=1.
                                    const ulong participantId = 1UL;

                                    // Mark the just-finished mission completed so the client doesn't auto-start it again.
                                    // (The prologue is a DirectStart mission and will otherwise immediately restart on hub load.)
                                    var completedMapName = !IsNullOrWhiteSpace(currentMissionMapName) ? currentMissionMapName : "1_010_Prologue";
                                    var wasAlreadyCompleted = completedStoryMissions.Contains(completedMapName);
                                    completedStoryMissions.Add(completedMapName);

                                    // Persist mission completion for this career so it survives restarts.
                                    if (_userStore != null)
                                    {
                                        try
                                        {
                                            var progressSlot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                                            if (progressSlot != null)
                                            {
                                                if (progressSlot.MainCampaignMissionStates == null)
                                                {
                                                    progressSlot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                                }

                                                // Retail-like: after a mission finishes, it becomes ReadyToReceiveRewards.
                                                // The player (in hub) then redeems rewards, which moves it to Completed.
                                                //
                                                // Special-case the prologue: if we leave it at ReadyToReceiveRewards,
                                                // the client's MandatoryMissionStarter can still consider it mandatory
                                                // and will try to immediately start it again during mission shutdown.
                                                // Our server-side StartMissionCancelled response then surfaces as
                                                // "missionController.ServerAbortedMission".
                                                var postMissionState = StoryMissionstate.ReadyToReceiveRewards;
                                                if (string.Equals(completedMapName, "1_010_Prologue", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    postMissionState = StoryMissionstate.Completed;
                                                }

                                                progressSlot.MainCampaignMissionStates[completedMapName] = postMissionState.ToString();
                                                _userStore.UpsertCareer(activeIdentityHash, progressSlot);

                                                // If this completes the chapter, advance now (retail server pushes ChapterChange).
                                                // During mission shutdown we only want to advance state + refresh cached hub payload.
                                                // Sending unsolicited hub instances here can arrive before Meta UI is recreated,
                                                // then get dropped client-side and also get duplicate-suppressed when the client
                                                // later requests the hub instance.
                                                TryAdvanceMainCampaignIfEligible(activeIdentityHash, progressSlot, peer, stream, "Main Campaign", responseMsgNoBase + 9, ref cachedHubStatePayload, cachedCreationInfoPayload, false);
                                            }
                                        }
                                        catch
                                        {
                                        }
                                    }

                                    // Apply post-mission rewards. The victory popup uses mission definitions client-side,
                                    // but the spendable karma balance comes from the PlayerCharacterSnapshot we send.
                                    // The client does not send the reward amount; we resolve it from extracted static-data.
                                    int karmaReward = 0;
                                    int nuyenReward = 0;

                                    // Loot pickup during the mission (loot boxes, interaction loot) is processed inside the simulation.
                                    // The client build ships with a no-op DefaultMissionLootController, so our sim installs a LocalMissionLootController
                                    // and queues grants here for persistence.
                                    int lootNuyenReward = 0;
                                    string[] lootItemIds = new string[0];
                                    string[] lootTables = new string[0];
                                    var lootItemChanges = new List<ItemChange>();
                                    if (simulationSession != null)
                                    {
                                        try
                                        {
                                            var grants = simulationSession.DrainPendingLoot();
                                            if (grants != null && grants.Length > 0)
                                            {
                                                var items = new List<string>();
                                                var tables = new List<string>();
                                                for (var i = 0; i < grants.Length; i++)
                                                {
                                                    var g = grants[i];
                                                    if (g == null)
                                                    {
                                                        continue;
                                                    }

                                                    if (!IsNullOrWhiteSpace(g.LootTable))
                                                    {
                                                        tables.Add(g.LootTable);
                                                    }

                                                    if (!IsNullOrWhiteSpace(g.ItemId))
                                                    {
                                                        items.Add(g.ItemId);

                                                        // Persist loot as actual inventory items; the client expects ItemChanges in GotMissionReward.
                                                        // Quality/flavour are not surfaced by LootGrant, so default to 0/-1.
                                                        if (g.Delta != 0)
                                                        {
                                                            try
                                                            {
                                                                lootItemChanges.Add(new ItemChange(g.ItemId, g.Delta)
                                                                {
                                                                    Quality = 0,
                                                                    Flavour = -1,
                                                                });
                                                            }
                                                            catch
                                                            {
                                                            }
                                                        }
                                                    }

                                                    if (g.Nuyen > 0)
                                                    {
                                                        try
                                                        {
                                                            checked
                                                            {
                                                                lootNuyenReward = lootNuyenReward + g.Nuyen;
                                                            }
                                                        }
                                                        catch
                                                        {
                                                            lootNuyenReward = int.MaxValue;
                                                        }
                                                    }
                                                }

                                                lootItemIds = items.ToArray();
                                                lootTables = tables.ToArray();

                                                _logger.Log(new
                                                {
                                                    ts = RequestLogger.UtcNowIso(),
                                                    type = "mission-loot-drain",
                                                    peer = peer,
                                                    mapName = completedMapName,
                                                    grants = grants.Length,
                                                    nuyenFromLoot = lootNuyenReward,
                                                    lootTables = lootTables,
                                                    itemIds = lootItemIds,
                                                    itemChanges = lootItemChanges != null ? lootItemChanges.Count : 0,
                                                });
                                            }
                                        }
                                        catch
                                        {
                                        }
                                    }
                                    
                                    int found;
                                    if (TryResolveMissionCurrencyReward(completedMapName, "Victory", "Karma", out found) && found > 0)
                                    {
                                        karmaReward = found;
                                    }

                                    if (TryResolveMissionCurrencyReward(completedMapName, "Victory", "Nuyen", out found) && found > 0)
                                    {
                                        nuyenReward = found;
                                    }

                                    // Note: we intentionally do NOT convert loot items to nuyen here.
                                    // Retail treats loot as items (ItemChanges); players can sell later.
                                    if (lootNuyenReward > 0)
                                    {
                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "mission-loot",
                                            peer = peer,
                                            mapName = completedMapName,
                                            nuyenValueIfAutoSold = lootNuyenReward,
                                            lootTables = lootTables,
                                            itemIds = lootItemIds,
                                        });
                                    }

                                    CareerSlot rewardSlot = null;
                                    if (_userStore != null)
                                    {
                                        rewardSlot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;

                                        var appliedLootItems = 0;
                                        if (rewardSlot != null && lootItemChanges != null && lootItemChanges.Count > 0)
                                        {
                                            if (rewardSlot.ItemPossessions == null)
                                            {
                                                rewardSlot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                            }

                                            for (var i = 0; i < lootItemChanges.Count; i++)
                                            {
                                                var c = lootItemChanges[i];
                                                if (c == null || IsNullOrWhiteSpace(c.ItemDefintionId) || c.Delta == 0)
                                                {
                                                    continue;
                                                }

                                                var packedKey = c.ItemDefintionId + "|" + c.Quality.ToString(CultureInfo.InvariantCulture) + "|" + c.Flavour.ToString(CultureInfo.InvariantCulture);
                                                int existing;
                                                if (!rewardSlot.ItemPossessions.TryGetValue(packedKey, out existing))
                                                {
                                                    existing = 0;
                                                }

                                                var next = existing + c.Delta;
                                                if (next <= 0)
                                                {
                                                    if (rewardSlot.ItemPossessions.ContainsKey(packedKey))
                                                    {
                                                        rewardSlot.ItemPossessions.Remove(packedKey);
                                                    }
                                                }
                                                else
                                                {
                                                    rewardSlot.ItemPossessions[packedKey] = next;
                                                }
                                                appliedLootItems++;
                                            }
                                        }

                                        if (rewardSlot != null && (karmaReward > 0 || nuyenReward > 0 || appliedLootItems > 0))
                                        {
                                            if (karmaReward > 0)
                                            {
                                                try
                                                {
                                                    checked
                                                    {
                                                        rewardSlot.Karma = rewardSlot.Karma + karmaReward;
                                                    }
                                                }
                                                catch
                                                {
                                                    // If overflow ever happens, just clamp to int.MaxValue.
                                                    rewardSlot.Karma = int.MaxValue;
                                                }
                                            }

                                            if (nuyenReward > 0)
                                            {
                                                try
                                                {
                                                    checked
                                                    {
                                                        rewardSlot.Nuyen = rewardSlot.Nuyen + nuyenReward;
                                                    }
                                                }
                                                catch
                                                {
                                                    // If overflow ever happens, just clamp to int.MaxValue.
                                                    rewardSlot.Nuyen = int.MaxValue;
                                                }
                                            }

                                            if (!IsNullOrWhiteSpace(activeIdentityHash))
                                            {
                                                _userStore.UpsertCareer(activeIdentityHash, rewardSlot);
                                            }

                                            _logger.Log(new
                                            {
                                                ts = RequestLogger.UtcNowIso(),
                                                type = "mission-reward",
                                                peer = peer,
                                                mapName = completedMapName,
                                                karmaDelta = karmaReward,
                                                karmaTotal = rewardSlot.Karma,
                                                nuyenDelta = nuyenReward,
                                                nuyenTotal = rewardSlot.Nuyen,
                                                lootItemChanges = lootItemChanges != null ? lootItemChanges.Count : 0,
                                                lootItemsApplied = appliedLootItems,
                                                careerIndex = activeCareerIndex,
                                            });

                                            // Tell the client what it earned so InventoryController applies ItemChanges.
                                            try
                                            {
                                                var earnedCurrencies = new List<object>();
                                                if (karmaReward != 0)
                                                {
                                                    earnedCurrencies.Add(new Dictionary<string, object>
                                                    {
                                                        { "CurrencyId", "Karma" },
                                                        { "EarnedValue", karmaReward },
                                                    });
                                                }
                                                if (nuyenReward != 0)
                                                {
                                                    earnedCurrencies.Add(new Dictionary<string, object>
                                                    {
                                                        { "CurrencyId", "Nuyen" },
                                                        { "EarnedValue", nuyenReward },
                                                    });
                                                }

                                                var itemChangesPayload = new object[0];
                                                if (lootItemChanges != null && lootItemChanges.Count > 0)
                                                {
                                                    var list = new List<object>();
                                                    for (var i = 0; i < lootItemChanges.Count; i++)
                                                    {
                                                        var c = lootItemChanges[i];
                                                        if (c == null || IsNullOrWhiteSpace(c.ItemDefintionId) || c.Delta == 0)
                                                        {
                                                            continue;
                                                        }
                                                        list.Add(new Dictionary<string, object>
                                                        {
                                                            { "ItemDefintionId", c.ItemDefintionId },
                                                            { "Delta", c.Delta },
                                                            { "Quality", c.Quality },
                                                            { "Flavour", c.Flavour },
                                                        });
                                                    }
                                                    itemChangesPayload = list.ToArray();
                                                }

                                                var missionRewardJson = Json.Serialize(new Dictionary<string, object>
                                                {
                                                    { "GrantedUnlocks", new string[0] },
                                                    { "EarnedCurrencies", earnedCurrencies.ToArray() },
                                                    { "ItemChanges", itemChangesPayload },
                                                });

                                                var missionRewardPayload = BuildUtf16StringPayload(missionRewardJson);
                                                var missionRewardCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 35, missionRewardPayload), responseMsgNoBase + 6);
                                                SendRawFrame(stream, peer, PrefixLength(missionRewardCore), "sent MetaGameplayCommunicationObject GotMissionReward after LeaveMission");
                                            }
                                            catch
                                            {
                                            }
                                        }
                                    }

                                    // Intentionally skip StoryprogressChanged here.
                                    // In the offline/local flow the client can have a null/tearing-down metagameplay viewmodel during mission exit,
                                    // and StorylineController.ApplyMissionStateChange can throw (seen in output_log.txt) which destabilizes the transition.
                                    // We already persist mission states and re-send a full metagameplay snapshot after LeaveMission.

                                    // After mission exit, send an updated metagameplay snapshot so hub UI reflects rewards.
                                    // Do NOT push hub instances here; the client requests its hub instance (RequestStoryHubFor
                                    // or RequestCurrentStorylineHubMessage) once the Metagameplay UI exists.
                                    // Unsolicited pushes can arrive too early, be discarded, and then get duplicate-suppressed
                                    // when the client makes the real request.
                                    if (_userStore != null)
                                    {
                                        try
                                        {
                                            var slotForSnapshot = rewardSlot ?? (!IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null);
                                            var zippedCareerInfo = slotForSnapshot != null
                                                ? _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, activeCareerIndex, slotForSnapshot)
                                                : _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, activeCareerIndex, activeCharacterName, false);

                                            var metaSnapshotPayload = BuildUtf16StringPayload(zippedCareerInfo);
                                            var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), responseMsgNoBase + 10);
                                            SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient after LeaveMission (reward sync)");

                                            // Refresh cached hub push payload to embed the updated PlayerCharacterSnapshot.
                                            if (slotForSnapshot != null)
                                            {
                                                var hubId = !IsNullOrWhiteSpace(slotForSnapshot.HubId) ? slotForSnapshot.HubId : DefaultHubId;
                                                var characterIdentifier = !IsNullOrWhiteSpace(slotForSnapshot.CharacterIdentifier)
                                                    ? slotForSnapshot.CharacterIdentifier
                                                    : (activeIdentityGuid.ToString() + ":" + activeCareerIndex.ToString());
                                                cachedHubStatePayload = BuildMetaHubPushPayload(4, SerializeHubStateOrFallback(hubId, characterIdentifier, slotForSnapshot.CharacterName, slotForSnapshot));
                                            }
                                        }
                                        catch
                                        {
                                        }
                                    }

                                    var leavePayload = BitConverter.GetBytes(participantId);
                                    var leaveCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 3, leavePayload), responseMsgNoBase + 13);
                                    SendRawFrame(stream, peer, PrefixLength(leaveCore), "sent GameworldCommunicationObject LeaveMission (participantId=" + participantId + ")");

                                    // Also emit GameworldCommunicationObject Stop to mirror the normal shutdown sequence.
                                    var stopCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 0, new byte[0]), responseMsgNoBase + 14);
                                    SendRawFrame(stream, peer, PrefixLength(stopCore), "sent GameworldCommunicationObject Stop after LeaveMission");

                                    if (simulationSession != null)
                                    {
                                        try
                                        {
                                            simulationSession.Stop();
                                        }
                                        catch
                                        {
                                        }
                                        simulationSession = null;
                                    }
                                }

                                if (shared.Value.FieldId == 2)
                                {
                                    ushort refType;
                                    ulong refId;
                                    int offset;
                                    if (TryReadGameClientRef(data, 0, out refType, out refId, out offset))
                                    {
                                        int x, y, z;
                                        if (TryReadInt32LE(data, ref offset, out x)
                                            && TryReadInt32LE(data, ref offset, out y)
                                            && TryReadInt32LE(data, ref offset, out z))
                                        {
                                            // MissionCommand FollowPath is sent as three int32 values.
                                            // The GameworldCommunicationObject callback is:
                                            //   onFollowPath(int agentId, int targetX, int targetY)
                                            followPathAgentId = x;
                                            followPathTargetX = y;
                                            followPathTargetY = z;
                                            var followPathPayload = Concat(BitConverter.GetBytes(x), BitConverter.GetBytes(y), BitConverter.GetBytes(z));
                                            followPathCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 1, followPathPayload), responseMsgNoBase + 1);
                                        }
                                    }
                                }

                                if (shared.Value.FieldId == 3)
                                {
                                    ushort refType;
                                    ulong refId;
                                    int offset;
                                    if (TryReadGameClientRef(data, 0, out refType, out refId, out offset))
                                    {
                                        int a, b, c, d, e, f;
                                        if (TryReadInt32LE(data, ref offset, out a)
                                            && TryReadInt32LE(data, ref offset, out b)
                                            && TryReadInt32LE(data, ref offset, out c)
                                            && TryReadInt32LE(data, ref offset, out d)
                                            && TryReadInt32LE(data, ref offset, out e)
                                            && TryReadInt32LE(data, ref offset, out f))
                                        {
                                            // MissionCommand ActivateActiveSkill is sent as six int32 values.
                                            // The GameworldCommunicationObject callback is:
                                            //   onActivateActiveSkill(int weaponIndex, int skillIndex, int skillId, int agentId, int targetX, int targetY, SeedPackage seeds)
                                            activateSkillId = c;
                                            activateAgentId = d;

                                            // Seed package for deterministic simulation (server authoritative).
                                            var seed0 = 0x11111111u;
                                            var seed1 = 0x22222222u;
                                            var seed2 = 0x33333333u;
                                            var seed3 = 0x44444444u;
                                            var seedPkg = new Cliffhanger.SRO.ServerClientCommons.Gameworld.Communication.SeedPackage(seed0, seed1, seed2, seed3);
                                            if (simulationSession != null)
                                            {
                                                seedPkg = simulationSession.CreateSeedPackage();
                                                seed0 = seedPkg.Seed0;
                                                seed1 = seedPkg.Seed1;
                                                seed2 = seedPkg.Seed2;
                                                seed3 = seedPkg.Seed3;
                                            }

                                            var activatePayload = Concat(
                                                BitConverter.GetBytes(a),
                                                BitConverter.GetBytes(b),
                                                BitConverter.GetBytes(c),
                                                BitConverter.GetBytes(d),
                                                BitConverter.GetBytes(e),
                                                BitConverter.GetBytes(f),
                                                BitConverter.GetBytes(seed0),
                                                BitConverter.GetBytes(seed1),
                                                BitConverter.GetBytes(seed2),
                                                BitConverter.GetBytes(seed3));

                                            activateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 2, activatePayload), responseMsgNoBase + 2);

                                            if (simulationSession != null)
                                            {
                                                try
                                                {
                                                    simulationSession.ExecuteActivateSkill(a, b, c, d, e, f, seedPkg);
                                                }
                                                catch (Exception ex)
                                                {
                                                    _logger.Log(new
                                                    {
                                                        ts = RequestLogger.UtcNowIso(),
                                                        type = "sim",
                                                        peer = peer,
                                                        status = "execute-failed",
                                                        cmd = "ActivateActiveSkill",
                                                        skillId = c,
                                                        agentId = d,
                                                        message = ex.Message,
                                                    });
                                                }
                                            }
                                        }
                                    }
                                }

                                // Update observed human agent IDs and detect when player regained control.
                                // We treat any non-EndTeamTurn action/move as "player input".
                                // (Legacy note) We used to keep an observed agentId set for AI-turn probe spam.
                                // That approach is removed; keep per-agent action counters only.

                                if (missionLog != null)
                                {
                                    _logger.Log(missionLog);
                                }
                                if (followPathCore != null)
                                {
                                    SendRawFrame(stream, peer, PrefixLength(followPathCore), "echoed GameworldCommunicationObject FollowPath from MissionCommand");

                                    if (simulationSession != null && followPathAgentId.HasValue && followPathTargetX.HasValue && followPathTargetY.HasValue)
                                    {
                                        try
                                        {
                                            simulationSession.ExecuteFollowPath(followPathAgentId.Value, followPathTargetX.Value, followPathTargetY.Value);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.Log(new
                                            {
                                                ts = RequestLogger.UtcNowIso(),
                                                type = "sim",
                                                peer = peer,
                                                status = "execute-failed",
                                                cmd = "FollowPath",
                                                agentId = followPathAgentId.Value,
                                                targetX = followPathTargetX.Value,
                                                targetY = followPathTargetY.Value,
                                                message = ex.Message,
                                            });
                                        }

                                        // Player often ends their last action with movement. If the authoritative sim
                                        // moved to an AI team, immediately skip AI turns here too.
                                        try
                                        {
                                            var aiEnds = simulationSession.SkipAiTurnsIfNeeded();
                                            if (aiEnds != null && aiEnds.Count > 0)
                                            {
                                                var baseMsgNo = responseMsgNoBase + 700;
                                                var idx = 0;
                                                foreach (var aiEnd in aiEnds)
                                                {
                                                    var seed0 = aiEnd.Seeds.Seed0;
                                                    var seed1 = aiEnd.Seeds.Seed1;
                                                    var seed2 = aiEnd.Seeds.Seed2;
                                                    var seed3 = aiEnd.Seeds.Seed3;

                                                    var payload = Concat(
                                                        BitConverter.GetBytes(0),
                                                        BitConverter.GetBytes(0),
                                                        BitConverter.GetBytes(EndTeamTurnSkillId),
                                                        BitConverter.GetBytes(aiEnd.AgentId),
                                                        BitConverter.GetBytes(0),
                                                        BitConverter.GetBytes(0),
                                                        BitConverter.GetBytes(seed0),
                                                        BitConverter.GetBytes(seed1),
                                                        BitConverter.GetBytes(seed2),
                                                        BitConverter.GetBytes(seed3));

                                                    var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 2, payload), baseMsgNo + (ulong)idx);
                                                    SendRawFrame(stream, peer, PrefixLength(core), "sim: auto-ended AI team turn (agentId=" + aiEnd.AgentId + ")");
                                                    idx++;
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.Log(new
                                            {
                                                ts = RequestLogger.UtcNowIso(),
                                                type = "sim",
                                                peer = peer,
                                                status = "skip-ai-failed",
                                                message = ex.Message,
                                            });
                                        }

                                        // If this action granted loot (e.g., interaction skill), show the in-mission loot popup.
                                        SendPendingLootPreviews(simulationSession, stream, peer, responseMsgNoBase + 900);
                                    }
                                }
                                if (activateCore != null)
                                {
                                    SendRawFrame(stream, peer, PrefixLength(activateCore), "echoed GameworldCommunicationObject ActivateActiveSkill from MissionCommand");

                                    if (simulationSession != null)
                                    {
                                        // If the authoritative simulation moved to an AI team, immediately skip AI turns.
                                        try
                                        {
                                            var aiEnds = simulationSession.SkipAiTurnsIfNeeded();
                                            if (aiEnds != null && aiEnds.Count > 0)
                                            {
                                                var baseMsgNo = responseMsgNoBase + 700;
                                                var idx = 0;
                                                foreach (var aiEnd in aiEnds)
                                                {
                                                    var seed0 = aiEnd.Seeds.Seed0;
                                                    var seed1 = aiEnd.Seeds.Seed1;
                                                    var seed2 = aiEnd.Seeds.Seed2;
                                                    var seed3 = aiEnd.Seeds.Seed3;

                                                    var payload = Concat(
                                                        BitConverter.GetBytes(0),
                                                        BitConverter.GetBytes(0),
                                                        BitConverter.GetBytes(EndTeamTurnSkillId),
                                                        BitConverter.GetBytes(aiEnd.AgentId),
                                                        BitConverter.GetBytes(0),
                                                        BitConverter.GetBytes(0),
                                                        BitConverter.GetBytes(seed0),
                                                        BitConverter.GetBytes(seed1),
                                                        BitConverter.GetBytes(seed2),
                                                        BitConverter.GetBytes(seed3));

                                                    var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 2, payload), baseMsgNo + (ulong)idx);
                                                    SendRawFrame(stream, peer, PrefixLength(core), "sim: auto-ended AI team turn (agentId=" + aiEnd.AgentId + ")");
                                                    idx++;
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.Log(new
                                            {
                                                ts = RequestLogger.UtcNowIso(),
                                                type = "sim",
                                                peer = peer,
                                                status = "skip-ai-failed",
                                                message = ex.Message,
                                            });
                                        }

                                        // If this action granted loot, show the in-mission loot popup.
                                        SendPendingLootPreviews(simulationSession, stream, peer, responseMsgNoBase + 950);
                                    }
                                }
                            }
                        }

                        var chunk = ReadChunk(stream);
                        if (chunk.Length == 0)
                        {
                            connectionClosed.Set();
                            _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "aplay-conn", peer = peer, note = "socket closed" });
                            break;
                        }

                        buffer.AddRange(chunk);
                    }
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
            lock (stream)
            {
                stream.Write(frameBytes, 0, frameBytes.Length);
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
            for (var idx = 0; idx < 3; idx++)
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
    }
}
