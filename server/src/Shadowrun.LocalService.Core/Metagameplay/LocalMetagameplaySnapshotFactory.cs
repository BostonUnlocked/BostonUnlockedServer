using System;
using System.Collections.Generic;
using System.Globalization;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal static class LocalMetagameplaySnapshotFactory
    {
        public static UnlockContainer BuildUnlockContainer(LocalUserStore userStore, Guid identityGuid, CareerSlot slot)
        {
            return new EffectiveUnlockResolver(userStore, null).BuildUnlockContainer(identityGuid, slot);
        }

        public static Inventory BuildInventoryFromSlot(CareerSlot slot)
        {
            var inventory = new Inventory();
            if (slot == null || slot.ItemPossessions == null || slot.ItemPossessions.Count <= 0)
            {
                return inventory;
            }

            var preferredKeysByTuple = BuildPreferredInventoryKeysByTuple(slot);
            var usedKeys = new HashSet<int>();
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

                var inventoryKey = ConsumePreferredInventoryKey(preferredKeysByTuple, usedKeys, itemId, quality, flavour);
                if (inventoryKey < 0)
                {
                    while (usedKeys.Contains(nextKey))
                    {
                        nextKey++;
                    }
                    inventoryKey = nextKey++;
                }

                var item = new Item();
                item.InventoryKey = inventoryKey;
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

        private static Dictionary<string, Queue<int>> BuildPreferredInventoryKeysByTuple(CareerSlot slot)
        {
            var preferred = new Dictionary<string, Queue<int>>(StringComparer.OrdinalIgnoreCase);
            if (slot == null || slot.EquippedItems == null || slot.EquippedItems.Count == 0)
            {
                return preferred;
            }

            var keys = new List<string>(slot.EquippedItems.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (var i = 0; i < keys.Count; i++)
            {
                var slotKey = keys[i];
                if (IsNullOrWhiteSpace(slotKey))
                {
                    continue;
                }

                CareerSlot.EquippedSlotState state;
                if (!slot.EquippedItems.TryGetValue(slotKey, out state) || state == null || IsNullOrWhiteSpace(state.ItemId) || state.InventoryKey < 0)
                {
                    continue;
                }

                var tuple = BuildItemTupleKey(state.ItemId, state.Quality, state.Flavour);
                Queue<int> queue;
                if (!preferred.TryGetValue(tuple, out queue))
                {
                    queue = new Queue<int>();
                    preferred[tuple] = queue;
                }

                if (!queue.Contains(state.InventoryKey))
                {
                    queue.Enqueue(state.InventoryKey);
                }
            }

            return preferred;
        }

        private static int ConsumePreferredInventoryKey(Dictionary<string, Queue<int>> preferredKeysByTuple, HashSet<int> usedKeys, string itemId, int quality, int flavour)
        {
            if (preferredKeysByTuple == null || usedKeys == null || IsNullOrWhiteSpace(itemId))
            {
                return -1;
            }

            Queue<int> queue;
            if (!preferredKeysByTuple.TryGetValue(BuildItemTupleKey(itemId, quality, flavour), out queue) || queue == null)
            {
                return -1;
            }

            while (queue.Count > 0)
            {
                var key = queue.Dequeue();
                if (key >= 0 && !usedKeys.Contains(key))
                {
                    usedKeys.Add(key);
                    return key;
                }
            }

            return -1;
        }

        private static string BuildItemTupleKey(string itemId, int quality, int flavour)
        {
            return (itemId ?? string.Empty)
                + "|"
                + quality.ToString(CultureInfo.InvariantCulture)
                + "|"
                + flavour.ToString(CultureInfo.InvariantCulture);
        }

        public static StoryProgressRuntimestate BuildStoryProgress(CareerSlot slot)
        {
            return new PortedStorySnapshotBuilder(null).BuildStoryProgress(slot);
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return string.IsNullOrEmpty(value) || string.IsNullOrEmpty(value.Trim());
        }
    }
}