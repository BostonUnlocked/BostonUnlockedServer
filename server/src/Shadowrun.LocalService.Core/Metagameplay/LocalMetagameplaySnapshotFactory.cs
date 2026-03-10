using System;
using System.Collections.Generic;
using System.Globalization;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal static class LocalMetagameplaySnapshotFactory
    {
        private const string CouponGameName = "SRO";
        private const string CouponUnlocksPlayerInfoKey = "CouponUnlocks";

        public static UnlockContainer BuildUnlockContainer(LocalUserStore userStore, Guid identityGuid, CareerSlot slot)
        {
            var container = new UnlockContainer();
            var unlocks = GetAllActiveUnlocks(userStore, identityGuid, slot);
            for (var i = 0; i < unlocks.Count; i++)
            {
                var technicalName = unlocks[i];
                if (IsNullOrWhiteSpace(technicalName))
                {
                    continue;
                }

                container.Add(new Unlock
                {
                    TechnicalName = technicalName,
                    Active = true,
                });
            }

            return container;
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
            var story = new StoryProgressRuntimestate();
            if (slot == null)
            {
                return story;
            }

            var hasAnyMissionState = slot.MainCampaignMissionStates != null && slot.MainCampaignMissionStates.Count > 0;
            if (!hasAnyMissionState && slot.MainCampaignCurrentChapter <= 0)
            {
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

        private static List<string> GetAllActiveUnlocks(LocalUserStore userStore, Guid identityGuid, CareerSlot slot)
        {
            var unlocks = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var couponUnlocks = GetCouponUnlocksForIdentity(userStore, identityGuid);
            for (var i = 0; i < couponUnlocks.Count; i++)
            {
                var unlock = couponUnlocks[i];
                if (!IsNullOrWhiteSpace(unlock) && seen.Add(unlock))
                {
                    unlocks.Add(unlock);
                }
            }

            if (slot != null && slot.ActiveUnlocks != null)
            {
                for (var i = 0; i < slot.ActiveUnlocks.Count; i++)
                {
                    var unlock = slot.ActiveUnlocks[i];
                    if (!IsNullOrWhiteSpace(unlock) && seen.Add(unlock))
                    {
                        unlocks.Add(unlock);
                    }
                }
            }

            return unlocks;
        }

        private static List<string> GetCouponUnlocksForIdentity(LocalUserStore userStore, Guid identityGuid)
        {
            var unlocks = new List<string>();
            if (userStore == null || identityGuid == Guid.Empty)
            {
                return unlocks;
            }

            try
            {
                var playerInfo = userStore.GetPlayerInfo(identityGuid.ToString(), CouponGameName);
                if (playerInfo == null)
                {
                    return unlocks;
                }

                string raw;
                if (!playerInfo.TryGetValue(CouponUnlocksPlayerInfoKey, out raw) || IsNullOrWhiteSpace(raw))
                {
                    return unlocks;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var parts = raw.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length; i++)
                {
                    var trimmed = parts[i] != null ? parts[i].Trim() : null;
                    if (IsNullOrWhiteSpace(trimmed) || seen.Contains(trimmed))
                    {
                        continue;
                    }

                    seen.Add(trimmed);
                    unlocks.Add(trimmed);
                }
            }
            catch
            {
            }

            return unlocks;
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

        private static bool IsNullOrWhiteSpace(string value)
        {
            return string.IsNullOrEmpty(value) || string.IsNullOrEmpty(value.Trim());
        }
    }
}