using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.Definitions;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Career
{
    public sealed class CareerInfoGenerator
    {
        private const string FallbackZippedCareerInfo = "Y2AAgjQGZgZ8gBeI/YGq0hhyGDIZ8hhSGYIYSoE0iFWESxMjAwA=";

        private readonly RequestLogger _logger;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.Ordinal);

        public CareerInfoGenerator(RequestLogger logger)
        {
            _logger = logger;
        }

        public string GetZippedCareerInfo()
        {
            return GetZippedCareerInfo(Guid.NewGuid(), 0, "OfflineRunner", false);
        }

        public string GetZippedCareerInfo(string characterName, bool pendingPersistenceCreation)
        {
            return GetZippedCareerInfo(Guid.NewGuid(), 0, characterName, pendingPersistenceCreation);
        }

        public string GetZippedCareerInfo(Guid identityGuid, int careerIndex, string characterName, bool pendingPersistenceCreation)
        {
            lock (_cacheLock)
            {
                var cacheKey = identityGuid.ToString() + "|" + careerIndex.ToString() + "|" + (characterName ?? string.Empty) + "|" + (pendingPersistenceCreation ? "1" : "0") + "|";
                string cached;
                if (_cache.TryGetValue(cacheKey, out cached) && !IsNullOrWhiteSpace(cached))
                {
                    return cached;
                }

                var generated = Generate(identityGuid, careerIndex, characterName, pendingPersistenceCreation);
                if (!IsNullOrWhiteSpace(generated))
                {
                    _cache[cacheKey] = generated;
                    return generated;
                }

                _cache[cacheKey] = FallbackZippedCareerInfo;
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "career-info-generate",
                    status = "fallback",
                    blobLength = FallbackZippedCareerInfo.Length,
                });
                return FallbackZippedCareerInfo;
            }
        }

        public string GetZippedCareerInfo(Guid identityGuid, int careerIndex, CareerSlot slot)
        {
            var name = slot != null ? slot.CharacterName : null;
            var pending = slot != null && slot.PendingPersistenceCreation;
            return GetZippedCareerInfo(identityGuid, careerIndex, name, pending, slot);
        }

        private string GetZippedCareerInfo(Guid identityGuid, int careerIndex, string characterName, bool pendingPersistenceCreation, CareerSlot slot)
        {
            lock (_cacheLock)
            {
                var bodytype = slot != null ? slot.Bodytype : 0UL;
                var skin = slot != null ? slot.SkinTextureIndex : 0;
                var story = slot != null ? slot.BackgroundStory : 0UL;

                var portraitPath = slot != null ? (slot.PortraitPath ?? string.Empty) : string.Empty;
                var voiceset = slot != null ? (slot.Voiceset ?? string.Empty) : string.Empty;
                var wants = slot != null && slot.WantsBackgroundChange;
                var equippedKey = BuildEquippedItemsKey(slot);
                var loadoutKey = slot != null
                    ? ((slot.PrimaryWeaponItemId ?? string.Empty) + ":" + slot.PrimaryWeaponInventoryKey.ToString(CultureInfo.InvariantCulture)
                        + "|" + (slot.SecondaryWeaponItemId ?? string.Empty) + ":" + slot.SecondaryWeaponInventoryKey.ToString(CultureInfo.InvariantCulture)
                        + "|" + (slot.ArmorItemId ?? string.Empty) + ":" + slot.ArmorInventoryKey.ToString(CultureInfo.InvariantCulture))
                    : string.Empty;
                var storyKey = BuildStoryProgressKey(slot);
                var skillKey = BuildSkillTreeKey(slot);
                var itemKey = BuildItemPossessionsKey(slot);

                var karma = slot != null ? slot.Karma : 0;
                var nuyen = slot != null ? slot.Nuyen : 0;
                var cacheKey = identityGuid.ToString() + "|" + careerIndex.ToString() + "|" + (characterName ?? string.Empty) + "|" + (pendingPersistenceCreation ? "1" : "0") + "|" + bodytype.ToString() + "|" + skin.ToString() + "|" + story.ToString() + "|" + portraitPath + "|" + voiceset + "|" + (wants ? "1" : "0") + "|" + karma.ToString(CultureInfo.InvariantCulture) + "|" + nuyen.ToString(CultureInfo.InvariantCulture) + "|" + equippedKey + "|" + loadoutKey + "|" + storyKey + "|" + skillKey + "|" + itemKey;
                string cached;
                if (_cache.TryGetValue(cacheKey, out cached) && !IsNullOrWhiteSpace(cached))
                {
                    return cached;
                }

                var generated = Generate(identityGuid, careerIndex, characterName, pendingPersistenceCreation, slot);
                if (!IsNullOrWhiteSpace(generated))
                {
                    _cache[cacheKey] = generated;
                    return generated;
                }

                _cache[cacheKey] = FallbackZippedCareerInfo;
                return FallbackZippedCareerInfo;
            }
        }

        private string Generate(Guid identityGuid, int careerIndex, string characterName, bool pendingPersistenceCreation)
        {
            return Generate(identityGuid, careerIndex, characterName, pendingPersistenceCreation, null);
        }

        private string Generate(Guid identityGuid, int careerIndex, string characterName, bool pendingPersistenceCreation, CareerSlot slot)
        {
            try
            {
                var gci = new GameCareerInfo();
#pragma warning disable 618 // PlayerCharacterSnapshot() is obsolete; recommended factory is in unavailable server-side DLLs.
                var pcs = new PlayerCharacterSnapshot();
#pragma warning restore 618
                var unlock = new UnlockContainer();
                var inventory = BuildInventoryFromSlot(slot);
                var story = BuildStoryProgress(slot);
                var mgd = new MetagameplayData(string.Empty, unlock, string.Empty, inventory, story);
                var ci = new CreationInfo(pendingPersistenceCreation, false);

                var pcInv = new PlayerCharacterInventory();
                var primaryItemId = (slot != null && !IsNullOrWhiteSpace(slot.PrimaryWeaponItemId)) ? slot.PrimaryWeaponItemId : PlayerCharacterDefaultValues.PrimaryWeapon;
                var primaryKey = slot != null ? slot.PrimaryWeaponInventoryKey : 0;
                pcInv.PrimaryWeapon = CreateItemWithId(primaryItemId, primaryKey);

                var secondaryItemId = (slot != null && !IsNullOrWhiteSpace(slot.SecondaryWeaponItemId)) ? slot.SecondaryWeaponItemId : PlayerCharacterDefaultValues.SecondaryWeapon;
                var secondaryKey = slot != null ? slot.SecondaryWeaponInventoryKey : 1;
                pcInv.SecondaryWeapon = CreateItemWithId(secondaryItemId, secondaryKey);

                var armorItemId = (slot != null && !IsNullOrWhiteSpace(slot.ArmorItemId)) ? slot.ArmorItemId : PlayerCharacterDefaultValues.Armor;
                var armorKey = slot != null ? slot.ArmorInventoryKey : 2;
                pcInv.Armor = CreateItemWithId(armorItemId, armorKey);

                pcs.CharacterName = IsNullOrWhiteSpace(characterName) ? "OfflineRunner" : characterName;

                // The client expects identifiers like "{accountGuid}:{careerIndex}".
                // If the format is wrong, PlayerCharacterSnapshot.AccountId throws InvalidDataException.
                var accountGuid = identityGuid;
                if (accountGuid == Guid.Empty)
                {
                    accountGuid = Guid.NewGuid();
                }
                if (careerIndex < 0)
                {
                    careerIndex = 0;
                }
                pcs.CharacterIdentifier = StringGuidUtility.CreateIndexed(accountGuid, careerIndex, ":");
                // Must match StaticData.Globals.VersioningInfo.Version (see local static-data/globals.json: Version=48).
                pcs.DataVersion = 48;
                pcs.PlayerId = 1UL;
                pcs.PlayerCharacterInventory = pcInv;
                pcs.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
                if (slot != null && slot.SkillTreeDefinitions != null && slot.SkillTreeDefinitions.Count > 0)
                {
                    foreach (var kvp in slot.SkillTreeDefinitions)
                    {
                        if (string.IsNullOrEmpty(kvp.Key) || kvp.Value == null)
                        {
                            continue;
                        }
                        pcs.SkillTreeDefinitions[kvp.Key] = kvp.Value;
                    }
                }
                pcs.Wallet = new Wallet();
                pcs.Wallet.Reset(CurrencyId.Karma, slot != null ? slot.Karma : 0, 0);
                pcs.Wallet.Reset(CurrencyId.Nuyen, slot != null ? slot.Nuyen : 0, 0);
                pcs.PortraitPath = (slot != null && !IsNullOrWhiteSpace(slot.PortraitPath)) ? slot.PortraitPath : PlayerCharacterDefaultValues.PortraitPath;
                pcs.Voiceset = (slot != null && !IsNullOrWhiteSpace(slot.Voiceset)) ? slot.Voiceset : PlayerCharacterDefaultValues.Voiceset;
                pcs.Bodytype = (slot != null && slot.Bodytype != 0UL) ? slot.Bodytype : PlayerCharacterDefaultValues.Bodytype;
                pcs.SkinTextureIndex = (slot != null) ? slot.SkinTextureIndex : PlayerCharacterDefaultValues.SkinTextureIndex;
                pcs.BackgroundStory = (slot != null && slot.BackgroundStory != 0UL) ? slot.BackgroundStory : PlayerCharacterDefaultValues.BackgroundStory;
                pcs.WantsBackgroundChange = slot != null && slot.WantsBackgroundChange;

                // Apply persisted cosmetic equipment slots (hair/clothes/etc).
                if (slot != null && slot.EquippedItems != null && slot.EquippedItems.Count > 0)
                {
                    ApplyEquippedItems(pcInv, slot.EquippedItems);
                }

                gci.PlayerCharacterSnapshot = pcs;
                gci.MetagameplayData = mgd;
                gci.CreationInfo = ci;

                var blob = GameCareerInfoSerializer.SerializeGameCareerInfo(gci);

                if (IsNullOrWhiteSpace(blob))
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "career-info-generate",
                        status = "failed",
                        reason = "serializer-returned-empty",
                    });
                    return null;
                }

                if (!IsValidBase64(blob))
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "career-info-generate",
                        status = "failed",
                        reason = "invalid-blob",
                    });
                    return null;
                }

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "career-info-generate",
                    status = "ok",
                    blobLength = blob.Length,
                    managedDir = "Dependencies",
                    characterName = characterName,
                    pendingPersistenceCreation = pendingPersistenceCreation,
                });
                return blob;
            }
            catch (Exception ex)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "career-info-generate",
                    status = "failed",
                    reason = "reflection-exception",
                    message = ex.Message,
                });
                return null;
            }
        }

        private static Inventory BuildInventoryFromSlot(CareerSlot slot)
        {
            var inventory = new Inventory();
            if (slot == null || slot.ItemPossessions == null || slot.ItemPossessions.Count <= 0)
            {
                return inventory;
            }

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

                // InventorySerializer writes Amount as uint8, so clamp to 255.
                if (amount > 255)
                {
                    amount = 255;
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

                // ItemSerializer writes Quality as uint8 and FlavourIndex as int16.
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
            return inventory;
        }

        private static string BuildItemPossessionsKey(CareerSlot slot)
        {
            try
            {
                if (slot == null || slot.ItemPossessions == null || slot.ItemPossessions.Count <= 0)
                {
                    return string.Empty;
                }

                var keys = new List<string>(slot.ItemPossessions.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);

                var sb = new StringBuilder();
                for (var i = 0; i < keys.Count; i++)
                {
                    var k = keys[i];
                    int v;
                    if (IsNullOrWhiteSpace(k) || !slot.ItemPossessions.TryGetValue(k, out v) || v <= 0)
                    {
                        continue;
                    }
                    sb.Append(k);
                    sb.Append('=');
                    sb.Append(v.ToString(CultureInfo.InvariantCulture));
                    sb.Append(';');
                }

                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static StoryProgressRuntimestate BuildStoryProgress(CareerSlot slot)
        {
            var story = new StoryProgressRuntimestate();
            if (slot == null)
            {
                return story;
            }

            var hasAnyMissionState = slot.MainCampaignMissionStates != null && slot.MainCampaignMissionStates.Count > 0;
            if (!hasAnyMissionState && slot.MainCampaignCurrentChapter <= 0)
            {
                // Keep the default "no runtime story" behavior for fresh careers.
                return story;
            }

            var main = new RuntimeStoryline();
            main.Storyline = "Main Campaign";
            main.CurrentChapter = slot.MainCampaignCurrentChapter;
            main.InteractedNpcs = new List<string>();
            main.RequiredUnlocksForCurrentChapter = new List<string>();
            main.RuntimeMissions = new List<RuntimeMission>();

            if (slot.MainCampaignInteractedNpcs != null && slot.MainCampaignInteractedNpcs.Count > 0)
            {
                for (var i = 0; i < slot.MainCampaignInteractedNpcs.Count; i++)
                {
                    var npcId = slot.MainCampaignInteractedNpcs[i];
                    if (IsNullOrWhiteSpace(npcId))
                    {
                        continue;
                    }
                    if (!main.InteractedNpcs.Contains(npcId))
                    {
                        main.InteractedNpcs.Add(npcId);
                    }
                }
            }

            if (slot.MainCampaignMissionStates != null)
            {
                foreach (var kvp in slot.MainCampaignMissionStates)
                {
                    if (IsNullOrWhiteSpace(kvp.Key) || IsNullOrWhiteSpace(kvp.Value))
                    {
                        continue;
                    }

                    StoryMissionstate parsed;
                    if (!TryParseStoryMissionState(kvp.Value, out parsed))
                    {
                        continue;
                    }

                    var rm = new RuntimeMission();
                    rm.Mission = kvp.Key;
                    rm.State = parsed;
                    rm.IsOptionalMission = false;
                    main.RuntimeMissions.Add(rm);
                }
            }

            story.Storylines.Add(main);
            return story;
        }

        private static string BuildSkillTreeKey(CareerSlot slot)
        {
            if (slot == null || slot.SkillTreeDefinitions == null || slot.SkillTreeDefinitions.Count == 0)
            {
                return string.Empty;
            }

            try
            {
                var keys = new List<string>();
                foreach (var k in slot.SkillTreeDefinitions.Keys)
                {
                    if (!IsNullOrWhiteSpace(k))
                    {
                        keys.Add(k);
                    }
                }
                keys.Sort(StringComparer.Ordinal);

                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < keys.Count; i++)
                {
                    var tree = keys[i];
                    string[] skills;
                    if (!slot.SkillTreeDefinitions.TryGetValue(tree, out skills) || skills == null)
                    {
                        continue;
                    }

                    var list = new List<string>();
                    for (var j = 0; j < skills.Length; j++)
                    {
                        var s = skills[j];
                        if (!IsNullOrWhiteSpace(s) && !list.Contains(s))
                        {
                            list.Add(s);
                        }
                    }
                    list.Sort(StringComparer.Ordinal);

                    sb.Append(tree);
                    sb.Append(':');
                    for (var j = 0; j < list.Count; j++)
                    {
                        if (j > 0)
                        {
                            sb.Append(',');
                        }
                        sb.Append(list[j]);
                    }
                    sb.Append(';');
                }
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryParseStoryMissionState(string value, out StoryMissionstate parsed)
        {
            parsed = StoryMissionstate.Available;
            if (IsNullOrWhiteSpace(value))
            {
                return false;
            }

            try
            {
                parsed = (StoryMissionstate)Enum.Parse(typeof(StoryMissionstate), value.Trim(), true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Item CreateItemWithId(string itemId, int inventoryKey)
        {
            var item = new Item();
            item.ItemId = itemId ?? string.Empty;
            item.InventoryKey = inventoryKey;
            item.Amount = 1;
            return item;
        }

        private static void ApplyEquippedItems(PlayerCharacterInventory inventory, Dictionary<string, string> equipped)
        {
            if (inventory == null || equipped == null || equipped.Count == 0)
            {
                return;
            }

            var keys = new List<string>(equipped.Keys);
            keys.Sort(StringComparer.Ordinal);

            var nextKey = 10;
            for (var i = 0; i < keys.Count; i++)
            {
                var slotKey = keys[i];
                if (IsNullOrWhiteSpace(slotKey))
                {
                    continue;
                }

                ulong slotId;
                if (!ulong.TryParse(slotKey, out slotId) || slotId == 0UL)
                {
                    continue;
                }

                string itemId;
                if (!equipped.TryGetValue(slotKey, out itemId) || IsNullOrWhiteSpace(itemId))
                {
                    continue;
                }

                var def = new LogicItemslotDefinition();
                def.Id = slotId;
                def.AssignableItemTypes = new ulong[0];
                def.CannotBeEmpty = false;
                def.DefaultItem = string.Empty;

                var slot = new ItemSlot(def);
                slot.Item = CreateItemWithId(itemId, nextKey++);
                inventory.EquippedItems.Add(slot);
            }
        }

        private static string BuildStoryProgressKey(CareerSlot slot)
        {
            if (slot == null)
            {
                return string.Empty;
            }

            var chapter = slot.MainCampaignCurrentChapter;
            var sb = new System.Text.StringBuilder();
            sb.Append(chapter.ToString(CultureInfo.InvariantCulture));
            sb.Append('|');

            if (slot.MainCampaignInteractedNpcs != null && slot.MainCampaignInteractedNpcs.Count > 0)
            {
                var npcs = new List<string>(slot.MainCampaignInteractedNpcs);
                npcs.Sort(StringComparer.OrdinalIgnoreCase);
                sb.Append("npcs=");
                for (var i = 0; i < npcs.Count; i++)
                {
                    var npc = npcs[i];
                    if (IsNullOrWhiteSpace(npc))
                    {
                        continue;
                    }
                    sb.Append(npc);
                    sb.Append(',');
                }
                sb.Append('|');
            }

            if (slot.MainCampaignMissionStates == null || slot.MainCampaignMissionStates.Count == 0)
            {
                return sb.ToString();
            }

            var keys = new List<string>(slot.MainCampaignMissionStates.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                if (IsNullOrWhiteSpace(k))
                {
                    continue;
                }
                string v;
                if (!slot.MainCampaignMissionStates.TryGetValue(k, out v) || IsNullOrWhiteSpace(v))
                {
                    continue;
                }
                sb.Append(k);
                sb.Append('=');
                sb.Append(v);
                sb.Append(';');
            }
            return sb.ToString();
        }

        private static string BuildEquippedItemsKey(CareerSlot slot)
        {
            if (slot == null || slot.EquippedItems == null || slot.EquippedItems.Count == 0)
            {
                return string.Empty;
            }

            var keys = new List<string>(slot.EquippedItems.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                if (IsNullOrWhiteSpace(k))
                {
                    continue;
                }
                string v;
                if (!slot.EquippedItems.TryGetValue(k, out v) || v == null)
                {
                    continue;
                }
                sb.Append(k);
                sb.Append('=');
                sb.Append(v);
                sb.Append(';');
            }
            return sb.ToString();
        }

        private static bool IsValidBase64(string value)
        {
            try
            {
                Convert.FromBase64String(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}
