using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.Definitions;
using Cliffhanger.SRO.ServerClientCommons.Matchmaking;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Career
{
    public sealed class MatchConfigurationGenerator
    {
        private const int DefaultTeamCount = 16;

        private readonly RequestLogger _logger;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.Ordinal);

        public MatchConfigurationGenerator(RequestLogger logger)
        {
            _logger = logger;
        }

        public string GetCompressedMatchConfiguration(string mapName, Guid identityGuid, int careerIndex, string characterName)
        {
            if (IsNullOrWhiteSpace(mapName))
            {
                mapName = "1_010_Prologue";
            }

            if (identityGuid == Guid.Empty)
            {
                identityGuid = Guid.NewGuid();
            }
            if (careerIndex < 0)
            {
                careerIndex = 0;
            }
            if (IsNullOrWhiteSpace(characterName))
            {
                characterName = "OfflineRunner";
            }

            lock (_cacheLock)
            {
                var cacheKey = mapName + "|" + identityGuid.ToString() + "|" + careerIndex.ToString() + "|" + characterName;
                string cached;
                if (_cache.TryGetValue(cacheKey, out cached) && !IsNullOrWhiteSpace(cached))
                {
                    return cached;
                }

                var generated = Generate(mapName, identityGuid, careerIndex, characterName);
                if (!IsNullOrWhiteSpace(generated))
                {
                    _cache[cacheKey] = generated;
                    return generated;
                }

                _cache[cacheKey] = string.Empty;
                return string.Empty;
            }
        }

        public string GetCompressedMatchConfiguration(string mapName, Guid identityGuid, int careerIndex, string characterName, PlayerCharacterSnapshot[] selectedHenchmen)
        {
            // Selections can change frequently (team selection UI); keep this uncached to avoid stale rosters.
            return Generate(mapName, identityGuid, careerIndex, characterName, null, selectedHenchmen);
        }

        public string GetCompressedMatchConfiguration(string mapName, Guid identityGuid, int careerIndex, CareerSlot slot)
        {
            var name = slot != null ? slot.CharacterName : null;
            if (IsNullOrWhiteSpace(name))
            {
                name = "OfflineRunner";
            }

            if (IsNullOrWhiteSpace(mapName))
            {
                mapName = "1_010_Prologue";
            }

            if (identityGuid == Guid.Empty)
            {
                identityGuid = Guid.NewGuid();
            }

            if (careerIndex < 0)
            {
                careerIndex = 0;
            }

            var bodytype = slot != null ? slot.Bodytype : 0UL;
            var skin = slot != null ? slot.SkinTextureIndex : 0;
            var story = slot != null ? slot.BackgroundStory : 0UL;
            var portraitPath = slot != null ? (slot.PortraitPath ?? string.Empty) : string.Empty;
            var voiceset = slot != null ? (slot.Voiceset ?? string.Empty) : string.Empty;
            var wants = slot != null && slot.WantsBackgroundChange;
            var equippedKey = BuildEquippedItemsKey(slot);
            var karma = slot != null ? slot.Karma : 0;
            var nuyen = slot != null ? slot.Nuyen : 0;

            lock (_cacheLock)
            {
                var cacheKey = mapName + "|" + identityGuid.ToString() + "|" + careerIndex.ToString() + "|" + name + "|" + bodytype.ToString() + "|" + skin.ToString() + "|" + story.ToString() + "|" + portraitPath + "|" + voiceset + "|" + (wants ? "1" : "0") + "|" + karma.ToString() + "|" + nuyen.ToString() + "|" + equippedKey;
                string cached;
                if (_cache.TryGetValue(cacheKey, out cached) && !IsNullOrWhiteSpace(cached))
                {
                    return cached;
                }

                var generated = Generate(mapName, identityGuid, careerIndex, name, slot);
                if (!IsNullOrWhiteSpace(generated))
                {
                    _cache[cacheKey] = generated;
                    return generated;
                }

                _cache[cacheKey] = string.Empty;
                return string.Empty;
            }
        }

        public string GetCompressedMatchConfiguration(string mapName, Guid identityGuid, int careerIndex, CareerSlot slot, PlayerCharacterSnapshot[] selectedHenchmen)
        {
            // Selections can change frequently (team selection UI); keep this uncached to avoid stale rosters.
            return Generate(mapName, identityGuid, careerIndex, slot != null ? slot.CharacterName : null, slot, selectedHenchmen);
        }

        private string Generate(string mapName, Guid identityGuid, int careerIndex, string characterName)
        {
            return Generate(mapName, identityGuid, careerIndex, characterName, null, null);
        }

        private string Generate(string mapName, Guid identityGuid, int careerIndex, string characterName, CareerSlot slot)
        {
            return Generate(mapName, identityGuid, careerIndex, characterName, slot, null);
        }

        private string Generate(string mapName, Guid identityGuid, int careerIndex, string characterName, CareerSlot slot, PlayerCharacterSnapshot[] selectedHenchmen)
        {
            try
            {
                var matchConfig = new MatchConfiguration();

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

#pragma warning disable 618 // PlayerCharacterSnapshot() is obsolete; recommended factory is in unavailable server-side DLLs.
                var pcs = new PlayerCharacterSnapshot();
#pragma warning restore 618
                pcs.CharacterName = characterName;
                pcs.CharacterIdentifier = identityGuid.ToString() + ":" + careerIndex.ToString();
                // Keep consistent with static-data/globals.json Version=48.
                pcs.DataVersion = 48;
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

                if (slot != null && slot.EquippedItems != null && slot.EquippedItems.Count > 0)
                {
                    ApplyEquippedItems(pcInv, slot.EquippedItems);
                }

                var humanPlayer = new HumanPlayer();
                humanPlayer.AccountHash = identityGuid.ToString();
                humanPlayer.ID = 1UL;
                humanPlayer.PlayerCharacterSnapshot = pcs;
                humanPlayer.Henchmen = selectedHenchmen ?? new PlayerCharacterSnapshot[0];

                var teams = new List<Team>();
                for (var i = 0; i < DefaultTeamCount; i++)
                {
                    if (i == 0)
                    {
                        var team = new Team(humanPlayer);
                        team.ID = i;
                        teams.Add(team);
                    }
                    else
                    {
                        var ai = new AIControlledPlayer();
                        ai.ID = (ulong)(i + 1);
                        var team = new Team(ai);
                        team.ID = i;
                        teams.Add(team);
                    }
                }

                matchConfig.Mapname = mapName;
                matchConfig.Teams = teams;

                var blob = MissionSerializer.SerializeMatchConfiguration(matchConfig);

                if (IsNullOrWhiteSpace(blob))
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "match-config-generate",
                        status = "failed",
                        reason = "serializer-returned-empty",
                        mapName = mapName,
                    });
                    return null;
                }

                Convert.FromBase64String(blob);

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "match-config-generate",
                    status = "ok",
                    blobLength = blob.Length,
                    teamsCount = DefaultTeamCount,
                    mapName = mapName,
                    skillTrees = pcs != null && pcs.SkillTreeDefinitions != null ? pcs.SkillTreeDefinitions.Count : 0,
                    managedDir = "Dependencies",
                });
                return blob;
            }
            catch (Exception ex)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "match-config-generate",
                    status = "failed",
                    reason = "reflection-exception",
                    mapName = mapName,
                    message = ex.Message,
                });
                return null;
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

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }

    }
}
