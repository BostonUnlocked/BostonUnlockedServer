using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed partial class LocalUserStore
    {
        private sealed class LocalCareerSeedService
        {
            private static readonly object ItemPackSeedLock = new object();
            private static List<string> CachedItemPackIds;
            private static string CachedItemPackIdsSourceDir;

            private static readonly object HairBeardSeedLock = new object();
            private static List<string> CachedHairBeardIds;
            private static string CachedHairBeardIdsSourceDir;
            private static int LoggedCosmeticSeed;

            private readonly LocalUserStore _owner;

            public LocalCareerSeedService(LocalUserStore owner)
            {
                _owner = owner;
            }

            public void ApplyNewCareerSeeds(CareerSlot slot)
            {
                if (slot == null)
                {
                    return;
                }

                SeedStarterCosmetics(slot);
                SeedStarterFreeSkills(slot);
                SeedStarterStartingInventory(slot);
                SeedExtraStarterCosmetics(slot);
                SeedStarterItemPackCosmetics(slot);
                SeedAllHairAndBeardOptions(slot);
            }

            public void ApplyOccupiedCareerBackfills(CareerSlot slot)
            {
                if (slot == null)
                {
                    return;
                }

                SeedExtraStarterCosmetics(slot);
                SeedStarterItemPackCosmetics(slot);
                SeedStarterHairAndBeardOptions(slot);
                SeedAllHairAndBeardOptions(slot);
            }

            private void SeedStarterItemPackCosmetics(CareerSlot slot)
            {
                if (slot == null)
                {
                    return;
                }

                try
                {
                    var packIds = GetOrLoadItemPackIds();
                    if (packIds == null || packIds.Count == 0)
                    {
                        return;
                    }

                    for (var i = 0; i < packIds.Count; i++)
                    {
                        var itemId = packIds[i];
                        if (!LocalUserStore.IsNullOrWhiteSpace(itemId))
                        {
                            OwnItem(slot, itemId);
                        }
                    }
                }
                catch
                {
                }
            }

            private static void SeedExtraStarterCosmetics(CareerSlot slot)
            {
                if (slot == null)
                {
                    return;
                }

                try
                {
                    OwnItem(slot, "Item_Tribal1Black");
                    OwnItem(slot, "Item_MaoriTribal1Black");
                    OwnItem(slot, "Item_MaoriForearmTribal1Black");
                    OwnItem(slot, "Item_DoubleDragon1Black");
                    OwnItem(slot, "Item_DragonHead1Black");
                    OwnItem(slot, "Item_Hex1Black");
                    OwnItem(slot, "Item_LadyLuckTribal1Black");
                    OwnItem(slot, "Item_MayanStyle1");
                    OwnItem(slot, "Item_MayanStyle2");

                    OwnItem(slot, "Item_HalloweenerFacepaint1");
                    OwnItem(slot, "Item_NativeIndianFacepaint1");
                    OwnItem(slot, "Item_JapaneseFacepaint");
                    OwnItem(slot, "Item_NeonFaceTribal1");
                    OwnItem(slot, "Item_NeonFaceTribal2");

                    OwnItem(slot, "Item_RiotHelmet");
                    OwnItem(slot, "Item_BasicCap1");
                    OwnItem(slot, "Item_BasicCap2");
                    OwnItem(slot, "Item_Pack4Fedora");
                    OwnItem(slot, "Item_AstralHelmet");
                    OwnItem(slot, "Item_CowboyHat");

                    OwnItem(slot, "Item_UrbanVisor");
                    OwnItem(slot, "Item_NeoGoggles");
                    OwnItem(slot, "Item_Pack2Goggles");
                    OwnItem(slot, "Item_Pack3GlassesNeo");
                    OwnItem(slot, "Item_Pack3GlassesRaybanDark");
                    OwnItem(slot, "Item_Pack3GlassesRound");
                    OwnItem(slot, "Item_Pack3GlassesRoundDark");
                    OwnItem(slot, "Item_DocMask");
                    OwnItem(slot, "Item_Pack4GasMask");
                    OwnItem(slot, "Item_Pack3Cigar");
                    OwnItem(slot, "Item_ShamanMask1");

                    OwnItem(slot, "Item_UrbanVest");
                    OwnItem(slot, "Item_NeoHarness");
                    OwnItem(slot, "Item_BikerJacket");
                    OwnItem(slot, "Item_RoadRageHarness");
                    OwnItem(slot, "Item_ShamanTop");
                    OwnItem(slot, "Item_RiotVest");
                    OwnItem(slot, "Item_RiotVestKE");
                    OwnItem(slot, "Item_BasicHoboJacket");
                    OwnItem(slot, "Item_HoboJacket2");
                    OwnItem(slot, "Item_GothJacket");
                    OwnItem(slot, "Item_Pack1CroppedJacket");
                    OwnItem(slot, "Item_LeatherVest");

                    OwnItem(slot, "Item_UrbanUndershirt");
                    OwnItem(slot, "Item_NeoUndershirt");
                    OwnItem(slot, "Item_Pack2BasicShirt");
                    OwnItem(slot, "Item_CeramicPlating");
                    OwnItem(slot, "Item_BasicWifebeater");
                    OwnItem(slot, "Item_TopBlue");
                    OwnItem(slot, "Item_TopPurple");
                    OwnItem(slot, "Item_BasicTubeTop");
                    OwnItem(slot, "Item_ShamanChestpiece");
                    OwnItem(slot, "Item_Pack1TopRaider");

                    OwnItem(slot, "Item_UrbanGloves");
                    OwnItem(slot, "Item_NeoGlovesHigh");
                    OwnItem(slot, "Item_BikerGloves");
                    OwnItem(slot, "Item_HideBracers");
                    OwnItem(slot, "Item_RiotGloves");
                    OwnItem(slot, "Item_BagGloves");

                    OwnItem(slot, "Item_UrbanBoots");
                    OwnItem(slot, "Item_NeoBootsHigh");
                    OwnItem(slot, "Item_BikerBoots");
                    OwnItem(slot, "Item_BasicCombatBoots");
                    OwnItem(slot, "Item_StreetBoots");
                    OwnItem(slot, "Item_RoadRageBoots");
                    OwnItem(slot, "Item_RiotBoots");
                    OwnItem(slot, "Item_ShamanSandals");
                    OwnItem(slot, "Item_Pack4Sandals");
                    OwnItem(slot, "Item_Pack1Sneakers");
                    OwnItem(slot, "Item_Pack4TwoToneShoes");
                    OwnItem(slot, "Item_Pack2HighHeelsBoots");

                    OwnItem(slot, "Item_UrbanPants");
                    OwnItem(slot, "Item_NeoBelt");
                    OwnItem(slot, "Item_RoadRagePants");
                    OwnItem(slot, "Item_ShamanKilt");
                    OwnItem(slot, "Item_BasicSkirt");
                    OwnItem(slot, "Item_Pack1Skirt1");
                    OwnItem(slot, "Item_Pack1Skirt2");
                    OwnItem(slot, "Item_Pack1Skirt5");
                    OwnItem(slot, "Item_RiggerPants");
                    OwnItem(slot, "Item_StreetPants");
                    OwnItem(slot, "Item_Pack2SuitPants");
                    OwnItem(slot, "Item_Pack1JeansModern");
                    OwnItem(slot, "Item_Pack1JeansBlack");
                    OwnItem(slot, "Item_Pack1JeansBlue02");
                    OwnItem(slot, "Item_Jeans01");
                    OwnItem(slot, "Item_Jeans02");
                    OwnItem(slot, "Item_Jeans03");
                    OwnItem(slot, "Item_Jeans04");
                    OwnItem(slot, "Item_Jeans05");
                    OwnItem(slot, "Item_Jeans06");
                    OwnItem(slot, "Item_Jeans07");
                    OwnItem(slot, "Item_HotPants");

                    OwnItem(slot, "Item_UrbanUnderpants");
                    OwnItem(slot, "Item_NeoUnderpants");
                    OwnItem(slot, "Item_Pack1PantsKneepads");
                    OwnItem(slot, "Item_Pack1StockingsCyber");
                    OwnItem(slot, "Item_Pack1StockingsKneehigh");
                    OwnItem(slot, "Item_Pack1StockingsStripes");
                }
                catch
                {
                }
            }

            private void SeedAllHairAndBeardOptions(CareerSlot slot)
            {
                if (slot == null)
                {
                    return;
                }

                try
                {
                    var ids = GetOrLoadHairAndBeardIds();
                    if (ids == null || ids.Count == 0)
                    {
                        return;
                    }

                    for (var i = 0; i < ids.Count; i++)
                    {
                        var itemId = ids[i];
                        if (!LocalUserStore.IsNullOrWhiteSpace(itemId))
                        {
                            if (string.Equals(itemId, "Item_Horns1", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(itemId, "Item_HornyHorns", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            OwnItem(slot, itemId);
                        }
                    }
                }
                catch
                {
                }
            }

            private List<string> GetOrLoadHairAndBeardIds()
            {
                try
                {
                    var staticDataDir = _owner._options != null ? _owner._options.StaticDataDir : null;
                    if (LocalUserStore.IsNullOrWhiteSpace(staticDataDir) || !Directory.Exists(staticDataDir))
                    {
                        return new List<string>();
                    }

                    lock (HairBeardSeedLock)
                    {
                        if (CachedHairBeardIds != null && string.Equals(CachedHairBeardIdsSourceDir, staticDataDir, StringComparison.OrdinalIgnoreCase))
                        {
                            return CachedHairBeardIds;
                        }

                        CachedHairBeardIds = LoadIdsByPrefixes(staticDataDir, new string[] { "Item_Hair", "Item_Beard", "Item_Horn" });
                        CachedHairBeardIdsSourceDir = staticDataDir;

                        try
                        {
                            if (_owner._logger != null && System.Threading.Interlocked.Exchange(ref LoggedCosmeticSeed, 1) == 0)
                            {
                                var total = CachedHairBeardIds != null ? CachedHairBeardIds.Count : 0;
                                var hair = 0;
                                var beard = 0;
                                var horn = 0;
                                if (CachedHairBeardIds != null)
                                {
                                    for (var i = 0; i < CachedHairBeardIds.Count; i++)
                                    {
                                        var k = CachedHairBeardIds[i];
                                        if (LocalUserStore.IsNullOrWhiteSpace(k))
                                        {
                                            continue;
                                        }

                                        if (k.StartsWith("Item_Hair", StringComparison.OrdinalIgnoreCase)) hair++;
                                        else if (k.StartsWith("Item_Beard", StringComparison.OrdinalIgnoreCase)) beard++;
                                        else if (k.StartsWith("Item_Horn", StringComparison.OrdinalIgnoreCase)) horn++;
                                    }
                                }

                                _owner._logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "cosmetic-seed-ids",
                                    staticDataDir = staticDataDir,
                                    total = total,
                                    hair = hair,
                                    beard = beard,
                                    horn = horn,
                                    sample0 = (CachedHairBeardIds != null && CachedHairBeardIds.Count > 0) ? CachedHairBeardIds[0] : null,
                                    sampleLast = (CachedHairBeardIds != null && CachedHairBeardIds.Count > 0) ? CachedHairBeardIds[CachedHairBeardIds.Count - 1] : null,
                                });
                            }
                        }
                        catch
                        {
                        }

                        return CachedHairBeardIds;
                    }
                }
                catch
                {
                    return new List<string>();
                }
            }

            private List<string> GetOrLoadItemPackIds()
            {
                try
                {
                    var staticDataDir = _owner._options != null ? _owner._options.StaticDataDir : null;
                    if (LocalUserStore.IsNullOrWhiteSpace(staticDataDir) || !Directory.Exists(staticDataDir))
                    {
                        return new List<string>();
                    }

                    lock (ItemPackSeedLock)
                    {
                        if (CachedItemPackIds != null && string.Equals(CachedItemPackIdsSourceDir, staticDataDir, StringComparison.OrdinalIgnoreCase))
                        {
                            return CachedItemPackIds;
                        }

                        CachedItemPackIds = LoadIdsByPrefixes(staticDataDir, new string[] { "Item_Pack" });
                        CachedItemPackIdsSourceDir = staticDataDir;
                        return CachedItemPackIds;
                    }
                }
                catch
                {
                    return new List<string>();
                }
            }

            private static List<string> LoadIdsByPrefixes(string staticDataDir, string[] prefixes)
            {
                var result = new List<string>();
                try
                {
                    if (LocalUserStore.IsNullOrWhiteSpace(staticDataDir) || !Directory.Exists(staticDataDir))
                    {
                        return result;
                    }

                    if (prefixes == null || prefixes.Length == 0)
                    {
                        return result;
                    }

                    var path = Path.Combine(staticDataDir, "ids.json");
                    if (!File.Exists(path))
                    {
                        return result;
                    }

                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (LocalUserStore.IsNullOrWhiteSpace(json))
                    {
                        return result;
                    }

                    IDictionary root = null;
                    try
                    {
                        root = LocalUserStore.Json.DeserializeObject(json) as IDictionary;
                    }
                    catch
                    {
                        root = null;
                    }

                    if (root != null && root.Count > 0)
                    {
                        foreach (DictionaryEntry entry in root)
                        {
                            var key = entry.Key as string;
                            if (LocalUserStore.IsNullOrWhiteSpace(key))
                            {
                                continue;
                            }

                            for (var i = 0; i < prefixes.Length; i++)
                            {
                                var prefix = prefixes[i];
                                if (LocalUserStore.IsNullOrWhiteSpace(prefix))
                                {
                                    continue;
                                }

                                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                {
                                    result.Add(key);
                                    break;
                                }
                            }
                        }
                    }

                    if (result.Count == 0)
                    {
                        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (var i = 0; i < prefixes.Length; i++)
                        {
                            var prefix = prefixes[i];
                            if (LocalUserStore.IsNullOrWhiteSpace(prefix))
                            {
                                continue;
                            }

                            var needle = "\"" + prefix;
                            var index = 0;
                            while (index >= 0 && index < json.Length)
                            {
                                index = json.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase);
                                if (index < 0)
                                {
                                    break;
                                }

                                var keyStart = index + 1;
                                var keyEnd = json.IndexOf('"', keyStart);
                                if (keyEnd > keyStart)
                                {
                                    var key = json.Substring(keyStart, keyEnd - keyStart);
                                    if (!LocalUserStore.IsNullOrWhiteSpace(key))
                                    {
                                        found.Add(key);
                                    }
                                }

                                index = keyEnd > 0 ? keyEnd + 1 : (index + needle.Length);
                            }
                        }

                        if (found.Count > 0)
                        {
                            foreach (var key in found)
                            {
                                result.Add(key);
                            }
                        }
                    }

                    result.Sort(StringComparer.OrdinalIgnoreCase);
                    return result;
                }
                catch
                {
                    return result;
                }
            }

            private static void SeedStarterFreeSkills(CareerSlot slot)
            {
                if (slot == null)
                {
                    return;
                }

                if (slot.SkillTreeDefinitions == null)
                {
                    slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
                }

                EnsureSkill(slot, "ShamanSkillTree", "ShamanLevelSkill_0");
                EnsureSkill(slot, "MageSkillTree", "MageLevelSkill_0");
                EnsureSkill(slot, "BladeSkillTree", "BladeLevelSkill_0");
                EnsureSkill(slot, "BruteSkillTree", "BruteLevelSkill_0");
                EnsureSkill(slot, "PistolSkillTree", "PistolLevelSkill_0");
                EnsureSkill(slot, "ShotgunSkillTree", "ShotgunLevelSkill_0");
                EnsureSkill(slot, "AssaultSkillTree", "AssaultLevelSkill_0");
                EnsureSkill(slot, "HackingSkillTree", "HackingLevelSkill_0");
                EnsureSkill(slot, "RiggingSkillTree", "RiggingLevelSkill_0");
            }

            private static void EnsureSkill(CareerSlot slot, string treeTechnicalName, string skillTechnicalName)
            {
                if (slot == null || LocalUserStore.IsNullOrWhiteSpace(treeTechnicalName) || LocalUserStore.IsNullOrWhiteSpace(skillTechnicalName))
                {
                    return;
                }

                if (slot.SkillTreeDefinitions == null)
                {
                    slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
                }

                string[] existing;
                if (!slot.SkillTreeDefinitions.TryGetValue(treeTechnicalName, out existing) || existing == null || existing.Length == 0)
                {
                    slot.SkillTreeDefinitions[treeTechnicalName] = new[] { skillTechnicalName };
                    return;
                }

                for (var i = 0; i < existing.Length; i++)
                {
                    if (string.Equals(existing[i], skillTechnicalName, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                var updated = new string[existing.Length + 1];
                for (var i = 0; i < existing.Length; i++)
                {
                    updated[i] = existing[i];
                }
                updated[existing.Length] = skillTechnicalName;
                slot.SkillTreeDefinitions[treeTechnicalName] = updated;
            }

            private static void SeedStarterStartingInventory(CareerSlot slot)
            {
                if (slot == null)
                {
                    return;
                }

                OwnItem(slot, "Automatics_IngramSmartgun_Tier_00");
                OwnItem(slot, "Spellcasting_PowerFocus_Tier_00");
                OwnItem(slot, "Shotgun_RemingtonSportsman_Tier_00");
                OwnItem(slot, "Club_NailBoard_Tier_00");
                OwnItem(slot, "Blade_Cleaver_Tier_00");
                OwnItem(slot, "Pistol_AresLightfire_Tier_00");
                OwnItem(slot, "Hacking_Mcd1Deck_Tier_00");
                OwnItem(slot, "Conjuring_ConjuringFocus_Tier_00");
                OwnItem(slot, "Rigging_ControlRigInterface_Tier_00");
            }

            private static void SeedStarterCosmetics(CareerSlot slot)
            {
                if (slot == null)
                {
                    return;
                }

                if (slot.EquippedItems == null)
                {
                    slot.EquippedItems = new Dictionary<string, CareerSlot.EquippedSlotState>(StringComparer.OrdinalIgnoreCase);
                }
                if (slot.ItemPossessions == null)
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                SeedSlot(slot, 196913UL, PlayerCharacterDefaultValues.Boots);
                SeedSlot(slot, 196910UL, PlayerCharacterDefaultValues.UpperBody);
                SeedSlot(slot, 196911UL, PlayerCharacterDefaultValues.LowerBody);
                SeedSlot(slot, 196914UL, PlayerCharacterDefaultValues.UpperUnderware);
                SeedSlot(slot, 196916UL, PlayerCharacterDefaultValues.Hair);
                SeedSlot(slot, 196912UL, PlayerCharacterDefaultValues.Gloves);

                SeedStarterHairAndBeardOptions(slot);
            }

            private static void SeedStarterHairAndBeardOptions(CareerSlot slot)
            {
                OwnItem(slot, "Item_HairAfro");
                OwnItem(slot, "Item_HairBob");
                OwnItem(slot, "Item_HairBraids");
                OwnItem(slot, "Item_HairElvis");
                OwnItem(slot, "Item_HairLong");
                OwnItem(slot, "Item_HairMohawk");
                OwnItem(slot, "Item_HairPage");
                OwnItem(slot, "Item_HairPony");
                OwnItem(slot, "Item_HairQuiff");
                OwnItem(slot, "Item_HairSidebraid");
                OwnItem(slot, "Item_HairSidecut");
                OwnItem(slot, "Item_HairUndercut");
                OwnItem(slot, "Item_HairWarhawk");

                OwnItem(slot, "Item_BeardGoatee");
                OwnItem(slot, "Item_BeardBigMustache");
                OwnItem(slot, "Item_BeardZappa");
                OwnItem(slot, "Item_BeardKlingon");
            }

            private static void OwnItem(CareerSlot slot, string itemId)
            {
                OwnItem(slot, itemId, 0, -1);
            }

            private static void OwnItem(CareerSlot slot, string itemId, int quality, int flavour)
            {
                if (slot == null || LocalUserStore.IsNullOrWhiteSpace(itemId))
                {
                    return;
                }

                if (quality < 0)
                {
                    quality = 0;
                }
                if (quality > byte.MaxValue)
                {
                    quality = byte.MaxValue;
                }
                if (flavour < short.MinValue)
                {
                    flavour = short.MinValue;
                }
                if (flavour > short.MaxValue)
                {
                    flavour = short.MaxValue;
                }

                if (slot.ItemPossessions == null)
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                var possessionKey = itemId + "|" + quality.ToString(CultureInfo.InvariantCulture) + "|" + flavour.ToString(CultureInfo.InvariantCulture);
                int amount;
                if (!slot.ItemPossessions.TryGetValue(possessionKey, out amount) || amount <= 0)
                {
                    slot.ItemPossessions[possessionKey] = 1;
                }
            }

            private static void SeedSlot(CareerSlot slot, ulong slotId, string itemId)
            {
                if (slot == null || slotId == 0UL || LocalUserStore.IsNullOrWhiteSpace(itemId))
                {
                    return;
                }

                var slotKey = slotId.ToString(CultureInfo.InvariantCulture);
                CareerSlot.EquippedSlotState existing;
                if (!slot.EquippedItems.TryGetValue(slotKey, out existing) || existing == null || LocalUserStore.IsNullOrWhiteSpace(existing.ItemId))
                {
                    slot.EquippedItems[slotKey] = CareerSlot.EquippedSlotState.Create(itemId, -1, 0, -1);
                }

                var possessionKey = itemId + "|0|-1";
                int amount;
                if (!slot.ItemPossessions.TryGetValue(possessionKey, out amount) || amount <= 0)
                {
                    slot.ItemPossessions[possessionKey] = 1;
                }
            }
        }
    }
}
