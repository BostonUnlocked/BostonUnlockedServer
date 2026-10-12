using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Shadowrun.LocalService.Core.Coupons;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed partial class LocalUserStore
    {
        private sealed class LocalCouponService
        {
            private static readonly object CouponItemPackageLock = new object();
            private static Dictionary<string, List<string>> CachedCouponItemPackages;
            private static string CachedCouponItemPackagesSourceIdentity;
            private static readonly JavaScriptSerializer CouponJson = CreateCouponJsonSerializer();
            private static readonly HashSet<string> CosmeticItemTypeNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "UpperBody",
                "LowerBody",
                "Hands",
                "Boots",
                "UpperUnderwear",
                "LowerUnderwear",
                "Glasses",
                "Head",
                "Skin",
                "Tattoo",
            };
            private const string BonusCosmeticsPackageName = "BONUSCOSMETICS";
            private static readonly string[] UnobtainableCosmeticsPackageItems = new[]
            {
                "Item_AstralCoat",
                "Item_BasicMageVest",
                "Item_BeardBraided",
                "Item_BeardConchita",
                "Item_BeardFull",
                "Item_BeardStubble",
                "Item_BikerPants",
                "Item_CyberdocOverall",
                "Item_DefaultLowerUnderwear",
                "Item_DefaultUpperUnderwear",
                "Item_DocGloves",
                "Item_DocOverall",
                "Item_ElegantTop",
                "Item_HairDreads",
                "Item_HairDreadsUndercut",
                "Item_HairLadyLuck",
                "Item_HairLong2",
                "Item_HairMohawkLimp",
                "Item_HairMohawkSideburns",
                "Item_HairPancake",
                "Item_HipsterBoots",
                "Item_HornsBasic",
                "Item_KimonoTop",
                "Item_LeatherBoots",
                "Item_LeatherCoat",
                "Item_MagicalLongsleeve",
                "Item_Pack1HackerTop",
                "Item_Pack1MageCoat",
                "Item_Pack3Corset",
                "Item_Pack4BagBoots",
                "Item_Pack4HospitalGown",
                "Item_RunnerLongsleeve",
                "Item_Sideburns",
                "Item_StreetCorset",
                "Item_SuitJacket",
                "Item_WesternTop",
            };

            private const string CouponGameName = "SRO";
            private const string CouponPackagesPlayerInfoKey = "CouponPackages";

            private readonly LocalUserStore _owner;

            public LocalCouponService(LocalUserStore owner)
            {
                _owner = owner;
            }

            public bool TryResolveCouponItemPackageCode(string code, out string packageTechnicalName)
            {
                packageTechnicalName = null;
                if (LocalUserStore.IsNullOrWhiteSpace(code))
                {
                    return false;
                }

                if (!HonoredCouponCodes.IsHonored(code))
                {
                    return false;
                }

                var packages = GetOrLoadCouponItemPackagesByTechnicalName();
                if (packages == null || packages.Count <= 0)
                {
                    return false;
                }

                List<string> ignored;
                if (packages.TryGetValue(code, out ignored))
                {
                    packageTechnicalName = code;
                    return true;
                }

                var normalized = NormalizeCouponCode(code);
                foreach (var kvp in packages)
                {
                    if (string.Equals(NormalizeCouponCode(kvp.Key), normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        packageTechnicalName = kvp.Key;
                        return true;
                    }
                }

                return false;
            }

            public bool ApplyCouponItemPackageToAllCareers(string identityHash, string packageTechnicalName)
            {
                if (!LocalUserStore.IsGuidish(identityHash) || LocalUserStore.IsNullOrWhiteSpace(packageTechnicalName))
                {
                    return false;
                }

                lock (_owner._lock)
                {
                    var identity = LocalUserStore.NormalizeGuidish(identityHash);
                    var account = _owner.LoadAccountForIdentityNoThrow(identity, true) ?? _owner.LoadAccountNoThrow();
                    var careersObj = account["Careers"];
                    var careersList = LocalUserStore.CoerceToArrayList(careersObj);
                    if (careersList == null)
                    {
                        careersList = LocalUserStore.BuildDefaultCareers(identity);
                        account["Careers"] = careersList;
                    }
                    else if (!(careersObj is ArrayList))
                    {
                        account["Careers"] = careersList;
                    }

                    var anyChanged = false;
                    for (var i = 0; i < careersList.Count; i++)
                    {
                        var dict = careersList[i] as IDictionary;
                        if (dict == null)
                        {
                            continue;
                        }

                        var slot = CareerSlot.FromDictionary(dict);
                        if (slot == null || !slot.IsOccupied)
                        {
                            continue;
                        }

                        var slotChanged = false;
                        if (ApplyCouponItemPackageToCareerNoLock(slot, packageTechnicalName))
                        {
                            slotChanged = true;
                        }

                        if (!slotChanged)
                        {
                            continue;
                        }

                        var updated = slot.ToDictionary();
                        foreach (DictionaryEntry entry in updated)
                        {
                            dict[entry.Key] = entry.Value;
                        }
                        anyChanged = true;
                    }

                    if (anyChanged)
                    {
                        _owner.SaveAccountNoThrow(account);
                    }

                    return anyChanged;
                }
            }

            public bool ApplyCouponItemPackagesToCareerNoLock(string identityHash, CareerSlot slot)
            {
                if (slot == null || !slot.IsOccupied || !LocalUserStore.IsGuidish(identityHash))
                {
                    return false;
                }

                var changed = false;
                var entitled = GetCouponItemPackageEntitlementsForIdentityNoLock(identityHash);
                if (entitled == null || entitled.Count <= 0)
                {
                    return false;
                }

                for (var i = 0; i < entitled.Count; i++)
                {
                    if (ApplyCouponItemPackageToCareerNoLock(slot, entitled[i]))
                    {
                        changed = true;
                    }
                }

                return changed;
            }

            public bool EnsureAllCouponEntitlementItemsPresentNoLock(CareerSlot slot, List<string> entitledPackages)
            {
                if (slot == null || !slot.IsOccupied || entitledPackages == null || entitledPackages.Count <= 0)
                {
                    return false;
                }

                var packages = GetOrLoadCouponItemPackagesByTechnicalName();
                if (packages == null || packages.Count <= 0)
                {
                    return false;
                }

                if (slot.ItemPossessions == null)
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                var requiredByItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var appliedPackages = new List<string>();
                for (var i = 0; i < entitledPackages.Count; i++)
                {
                    var packageName = entitledPackages[i];
                    if (LocalUserStore.IsNullOrWhiteSpace(packageName))
                    {
                        continue;
                    }

                    List<string> items;
                    if (!packages.TryGetValue(packageName, out items) || items == null || items.Count <= 0)
                    {
                        continue;
                    }

                    appliedPackages.Add(packageName);
                    for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
                    {
                        var itemId = items[itemIndex];
                        if (LocalUserStore.IsNullOrWhiteSpace(itemId))
                        {
                            continue;
                        }

                        int required;
                        if (!requiredByItem.TryGetValue(itemId, out required) || required < 0)
                        {
                            required = 0;
                        }

                        if (required < int.MaxValue)
                        {
                            required++;
                        }

                        requiredByItem[itemId] = required;
                    }
                }

                var changed = false;
                foreach (var kvp in requiredByItem)
                {
                    var possessionKey = kvp.Key + "|0|-1";
                    var required = kvp.Value;
                    int existing;
                    if (!slot.ItemPossessions.TryGetValue(possessionKey, out existing) || existing < 0)
                    {
                        existing = 0;
                    }

                    if (existing >= required)
                    {
                        continue;
                    }

                    AddOwnedItemAmount(slot, kvp.Key, required - existing);
                    changed = true;
                }

                if (slot.AppliedCouponItemPackages == null)
                {
                    slot.AppliedCouponItemPackages = new List<string>();
                }

                for (var i = 0; i < appliedPackages.Count; i++)
                {
                    if (!ListContainsIgnoreCase(slot.AppliedCouponItemPackages, appliedPackages[i]))
                    {
                        slot.AppliedCouponItemPackages.Add(appliedPackages[i]);
                        changed = true;
                    }
                }

                return changed;
            }

            public List<string> GetCouponItemPackageEntitlementsForIdentityNoLock(string identityHash)
            {
                var packages = new List<string>();
                if (!LocalUserStore.IsGuidish(identityHash))
                {
                    return packages;
                }

                var playerInfo = _owner.GetPlayerInfo(identityHash, CouponGameName);
                if (playerInfo == null)
                {
                    return packages;
                }

                string raw;
                if (!playerInfo.TryGetValue(CouponPackagesPlayerInfoKey, out raw) || LocalUserStore.IsNullOrWhiteSpace(raw))
                {
                    return packages;
                }

                var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var split = raw.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < split.Length; i++)
                {
                    var s = split[i] != null ? split[i].Trim() : null;
                    if (LocalUserStore.IsNullOrWhiteSpace(s) || seen.ContainsKey(s) || !HonoredCouponCodes.IsHonored(s))
                    {
                        continue;
                    }

                    seen[s] = true;
                    packages.Add(s);
                }

                return packages;
            }

            private bool ApplyCouponItemPackageToCareerNoLock(CareerSlot slot, string packageTechnicalName)
            {
                if (slot == null || !slot.IsOccupied || LocalUserStore.IsNullOrWhiteSpace(packageTechnicalName))
                {
                    return false;
                }

                if (slot.AppliedCouponItemPackages == null)
                {
                    slot.AppliedCouponItemPackages = new List<string>();
                }

                var alreadyApplied = ListContainsIgnoreCase(slot.AppliedCouponItemPackages, packageTechnicalName);
                var packages = GetOrLoadCouponItemPackagesByTechnicalName();
                if (packages == null)
                {
                    return false;
                }

                List<string> items;
                if (!packages.TryGetValue(packageTechnicalName, out items) || items == null || items.Count <= 0)
                {
                    return false;
                }

                if (slot.ItemPossessions == null)
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                if (alreadyApplied)
                {
                    return false;
                }

                for (var i = 0; i < items.Count; i++)
                {
                    AddOwnedItemAmount(slot, items[i], 1);
                }

                slot.AppliedCouponItemPackages.Add(packageTechnicalName);
                return true;
            }

            private Dictionary<string, List<string>> GetOrLoadCouponItemPackagesByTechnicalName()
            {
                try
                {
                    var staticDataDir = _owner._options != null ? _owner._options.StaticDataDir : null;
                    var streamingAssetsDir = _owner._options != null ? _owner._options.StreamingAssetsDir : null;
                    if (LocalUserStore.IsNullOrWhiteSpace(staticDataDir) || !Directory.Exists(staticDataDir))
                    {
                        LogBonusCosmeticsDataError(new DirectoryNotFoundException("Static data directory is missing."));
                        return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    }

                    lock (CouponItemPackageLock)
                    {
                        var sourceIdentity = GetCouponItemPackageSourceIdentity(staticDataDir, streamingAssetsDir);
                        if (CachedCouponItemPackages != null && string.Equals(CachedCouponItemPackagesSourceIdentity, sourceIdentity, StringComparison.OrdinalIgnoreCase))
                        {
                            return CachedCouponItemPackages;
                        }

                        CachedCouponItemPackages = LoadCouponItemPackagesByTechnicalName(staticDataDir);
                        AddBuiltInCouponItemPackages(CachedCouponItemPackages);
                        CachedCouponItemPackages.Remove(BonusCosmeticsPackageName);
                        try
                        {
                            CachedCouponItemPackages[BonusCosmeticsPackageName] = BuildBonusCosmeticsPackage(staticDataDir, streamingAssetsDir);
                        }
                        catch (Exception ex)
                        {
                            LogBonusCosmeticsDataError(ex);
                        }

                        CachedCouponItemPackagesSourceIdentity = sourceIdentity;
                        return CachedCouponItemPackages;
                    }
                }
                catch (Exception ex)
                {
                    LogBonusCosmeticsDataError(ex);
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }
            }

            private static Dictionary<string, List<string>> LoadCouponItemPackagesByTechnicalName(string staticDataDir)
            {
                var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (LocalUserStore.IsNullOrWhiteSpace(staticDataDir))
                    {
                        return result;
                    }

                    var path = Path.Combine(staticDataDir, "serverData.json");
                    if (!File.Exists(path))
                    {
                        return result;
                    }

                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (LocalUserStore.IsNullOrWhiteSpace(json))
                    {
                        return result;
                    }

                    var packagePattern =
                        "\\\"TypeName\\\"\\s*:\\s*\\\"Cliffhanger\\.SRO\\.ServerClientCommons\\.Definitions\\.ItemPackageDefinition, Cliffhanger\\.SRO\\.ServerClientCommons\\\"" +
                        "\\s*,\\s*\\\"TechnicalName\\\"\\s*:\\s*\\\"(?<name>[^\\\"]+)\\\"" +
                        "\\s*,\\s*\\\"Items\\\"\\s*:\\s*\\[(?<items>.*?)\\]";

                    var packageRegex = new Regex(packagePattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    var itemRegex = new Regex("\\\"(?<item>[^\\\"]+)\\\"", RegexOptions.Singleline);

                    var matches = packageRegex.Matches(json);
                    for (var i = 0; i < matches.Count; i++)
                    {
                        var match = matches[i];
                        if (match == null)
                        {
                            continue;
                        }

                        var name = match.Groups["name"] != null ? match.Groups["name"].Value : null;
                        if (LocalUserStore.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        var itemsBlob = match.Groups["items"] != null ? match.Groups["items"].Value : null;
                        var items = new List<string>();
                        if (!LocalUserStore.IsNullOrWhiteSpace(itemsBlob))
                        {
                            var itemMatches = itemRegex.Matches(itemsBlob);
                            for (var itemIndex = 0; itemIndex < itemMatches.Count; itemIndex++)
                            {
                                var itemMatch = itemMatches[itemIndex];
                                var itemId = itemMatch != null && itemMatch.Groups["item"] != null ? itemMatch.Groups["item"].Value : null;
                                if (!LocalUserStore.IsNullOrWhiteSpace(itemId))
                                {
                                    items.Add(itemId);
                                }
                            }
                        }

                        if (items.Count > 0)
                        {
                            result[name] = items;
                        }
                    }
                }
                catch
                {
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                return result;
            }

            private static void AddBuiltInCouponItemPackages(Dictionary<string, List<string>> packages)
            {
                if (packages == null)
                {
                    return;
                }

                AddBuiltInCouponItemPackage(packages, "UnobtainableCosmetics", UnobtainableCosmeticsPackageItems);
            }

            private static JavaScriptSerializer CreateCouponJsonSerializer()
            {
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                serializer.RecursionLimit = 100;
                return serializer;
            }

            private static List<string> BuildBonusCosmeticsPackage(string staticDataDir, string streamingAssetsDir)
            {
                if (LocalUserStore.IsNullOrWhiteSpace(staticDataDir) || !Directory.Exists(staticDataDir))
                {
                    throw new InvalidDataException("Static data directory is missing.");
                }

                if (LocalUserStore.IsNullOrWhiteSpace(streamingAssetsDir) || !Directory.Exists(streamingAssetsDir))
                {
                    throw new InvalidDataException("StreamingAssets directory is missing.");
                }

                var metagameplay = ReadCouponJsonObject(Path.Combine(staticDataDir, "metagameplay.json"));
                var ids = ReadCouponJsonObject(Path.Combine(staticDataDir, "ids.json"));
                var serverData = ReadCouponJsonObject(Path.Combine(staticDataDir, "serverData.json"));
                var globals = ReadCouponJsonObject(Path.Combine(staticDataDir, "globals.json"));

                var itemDefinitions = FindComponent(metagameplay, "LogicCharacterDataCollection");
                var equipmentDefinitions = GetRequiredObjectArray(itemDefinitions, "EquipmentItemDefinitions");
                var cosmetics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < equipmentDefinitions.Length; i++)
                {
                    var definition = RequireDictionary(equipmentDefinitions[i], "equipment item definition");
                    var itemId = GetRequiredString(definition, "Id");
                    var itemTypeId = Convert.ToString(definition["ItemTypeId"], CultureInfo.InvariantCulture);
                    string rawType;
                    if (!ids.Contains(itemTypeId) || !TryGetString(ids[itemTypeId], out rawType))
                    {
                        throw new InvalidDataException("Missing item type " + itemTypeId + " for " + itemId + ".");
                    }

                    const string itemTypePrefix = "MetagameplayIdentifiers.Itemtype_";
                    if (!rawType.StartsWith(itemTypePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var itemTypeName = rawType.Substring(itemTypePrefix.Length);
                    if (!CosmeticItemTypeNames.Contains(itemTypeName))
                    {
                        continue;
                    }

                    string visualReference;
                    if (!definition.Contains("VisualItemReference") || !TryGetString(definition["VisualItemReference"], out visualReference)
                        || LocalUserStore.IsNullOrWhiteSpace(visualReference)
                        || string.Equals(visualReference, "EmptyTexture", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    cosmetics.Add(itemId);
                }

                if (cosmetics.Count <= 0)
                {
                    throw new InvalidDataException("No cosmetic equipment definitions were found.");
                }

                var lootTables = FindComponent(serverData, "LootTableDefinitionCollection");
                var lootTableDefinitions = GetRequiredObjectArray(lootTables, "LootTables");
                var lootTablesById = new Dictionary<string, IDictionary>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < lootTableDefinitions.Length; i++)
                {
                    var table = RequireDictionary(lootTableDefinitions[i], "loot table definition");
                    var tableId = GetRequiredString(table, "Id");
                    if (lootTablesById.ContainsKey(tableId))
                    {
                        throw new InvalidDataException("Duplicate loot table definition " + tableId + ".");
                    }

                    lootTablesById.Add(tableId, table);
                }

                var levelsDir = Path.Combine(streamingAssetsDir, "levels");
                if (!Directory.Exists(levelsDir))
                {
                    throw new InvalidDataException("Mission levels directory is missing: " + levelsDir);
                }

                var mapdataPaths = Directory.GetFiles(levelsDir, "mapdata.json", SearchOption.AllDirectories);
                if (mapdataPaths.Length <= 0)
                {
                    throw new InvalidDataException("No mission mapdata.json files were found under " + levelsDir + ".");
                }

                var lootRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < mapdataPaths.Length; i++)
                {
                    var mapData = ReadCouponJsonObject(mapdataPaths[i]);
                    CollectLootTableRoots(mapData, lootRoots);
                }

                if (lootRoots.Count <= 0)
                {
                    throw new InvalidDataException("No mission loot table roots were found in mapdata.");
                }

                var missionDropItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in lootRoots)
                {
                    CollectLootTableItems(root, lootTablesById, missionDropItems, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }

                CollectMissionCompletionRewardItems(globals, missionDropItems);
                cosmetics.ExceptWith(missionDropItems);
                var result = new List<string>(cosmetics);
                result.Sort(StringComparer.OrdinalIgnoreCase);
                if (result.Count <= 0)
                {
                    throw new InvalidDataException("No cosmetics remain after excluding mission rewards.");
                }

                return result;
            }

            private static IDictionary ReadCouponJsonObject(string path)
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Required coupon data file is missing.", path);
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var root = CouponJson.DeserializeObject(json) as IDictionary;
                if (root == null)
                {
                    throw new InvalidDataException("Required coupon data file is not a JSON object: " + path);
                }

                return root;
            }

            private static IDictionary FindComponent(IDictionary root, string componentTypeName)
            {
                var components = GetRequiredObjectArray(root, "Components");
                for (var i = 0; i < components.Length; i++)
                {
                    var component = components[i] as IDictionary;
                    string typeName;
                    if (component != null
                        && TryGetString(component["TypeName"], out typeName)
                        && typeName.IndexOf(componentTypeName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return component;
                    }
                }

                throw new InvalidDataException("Required component was not found: " + componentTypeName + ".");
            }

            private static void CollectLootTableRoots(object value, HashSet<string> roots)
            {
                var dictionary = value as IDictionary;
                if (dictionary != null)
                {
                    string id;
                    string root;
                    if (dictionary.Contains("Id") && TryGetString(dictionary["Id"], out id)
                        && string.Equals(id, "LootTable", StringComparison.Ordinal)
                        && dictionary.Contains("Value") && TryGetString(dictionary["Value"], out root)
                        && !LocalUserStore.IsNullOrWhiteSpace(root))
                    {
                        roots.Add(root);
                    }

                    foreach (DictionaryEntry entry in dictionary)
                    {
                        CollectLootTableRoots(entry.Value, roots);
                    }

                    return;
                }

                var values = value as object[];
                if (values != null)
                {
                    for (var i = 0; i < values.Length; i++)
                    {
                        CollectLootTableRoots(values[i], roots);
                    }
                }
            }

            private static void CollectLootTableItems(string tableId, Dictionary<string, IDictionary> tables, HashSet<string> items, HashSet<string> path)
            {
                if (LocalUserStore.IsNullOrWhiteSpace(tableId) || !tables.ContainsKey(tableId))
                {
                    throw new InvalidDataException("Missing mission loot table reference: " + tableId + ".");
                }

                if (!path.Add(tableId))
                {
                    throw new InvalidDataException("Cycle in mission loot table references at " + tableId + ".");
                }

                var entries = GetRequiredObjectArray(tables[tableId], "Entries");
                for (var i = 0; i < entries.Length; i++)
                {
                    var entry = RequireDictionary(entries[i], "loot table entry");
                    if (!entry.Contains("Weight") || entry["Weight"] == null)
                    {
                        throw new InvalidDataException("Loot table entry in " + tableId + " has no weight.");
                    }

                    double weight;
                    try
                    {
                        weight = Convert.ToDouble(entry["Weight"], CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidDataException("Invalid loot weight in " + tableId + ".", ex);
                    }

                    if (double.IsNaN(weight) || double.IsInfinity(weight))
                    {
                        throw new InvalidDataException("Non-finite loot weight in " + tableId + ".");
                    }

                    if (weight <= 0)
                    {
                        continue;
                    }

                    var reference = RequireDictionary(entry.Contains("Reference") ? entry["Reference"] : null, "loot reference");
                    var typeName = GetRequiredString(reference, "TypeName");
                    var referenceId = GetRequiredString(reference, "Reference");
                    if (typeName.IndexOf("LogicItemLootReference", StringComparison.Ordinal) >= 0)
                    {
                        items.Add(referenceId);
                    }
                    else if (typeName.IndexOf("LootTableLootReference", StringComparison.Ordinal) >= 0)
                    {
                        CollectLootTableItems(referenceId, tables, items, path);
                    }
                    else
                    {
                        throw new InvalidDataException("Unsupported positive-weight loot reference " + typeName + " in " + tableId + ".");
                    }
                }

                path.Remove(tableId);
            }

            private static void CollectMissionCompletionRewardItems(IDictionary globals, HashSet<string> items)
            {
                var missionDefinitions = GetRequiredObjectArray(FindComponent(globals, "StaticMissionDataCollection"), "MissionDefinitions");
                if (missionDefinitions.Length <= 0)
                {
                    throw new InvalidDataException("No mission definitions were found in globals.json.");
                }

                for (var i = 0; i < missionDefinitions.Length; i++)
                {
                    var mission = RequireDictionary(missionDefinitions[i], "mission definition");
                    CollectMissionCompletionRewardItems(mission, "Rewards", items);
                    CollectMissionCompletionRewardItems(mission, "StoryRewards", items);
                }
            }

            private static void CollectMissionCompletionRewardItems(IDictionary mission, string rewardKey, HashSet<string> items)
            {
                if (!mission.Contains(rewardKey) || mission[rewardKey] == null)
                {
                    return;
                }

                var reward = RequireDictionary(mission[rewardKey], "mission " + rewardKey);
                if (!reward.Contains("ItemChanges") || reward["ItemChanges"] == null)
                {
                    return;
                }

                var changes = reward["ItemChanges"] as object[];
                if (changes == null)
                {
                    throw new InvalidDataException("Mission " + rewardKey + " item changes are invalid.");
                }

                for (var i = 0; i < changes.Length; i++)
                {
                    var change = RequireDictionary(changes[i], "mission item change");
                    if (!change.Contains("Delta") || change["Delta"] == null)
                    {
                        throw new InvalidDataException("Mission item change has no delta.");
                    }

                    int delta;
                    try
                    {
                        delta = Convert.ToInt32(change["Delta"], CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidDataException("Mission item change has an invalid delta.", ex);
                    }

                    if (delta > 0)
                    {
                        items.Add(GetRequiredString(change, "ItemDefintionId"));
                    }
                }
            }

            private static IDictionary RequireDictionary(object value, string description)
            {
                var dictionary = value as IDictionary;
                if (dictionary == null)
                {
                    throw new InvalidDataException("Invalid " + description + ".");
                }

                return dictionary;
            }

            private static object[] GetRequiredObjectArray(IDictionary dictionary, string key)
            {
                if (dictionary == null || !dictionary.Contains(key) || dictionary[key] == null)
                {
                    throw new InvalidDataException("Required JSON array is missing: " + key + ".");
                }

                var values = dictionary[key] as object[];
                if (values == null)
                {
                    throw new InvalidDataException("Required JSON value is not an array: " + key + ".");
                }

                return values;
            }

            private static string GetRequiredString(IDictionary dictionary, string key)
            {
                string value;
                if (dictionary == null || !dictionary.Contains(key) || !TryGetString(dictionary[key], out value)
                    || LocalUserStore.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidDataException("Required JSON string is missing: " + key + ".");
                }

                return value;
            }

            private static bool TryGetString(object value, out string result)
            {
                result = value as string;
                return result != null;
            }

            private static string GetCouponItemPackageSourceIdentity(string staticDataDir, string streamingAssetsDir)
            {
                var identity = new StringBuilder();
                AppendFileIdentity(identity, Path.Combine(staticDataDir, "serverData.json"));
                AppendFileIdentity(identity, Path.Combine(staticDataDir, "metagameplay.json"));
                AppendFileIdentity(identity, Path.Combine(staticDataDir, "ids.json"));
                AppendFileIdentity(identity, Path.Combine(staticDataDir, "globals.json"));

                var levelsDir = LocalUserStore.IsNullOrWhiteSpace(streamingAssetsDir)
                    ? null
                    : Path.Combine(streamingAssetsDir, "levels");
                if (LocalUserStore.IsNullOrWhiteSpace(levelsDir) || !Directory.Exists(levelsDir))
                {
                    identity.Append("|levels:missing");
                    return identity.ToString();
                }

                var mapdataPaths = Directory.GetFiles(levelsDir, "mapdata.json", SearchOption.AllDirectories);
                Array.Sort(mapdataPaths, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < mapdataPaths.Length; i++)
                {
                    AppendFileIdentity(identity, mapdataPaths[i]);
                }

                return identity.ToString();
            }

            private static void AppendFileIdentity(StringBuilder identity, string path)
            {
                identity.Append('|').Append(path);
                if (!File.Exists(path))
                {
                    identity.Append(":missing");
                    return;
                }

                var info = new FileInfo(path);
                identity.Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
            }

            private void LogBonusCosmeticsDataError(Exception exception)
            {
                if (_owner == null || _owner._logger == null)
                {
                    return;
                }

                try
                {
                    _owner._logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        level = "warning",
                        type = "coupon-data",
                        op = "bonus-cosmetics-unavailable",
                        error = exception != null ? exception.Message : "Unknown data error",
                    });
                }
                catch
                {
                }
            }

            private static void AddBuiltInCouponItemPackage(Dictionary<string, List<string>> packages, string packageTechnicalName, string[] itemIds)
            {
                if (packages == null || LocalUserStore.IsNullOrWhiteSpace(packageTechnicalName) || itemIds == null || itemIds.Length <= 0)
                {
                    return;
                }

                var items = new List<string>(itemIds.Length);
                for (var i = 0; i < itemIds.Length; i++)
                {
                    var itemId = itemIds[i];
                    if (!LocalUserStore.IsNullOrWhiteSpace(itemId))
                    {
                        items.Add(itemId);
                    }
                }

                if (items.Count > 0)
                {
                    packages[packageTechnicalName] = items;
                }
            }

            private static string NormalizeCouponCode(string value)
            {
                if (LocalUserStore.IsNullOrWhiteSpace(value))
                {
                    return string.Empty;
                }

                var sb = new StringBuilder(value.Length);
                for (var i = 0; i < value.Length; i++)
                {
                    var ch = value[i];
                    if (char.IsLetterOrDigit(ch))
                    {
                        sb.Append(char.ToUpperInvariant(ch));
                    }
                }

                return sb.ToString();
            }

            private static bool ListContainsIgnoreCase(List<string> list, string value)
            {
                if (list == null || LocalUserStore.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                for (var i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool EnsureCouponItemPackageItemsPresentNoLock(CareerSlot slot, List<string> items)
            {
                if (slot == null || items == null || items.Count <= 0)
                {
                    return false;
                }

                if (slot.ItemPossessions == null)
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                var requiredByItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < items.Count; i++)
                {
                    var itemId = items[i];
                    if (LocalUserStore.IsNullOrWhiteSpace(itemId))
                    {
                        continue;
                    }

                    int required;
                    if (!requiredByItem.TryGetValue(itemId, out required) || required < 0)
                    {
                        required = 0;
                    }

                    if (required < int.MaxValue)
                    {
                        required++;
                    }

                    requiredByItem[itemId] = required;
                }

                var changed = false;
                foreach (var kvp in requiredByItem)
                {
                    var possessionKey = kvp.Key + "|0|-1";
                    var required = kvp.Value;
                    int existing;
                    if (!slot.ItemPossessions.TryGetValue(possessionKey, out existing) || existing < 0)
                    {
                        existing = 0;
                    }

                    if (existing >= required)
                    {
                        continue;
                    }

                    AddOwnedItemAmount(slot, kvp.Key, required - existing);
                    changed = true;
                }

                return changed;
            }

            private static void AddOwnedItemAmount(CareerSlot slot, string itemId, int amount)
            {
                if (slot == null || LocalUserStore.IsNullOrWhiteSpace(itemId) || amount <= 0)
                {
                    return;
                }

                if (slot.ItemPossessions == null)
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                var possessionKey = itemId + "|0|-1";
                int existing;
                if (!slot.ItemPossessions.TryGetValue(possessionKey, out existing) || existing < 0)
                {
                    existing = 0;
                }

                if (existing > int.MaxValue - amount)
                {
                    slot.ItemPossessions[possessionKey] = int.MaxValue;
                    return;
                }

                slot.ItemPossessions[possessionKey] = existing + amount;
            }
        }
    }
}
