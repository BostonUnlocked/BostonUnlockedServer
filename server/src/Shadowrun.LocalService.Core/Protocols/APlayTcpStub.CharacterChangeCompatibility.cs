using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.Definitions;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private sealed class CharacterChangeApplicationResult
        {
            public bool Changed;
            public bool ShouldSendCorrection;
            public string IgnoredPortraitOld;
            public string IgnoredPortraitNew;
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

            var p = portraitPath;
            if (p.IndexOf("portrait_male_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                genderId = 196610UL;
            }
            else if (p.IndexOf("portrait_female_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                genderId = 196609UL;
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

        private static bool HasRelevantCharacterChange(string rawMessage)
        {
            return rawMessage != null
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
        }

        private bool TryApplyCharacterChangeCollectionToSlot(string rawMessage, CareerSlot slot, out CharacterChangeApplicationResult result)
        {
            result = null;
            if (slot == null || !HasRelevantCharacterChange(rawMessage))
            {
                return false;
            }

            var parsedChange = TryDeserializeJsonDict(rawMessage);
            var applyResult = new CharacterChangeApplicationResult();
            result = applyResult;

            if (slot.EquippedItems == null)
            {
                slot.EquippedItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var newName = parsedChange != null
                ? GetStringValue(GetDictValue(parsedChange, "NameChange"), "NewName")
                : ExtractJsonStringValue(rawMessage, "NewName");
            if (!IsNullOrWhiteSpace(newName) && !string.Equals(slot.CharacterName, newName, StringComparison.Ordinal))
            {
                slot.CharacterName = newName;
                applyResult.Changed = true;
            }

            if (!slot.IsOccupied)
            {
                slot.IsOccupied = true;
                applyResult.Changed = true;
            }

            if (parsedChange != null)
            {
                ApplyStructuredCharacterChangeCollection(slot, parsedChange, applyResult);
            }
            else
            {
                ApplyLegacyCharacterChangeCollection(slot, rawMessage, applyResult);
            }

            return applyResult.Changed || applyResult.ShouldSendCorrection;
        }

        private void ApplyStructuredCharacterChangeCollection(CareerSlot slot, IDictionary parsedChange, CharacterChangeApplicationResult result)
        {
            var skinChange = GetDictValue(parsedChange, "SkinTextureIndexChange");
            var skin = GetInt32Value(skinChange, "NewIndex", -1);
            if (skin >= 0 && slot.SkinTextureIndex != skin)
            {
                slot.SkinTextureIndex = skin;
                result.Changed = true;
            }

            var storyChange = GetDictValue(parsedChange, "BackgroundStoryChange");
            var story = GetUInt64Value(storyChange, "NewStory", 0UL);
            if (story != 0UL && slot.BackgroundStory != story)
            {
                slot.BackgroundStory = story;
                result.Changed = true;
            }

            var bodyChange = GetDictValue(parsedChange, "BodyChange");
            if (bodyChange != null)
            {
                var meta = GetUInt64Value(bodyChange, "NewMetatype", 0UL);
                var gender = GetUInt64Value(bodyChange, "NewGender", 0UL);
                if (TryApplyBodytypeChange(slot, meta, gender))
                {
                    result.Changed = true;
                }
            }

            var portraitChange = GetDictValue(parsedChange, "PortraitChange");
            var newPortrait = GetStringValue(portraitChange, "NewPortrait");
            if (!IsNullOrWhiteSpace(newPortrait) && !string.Equals(slot.PortraitPath, newPortrait, StringComparison.Ordinal))
            {
                var allowPortraitUpdate = slot.PendingPersistenceCreation || IsNullOrWhiteSpace(slot.PortraitPath);
                if (allowPortraitUpdate)
                {
                    slot.PortraitPath = newPortrait;
                    slot.Portrait = newPortrait;
                    result.Changed = true;

                    if (slot.Bodytype == 0UL && TryInferBodytypeFromPortrait(slot, newPortrait))
                    {
                        result.Changed = true;
                    }
                }
                else
                {
                    result.IgnoredPortraitOld = GetStringValue(portraitChange, "OldPortrait");
                    result.IgnoredPortraitNew = newPortrait;
                    result.ShouldSendCorrection = true;
                }
            }

            if (slot.Bodytype == 0UL && !IsNullOrWhiteSpace(slot.PortraitPath) && TryInferBodytypeFromPortrait(slot, slot.PortraitPath))
            {
                result.Changed = true;
            }

            var voiceChange = GetDictValue(parsedChange, "VoiceSetChange");
            var newVoice = GetStringValue(voiceChange, "NewVoiceSet");
            if (!IsNullOrWhiteSpace(newVoice) && !string.Equals(slot.Voiceset, newVoice, StringComparison.Ordinal))
            {
                slot.Voiceset = newVoice;
                result.Changed = true;
            }

            var wantsChange = GetDictValue(parsedChange, "WantsBackgroundChangeChange");
            var wantsNew = GetStringValue(wantsChange, "New");
            bool wantsBool;
            if (!IsNullOrWhiteSpace(wantsNew) && bool.TryParse(wantsNew, out wantsBool) && slot.WantsBackgroundChange != wantsBool)
            {
                slot.WantsBackgroundChange = wantsBool;
                result.Changed = true;
            }

            var primaryWeaponChange = GetDictValue(parsedChange, "PrimaryWeaponChange");
            if (ApplyWeaponChange(primaryWeaponChange, "NewWeapon", 0, slot.PrimaryWeaponItemId, slot.PrimaryWeaponInventoryKey, out slot.PrimaryWeaponItemId, out slot.PrimaryWeaponInventoryKey))
            {
                result.Changed = true;
            }

            var secondaryWeaponChange = GetDictValue(parsedChange, "SecondaryWeaponChange");
            if (ApplyWeaponChange(secondaryWeaponChange, "NewWeapon", 1, slot.SecondaryWeaponItemId, slot.SecondaryWeaponInventoryKey, out slot.SecondaryWeaponItemId, out slot.SecondaryWeaponInventoryKey))
            {
                result.Changed = true;
            }

            var armorChange = GetDictValue(parsedChange, "ArmorChange");
            if (armorChange != null)
            {
                var newArmor = GetDictValue(armorChange, "NewArmor") ?? GetDictValue(armorChange, "NewItem");
                var newItemId = GetStringValue(newArmor, "ItemId");
                var newInvKey = GetInt32Value(newArmor, "InventoryKey", 2);
                if (!IsNullOrWhiteSpace(newItemId)
                    && (!string.Equals(slot.ArmorItemId, newItemId, StringComparison.Ordinal)
                        || slot.ArmorInventoryKey != newInvKey))
                {
                    slot.ArmorItemId = newItemId;
                    slot.ArmorInventoryKey = newInvKey;
                    result.Changed = true;
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
                            result.Changed = true;
                        }
                    }
                    else
                    {
                        string existing;
                        if (!slot.EquippedItems.TryGetValue(key, out existing) || !string.Equals(existing, newItemId, StringComparison.Ordinal))
                        {
                            slot.EquippedItems[key] = newItemId;
                            result.Changed = true;
                        }
                    }
                }
            }
        }

        private void ApplyLegacyCharacterChangeCollection(CareerSlot slot, string rawMessage, CharacterChangeApplicationResult result)
        {
            if (rawMessage.IndexOf("SkinTextureIndexChange", StringComparison.Ordinal) >= 0)
            {
                int skin;
                if (TryParseInt32(ExtractJsonStringValue(rawMessage, "NewIndex"), out skin) && slot.SkinTextureIndex != skin)
                {
                    slot.SkinTextureIndex = skin;
                    result.Changed = true;
                }
            }
            if (rawMessage.IndexOf("BackgroundStoryChange", StringComparison.Ordinal) >= 0)
            {
                ulong story;
                if (TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewStory"), out story) && slot.BackgroundStory != story)
                {
                    slot.BackgroundStory = story;
                    result.Changed = true;
                }
            }
            if (rawMessage.IndexOf("BodyChange", StringComparison.Ordinal) >= 0)
            {
                ulong meta;
                ulong gender;
                if (TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewMetatype"), out meta)
                    && TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewGender"), out gender)
                    && TryApplyBodytypeChange(slot, meta, gender))
                {
                    result.Changed = true;
                }
            }

            if (rawMessage.IndexOf("PortraitChange", StringComparison.Ordinal) >= 0)
            {
                var newPortrait = ExtractJsonStringValue(rawMessage, "NewPortrait");
                if (!IsNullOrWhiteSpace(newPortrait) && !string.Equals(slot.PortraitPath, newPortrait, StringComparison.Ordinal))
                {
                    slot.PortraitPath = newPortrait;
                    slot.Portrait = newPortrait;
                    result.Changed = true;
                }
            }

            if (rawMessage.IndexOf("VoiceSetChange", StringComparison.Ordinal) >= 0)
            {
                var newVoice = ExtractJsonStringValue(rawMessage, "NewVoiceSet");
                if (!IsNullOrWhiteSpace(newVoice) && !string.Equals(slot.Voiceset, newVoice, StringComparison.Ordinal))
                {
                    slot.Voiceset = newVoice;
                    result.Changed = true;
                }
            }
        }

        private bool TryApplyBodytypeChange(CareerSlot slot, ulong metatypeId, ulong genderId)
        {
            if (slot == null || metatypeId == 0UL || genderId == 0UL)
            {
                return false;
            }

            ulong bodytype;
            if (!TryResolveBodytypeId(metatypeId, genderId, out bodytype) || bodytype == 0UL || slot.Bodytype == bodytype)
            {
                return false;
            }

            slot.Bodytype = bodytype;
            return true;
        }

        private bool TryInferBodytypeFromPortrait(CareerSlot slot, string portraitPath)
        {
            if (slot == null || IsNullOrWhiteSpace(portraitPath))
            {
                return false;
            }

            ulong inferredMeta;
            ulong inferredGender;
            return TryInferMetatypeAndGenderFromPortrait(portraitPath, out inferredMeta, out inferredGender)
                && TryApplyBodytypeChange(slot, inferredMeta, inferredGender);
        }

        private static bool ApplyWeaponChange(IDictionary weaponChange, string newWeaponKey, int defaultInventoryKey, string currentItemId, int currentInventoryKey, out string updatedItemId, out int updatedInventoryKey)
        {
            updatedItemId = currentItemId;
            updatedInventoryKey = currentInventoryKey;
            if (weaponChange == null)
            {
                return false;
            }

            var newWeapon = GetDictValue(weaponChange, newWeaponKey);
            var newItemId = GetStringValue(newWeapon, "ItemId");
            var newInvKey = GetInt32Value(newWeapon, "InventoryKey", defaultInventoryKey);
            if (IsNullOrWhiteSpace(newItemId)
                || (string.Equals(currentItemId, newItemId, StringComparison.Ordinal) && currentInventoryKey == newInvKey))
            {
                return false;
            }

            updatedItemId = newItemId;
            updatedInventoryKey = newInvKey;
            return true;
        }

        private static void ResetNewCareerStoryProgression(CareerSlot slot, HashSet<string> completedStoryMissions)
        {
            if (slot == null)
            {
                return;
            }

            slot.MainCampaignCurrentChapter = 0;
            slot.Nuyen = 0;
            slot.Karma = 0;
            slot.SpentKarma = 0;
            slot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            slot.MainCampaignMissionStates["1_010_Prologue"] = StoryMissionstate.Available.ToString();

            if (slot.MainCampaignInteractedNpcs != null && slot.MainCampaignInteractedNpcs.Count > 0)
            {
                slot.MainCampaignInteractedNpcs = new List<string>();
            }

            if (completedStoryMissions != null)
            {
                completedStoryMissions.Clear();
            }

            if (IsNullOrWhiteSpace(slot.HubId))
            {
                slot.HubId = DefaultHubId;
            }
        }
    }
}
