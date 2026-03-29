using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class ShopTransactionApplicationResult
    {
        public bool Persisted;
        public int NuyenBefore;
        public int NuyenAfter;
        public ShopItemChanges ShopChanges;
        public List<object> SellGuardChecks;
    }

    internal sealed class PortedShopInventoryService
    {
        private readonly LocalServiceOptions _options;
        private readonly EffectiveUnlockResolver _unlockResolver;

        public PortedShopInventoryService(LocalServiceOptions options, LocalUserStore userStore)
        {
            _options = options;
            _unlockResolver = new EffectiveUnlockResolver(userStore, options);
        }

        public ShopTransactionApplicationResult Apply(Guid identityGuid, CareerSlot slot, ItemPossessionChanges requestedChanges)
        {
            var result = new ShopTransactionApplicationResult();
            result.ShopChanges = new ShopItemChanges();
            result.SellGuardChecks = new List<object>();

            if (slot == null || requestedChanges == null)
            {
                result.ShopChanges.Failed = true;
                return result;
            }

            if (slot.ItemPossessions == null)
            {
                slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            result.NuyenBefore = slot.Nuyen;
            result.NuyenAfter = slot.Nuyen;

            var index = MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null);
            var storyProgression = new PortedStoryProgressionService(_options);
            var effectiveUnlocks = _unlockResolver.GetAllActiveUnlocks(identityGuid, slot);
            var availabilityContext = MetagameplayAvailabilityContext.Create(slot, storyProgression.GetVirtualChapterIndex(identityGuid, slot, "Main Campaign"), effectiveUnlocks);
            var tempInventory = new Dictionary<string, int>(slot.ItemPossessions, StringComparer.OrdinalIgnoreCase);
            var applied = new List<ItemChange>();
            var allRequested = requestedChanges.ItemChanges ?? new ItemChange[0];
            var totalNuyenChange = 0;

            if (string.IsNullOrEmpty(requestedChanges.ShopKeeper) || !index.ShopEntries.ContainsKey(requestedChanges.ShopKeeper))
            {
                result.ShopChanges = new ShopItemChanges
                {
                    Failed = true,
                    AppliedChanges = new ItemChange[0],
                    NotAppliedChanges = allRequested,
                    TotalNuyenChange = 0,
                };
                return result;
            }

            for (var i = 0; i < allRequested.Length; i++)
            {
                var change = allRequested[i];
                if (change == null || change == ItemChange.Empty)
                {
                    result.ShopChanges = Fail(allRequested);
                    return result;
                }

                if (change.Delta == 1)
                {
                    if (!TryApplyBuy(index, requestedChanges.ShopKeeper, availabilityContext, tempInventory, change, applied, ref totalNuyenChange))
                    {
                        result.ShopChanges = Fail(allRequested);
                        return result;
                    }
                }
                else if (change.Delta == -1)
                {
                    if (!TryApplySell(index, slot, tempInventory, change, applied, ref totalNuyenChange, result.SellGuardChecks))
                    {
                        result.ShopChanges = Fail(allRequested);
                        return result;
                    }
                }
                else
                {
                    result.ShopChanges = Fail(allRequested);
                    return result;
                }
            }

            var nextNuyen = slot.Nuyen + totalNuyenChange;
            if (nextNuyen < 0)
            {
                result.ShopChanges = Fail(allRequested);
                return result;
            }

            slot.ItemPossessions = tempInventory;
            slot.Nuyen = nextNuyen;
            result.NuyenAfter = nextNuyen;
            result.Persisted = true;
            result.ShopChanges = new ShopItemChanges
            {
                Failed = false,
                AppliedChanges = applied.ToArray(),
                NotAppliedChanges = new ItemChange[0],
                TotalNuyenChange = totalNuyenChange,
            };
            return result;
        }

        private static ShopItemChanges Fail(ItemChange[] requestedChanges)
        {
            return new ShopItemChanges
            {
                Failed = true,
                AppliedChanges = new ItemChange[0],
                NotAppliedChanges = requestedChanges ?? new ItemChange[0],
                TotalNuyenChange = 0,
            };
        }

        private static bool TryApplyBuy(MetagameplayStaticDataIndex index, string shopKeeper, MetagameplayAvailabilityContext availabilityContext, Dictionary<string, int> tempInventory, ItemChange requestedChange, List<ItemChange> applied, ref int totalNuyenChange)
        {
            MetagameplayStaticDataIndex.ItemDefinitionInfo itemDefinition;
            if (!index.TryGetItemDefinition(requestedChange.ItemDefintionId, out itemDefinition) || itemDefinition == null)
            {
                return false;
            }

            MetagameplayStaticDataIndex.ShopEntryInfo shopEntry;
            if (!index.TryGetShopEntry(shopKeeper, requestedChange.ItemDefintionId, out shopEntry) || shopEntry == null)
            {
                return false;
            }

            if (!MetagameplayAvailabilityEvaluator.IsFulfilled(shopEntry.Condition, availabilityContext))
            {
                return false;
            }

            var possessionKey = BuildPossessionKey(requestedChange.ItemDefintionId, requestedChange.Quality, requestedChange.Flavour);
            int existingAmount;
            if (!tempInventory.TryGetValue(possessionKey, out existingAmount) || existingAmount < 0)
            {
                existingAmount = 0;
            }

            if (itemDefinition.MaxStacksize <= 1 && existingAmount > 0)
            {
                return false;
            }

            if (existingAmount >= itemDefinition.MaxStacksize)
            {
                return false;
            }

            tempInventory[possessionKey] = existingAmount + 1;
            applied.Add(CloneItemChange(requestedChange));
            totalNuyenChange -= shopEntry.Price;
            return true;
        }

        private static bool TryApplySell(MetagameplayStaticDataIndex index, CareerSlot slot, Dictionary<string, int> tempInventory, ItemChange requestedChange, List<ItemChange> applied, ref int totalNuyenChange, List<object> sellGuardChecks)
        {
            var possessionKey = BuildPossessionKey(requestedChange.ItemDefintionId, requestedChange.Quality, requestedChange.Flavour);
            int existingAmount;
            if (!tempInventory.TryGetValue(possessionKey, out existingAmount) || existingAmount <= 0)
            {
                return false;
            }

            int richCount;
            int primaryCount;
            int secondaryCount;
            int armorCount;
            var equippedCount = CountEquippedMatchingItem(slot, requestedChange, out richCount, out primaryCount, out secondaryCount, out armorCount);
            var blockedByEquipped = existingAmount - 1 < equippedCount;

            if (sellGuardChecks != null)
            {
                sellGuardChecks.Add(new
                {
                    itemId = requestedChange.ItemDefintionId,
                    quality = requestedChange.Quality,
                    flavour = requestedChange.Flavour,
                    possessionKey = possessionKey,
                    existingAmount = existingAmount,
                    equippedCount = equippedCount,
                    richEquippedCount = richCount,
                    primarySlotCount = primaryCount,
                    secondarySlotCount = secondaryCount,
                    armorSlotCount = armorCount,
                    blockedByEquippedRule = blockedByEquipped,
                    primaryWeaponItemId = slot != null ? slot.PrimaryWeaponItemId : null,
                    primaryWeaponInventoryKey = slot != null ? slot.PrimaryWeaponInventoryKey : -1,
                    secondaryWeaponItemId = slot != null ? slot.SecondaryWeaponItemId : null,
                    secondaryWeaponInventoryKey = slot != null ? slot.SecondaryWeaponInventoryKey : -1,
                    armorItemId = slot != null ? slot.ArmorItemId : null,
                    armorInventoryKey = slot != null ? slot.ArmorInventoryKey : -1,
                    richEquippedEntries = slot != null && slot.EquippedItems != null ? slot.EquippedItems.Count : 0,
                });
            }

            if (blockedByEquipped)
            {
                return false;
            }

            if (existingAmount == 1)
            {
                tempInventory.Remove(possessionKey);
            }
            else
            {
                tempInventory[possessionKey] = existingAmount - 1;
            }

            MetagameplayStaticDataIndex.ItemDefinitionInfo itemDefinition;
            if (!index.TryGetItemDefinition(requestedChange.ItemDefintionId, out itemDefinition) || itemDefinition == null)
            {
                return false;
            }

            applied.Add(CloneItemChange(requestedChange));
            totalNuyenChange += itemDefinition.SellPrice;
            return true;
        }

        private static int CountEquippedMatchingItem(CareerSlot slot, ItemChange requestedChange, out int richCount, out int primaryCount, out int secondaryCount, out int armorCount)
        {
            richCount = 0;
            primaryCount = 0;
            secondaryCount = 0;
            armorCount = 0;

            if (slot == null || requestedChange == null || string.IsNullOrEmpty(requestedChange.ItemDefintionId))
            {
                return 0;
            }

            var itemId = requestedChange.ItemDefintionId;

            if (slot.EquippedItems != null && slot.EquippedItems.Count > 0)
            {
                foreach (var equipped in slot.EquippedItems.Values)
                {
                    if (equipped == null || !string.Equals(equipped.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsEquippedTupleMatch(equipped, requestedChange))
                    {
                        richCount++;
                    }
                }
            }

            if (string.Equals(slot.PrimaryWeaponItemId, itemId, StringComparison.OrdinalIgnoreCase)
                && !HasRichEquippedIdentity(slot.EquippedItems, itemId, slot.PrimaryWeaponInventoryKey))
            {
                primaryCount = 1;
            }

            if (string.Equals(slot.SecondaryWeaponItemId, itemId, StringComparison.OrdinalIgnoreCase)
                && !HasRichEquippedIdentity(slot.EquippedItems, itemId, slot.SecondaryWeaponInventoryKey))
            {
                secondaryCount = 1;
            }

            if (string.Equals(slot.ArmorItemId, itemId, StringComparison.OrdinalIgnoreCase)
                && !HasRichEquippedIdentity(slot.EquippedItems, itemId, slot.ArmorInventoryKey))
            {
                armorCount = 1;
            }

            return richCount + primaryCount + secondaryCount + armorCount;
        }

        private static bool HasRichEquippedIdentity(Dictionary<string, CareerSlot.EquippedSlotState> equippedItems, string itemId, int inventoryKey)
        {
            if (equippedItems == null || equippedItems.Count == 0 || string.IsNullOrEmpty(itemId))
            {
                return false;
            }

            foreach (var equipped in equippedItems.Values)
            {
                if (equipped == null)
                {
                    continue;
                }

                if (!string.Equals(equipped.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (equipped.InventoryKey == inventoryKey)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEquippedTupleMatch(CareerSlot.EquippedSlotState equipped, ItemChange requestedChange)
        {
            if (equipped == null || requestedChange == null)
            {
                return false;
            }

            if (equipped.Quality == requestedChange.Quality && equipped.Flavour == requestedChange.Flavour)
            {
                return true;
            }

            // Legacy equipped entries were stored without tuple identity.
            // Treat them as unknown variants of the equipped item and block all sells for that item id.
            if (equipped.InventoryKey < 0 && equipped.Quality == 0 && equipped.Flavour == -1)
            {
                return true;
            }

            return false;
        }

        private static ItemChange CloneItemChange(ItemChange requestedChange)
        {
            return new ItemChange(requestedChange.ItemDefintionId, requestedChange.Delta)
            {
                Quality = requestedChange.Quality,
                Flavour = requestedChange.Flavour,
            };
        }

        private static string BuildPossessionKey(string itemId, int quality, int flavour)
        {
            return (itemId ?? string.Empty)
                + "|"
                + quality.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "|"
                + flavour.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
