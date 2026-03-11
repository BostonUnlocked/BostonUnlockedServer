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