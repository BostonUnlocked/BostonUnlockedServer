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
    }

    internal sealed class PortedShopInventoryService
    {
        private readonly LocalServiceOptions _options;

        public PortedShopInventoryService(LocalServiceOptions options)
        {
            _options = options;
        }

        public ShopTransactionApplicationResult Apply(CareerSlot slot, ItemPossessionChanges requestedChanges)
        {
            var result = new ShopTransactionApplicationResult();
            result.ShopChanges = new ShopItemChanges();

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
                    if (!TryApplyBuy(index, requestedChanges.ShopKeeper, slot, tempInventory, change, applied, ref totalNuyenChange))
                    {
                        result.ShopChanges = Fail(allRequested);
                        return result;
                    }
                }
                else if (change.Delta == -1)
                {
                    if (!TryApplySell(index, tempInventory, change, applied, ref totalNuyenChange))
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

        private static bool TryApplyBuy(MetagameplayStaticDataIndex index, string shopKeeper, CareerSlot slot, Dictionary<string, int> tempInventory, ItemChange requestedChange, List<ItemChange> applied, ref int totalNuyenChange)
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

            if (!MetagameplayAvailabilityEvaluator.IsFulfilled(shopEntry.Condition, MetagameplayAvailabilityContext.Create(slot)))
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

        private static bool TryApplySell(MetagameplayStaticDataIndex index, Dictionary<string, int> tempInventory, ItemChange requestedChange, List<ItemChange> applied, ref int totalNuyenChange)
        {
            var possessionKey = BuildPossessionKey(requestedChange.ItemDefintionId, requestedChange.Quality, requestedChange.Flavour);
            int existingAmount;
            if (!tempInventory.TryGetValue(possessionKey, out existingAmount) || existingAmount <= 0)
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
