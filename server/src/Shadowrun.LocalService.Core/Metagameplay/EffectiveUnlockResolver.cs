using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class EffectiveUnlockResolver
    {
        private const string CouponGameName = "SRO";
        private const string CouponUnlocksPlayerInfoKey = "CouponUnlocks";

        private readonly LocalUserStore _userStore;
        private readonly RepeatableMissionUnlockService _repeatableUnlockService;

        public EffectiveUnlockResolver(LocalUserStore userStore, LocalServiceOptions options)
        {
            _userStore = userStore;
            _repeatableUnlockService = new RepeatableMissionUnlockService(options);
        }

        public UnlockContainer BuildUnlockContainer(Guid identityGuid, CareerSlot slot)
        {
            var container = new UnlockContainer();
            var unlocks = GetAllActiveUnlocks(identityGuid, slot);
            for (var i = 0; i < unlocks.Count; i++)
            {
                var technicalName = unlocks[i];
                if (string.IsNullOrEmpty(technicalName))
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

        public List<string> GetAllActiveUnlocks(Guid identityGuid, CareerSlot slot)
        {
            var unlocks = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var couponUnlocks = GetCouponUnlocksForIdentity(identityGuid);
            for (var i = 0; i < couponUnlocks.Count; i++)
            {
                var unlock = couponUnlocks[i];
                if (!string.IsNullOrEmpty(unlock) && seen.Add(unlock))
                {
                    unlocks.Add(unlock);
                }
            }

            if (slot != null && slot.ActiveUnlocks != null)
            {
                for (var i = 0; i < slot.ActiveUnlocks.Count; i++)
                {
                    var unlock = slot.ActiveUnlocks[i];
                    if (!string.IsNullOrEmpty(unlock) && seen.Add(unlock))
                    {
                        unlocks.Add(unlock);
                    }
                }
            }

            var virtualUnlocks = _repeatableUnlockService.GetVirtualActiveUnlocks(slot, unlocks);
            for (var i = 0; i < virtualUnlocks.Count; i++)
            {
                var unlock = virtualUnlocks[i];
                if (!string.IsNullOrEmpty(unlock) && seen.Add(unlock))
                {
                    unlocks.Add(unlock);
                }
            }

            return unlocks;
        }

        public bool HasAllActiveUnlocks(Guid identityGuid, CareerSlot slot, List<string> requiredUnlocks)
        {
            if (requiredUnlocks == null || requiredUnlocks.Count == 0)
            {
                return true;
            }

            var unlocks = new HashSet<string>(GetAllActiveUnlocks(identityGuid, slot), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < requiredUnlocks.Count; i++)
            {
                var requiredUnlock = requiredUnlocks[i];
                if (!string.IsNullOrEmpty(requiredUnlock) && !unlocks.Contains(requiredUnlock))
                {
                    return false;
                }
            }

            return true;
        }

        private List<string> GetCouponUnlocksForIdentity(Guid identityGuid)
        {
            var unlocks = new List<string>();
            if (_userStore == null || identityGuid == Guid.Empty)
            {
                return unlocks;
            }

            try
            {
                var playerInfo = _userStore.GetPlayerInfo(identityGuid.ToString(), CouponGameName);
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
                    if (IsNullOrWhiteSpace(trimmed) || !seen.Add(trimmed))
                    {
                        continue;
                    }

                    unlocks.Add(trimmed);
                }
            }
            catch
            {
            }

            return unlocks;
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return string.IsNullOrEmpty(value) || string.IsNullOrEmpty(value.Trim());
        }
    }
}