using System;
using System.Collections.Generic;
using System.Globalization;
using Cliffhanger.SRO.ServerClientCommons.Definitions;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Career
{
    internal static class PlayerCharacterSnapshotFactory
    {
        public static PlayerCharacterSnapshot Create(string characterIdentifier, string characterName, CareerSlot slot)
        {
#pragma warning disable 618
            var snapshot = new PlayerCharacterSnapshot();
#pragma warning restore 618
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
                snapshot.Wallet.Reset(CurrencyId.Karma, slot != null ? slot.Karma : 0, slot != null ? slot.SpentKarma : 0);
                snapshot.Wallet.Reset(CurrencyId.Nuyen, slot != null ? slot.Nuyen : 0, 0);
            }

            if (snapshot.SkillTreeDefinitions == null)
            {
                snapshot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
            }
            if (slot != null && slot.SkillTreeDefinitions != null)
            {
                foreach (var kvp in slot.SkillTreeDefinitions)
                {
                    if (!IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                    {
                        snapshot.SkillTreeDefinitions[kvp.Key] = kvp.Value;
                    }
                }
            }

            var inventory = new PlayerCharacterInventory();
            inventory.PrimaryWeapon = CreateItem(
                slot != null && !IsNullOrWhiteSpace(slot.PrimaryWeaponItemId) ? slot.PrimaryWeaponItemId : PlayerCharacterDefaultValues.PrimaryWeapon,
                slot != null ? slot.PrimaryWeaponInventoryKey : 0, slot != null ? slot.PrimaryWeaponQuality : 0, slot != null ? slot.PrimaryWeaponFlavour : -1);
            inventory.SecondaryWeapon = CreateItem(
                slot != null && !IsNullOrWhiteSpace(slot.SecondaryWeaponItemId) ? slot.SecondaryWeaponItemId : PlayerCharacterDefaultValues.SecondaryWeapon,
                slot != null ? slot.SecondaryWeaponInventoryKey : 1, slot != null ? slot.SecondaryWeaponQuality : 0, slot != null ? slot.SecondaryWeaponFlavour : -1);
            inventory.Armor = CreateItem(
                slot != null && !IsNullOrWhiteSpace(slot.ArmorItemId) ? slot.ArmorItemId : PlayerCharacterDefaultValues.Armor,
                slot != null ? slot.ArmorInventoryKey : 2, slot != null ? slot.ArmorQuality : 0, slot != null ? slot.ArmorFlavour : -1);
            if (slot != null && slot.EquippedItems != null && slot.EquippedItems.Count > 0)
            {
                var keys = new List<string>(slot.EquippedItems.Keys);
                keys.Sort(StringComparer.Ordinal);
                var nextKey = 10;
                var usedInventoryKeys = new HashSet<int>();
                foreach (var slotKey in keys)
                {
                    ulong slotId;
                    if (IsNullOrWhiteSpace(slotKey)
                        || !ulong.TryParse(slotKey.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out slotId)
                        || slotId == 0UL)
                    {
                        continue;
                    }

                    CareerSlot.EquippedSlotState state;
                    if (!slot.EquippedItems.TryGetValue(slotKey, out state) || state == null || IsNullOrWhiteSpace(state.ItemId))
                    {
                        continue;
                    }

                    var definition = new LogicItemslotDefinition();
                    definition.Id = slotId;
                    definition.AssignableItemTypes = new ulong[0];
                    definition.CannotBeEmpty = false;
                    definition.DefaultItem = string.Empty;
                    var itemSlot = new ItemSlot(definition);
                    var inventoryKey = state.InventoryKey >= 0 ? state.InventoryKey : nextKey;
                    if (inventoryKey < 0 || usedInventoryKeys.Contains(inventoryKey))
                    {
                        while (usedInventoryKeys.Contains(nextKey)) nextKey++;
                        inventoryKey = nextKey++;
                    }

                    usedInventoryKeys.Add(inventoryKey);
                    itemSlot.Item = CreateItem(state.ItemId, inventoryKey, state.Quality, state.Flavour);
                    inventory.EquippedItems.Add(itemSlot);
                    if (inventoryKey >= nextKey) nextKey = inventoryKey + 1;
                }
            }
            snapshot.PlayerCharacterInventory = inventory;
            return snapshot;
        }

        private static Item CreateItem(string itemId, int inventoryKey, int quality, int flavour)
        {
            return new Item
            {
                ItemId = itemId ?? string.Empty,
                InventoryKey = inventoryKey,
                Amount = 1,
                Quality = quality,
                FlavourIndex = flavour,
            };
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}
