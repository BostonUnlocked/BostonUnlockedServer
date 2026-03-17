using System;
using System.Collections.Generic;
using System.Globalization;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class MissionRewardApplicationResult
    {
        public bool Persisted;
        public int KarmaBefore;
        public int KarmaAfter;
        public int NuyenBefore;
        public int NuyenAfter;
        public string[] AppliedGrantedUnlocks = new string[0];
        public string[] AppliedDeactivatedUnlocks = new string[0];
        public MissionReward TransportReward = new MissionReward
        {
            GrantedUnlocks = new string[0],
            EarnedCurrencies = new CurrencyReward[0],
            ItemChanges = new ItemChange[0],
        };

        public int AppliedItemChangeCount
        {
            get
            {
                return TransportReward != null && TransportReward.ItemChanges != null
                    ? TransportReward.ItemChanges.Length
                    : 0;
            }
        }

        public bool ShouldNotifyClient
        {
            get
            {
                if (TransportReward == null)
                {
                    return false;
                }

                return (TransportReward.GrantedUnlocks != null && TransportReward.GrantedUnlocks.Length > 0)
                    || (TransportReward.EarnedCurrencies != null && TransportReward.EarnedCurrencies.Length > 0)
                    || (TransportReward.ItemChanges != null && TransportReward.ItemChanges.Length > 0);
            }
        }
    }

    internal sealed class PortedMissionRewardService
    {
        private readonly LocalServiceOptions _options;
        private readonly RepeatableMissionUnlockService _repeatableUnlockService;

        public PortedMissionRewardService(LocalServiceOptions options)
        {
            _options = options;
            _repeatableUnlockService = new RepeatableMissionUnlockService(options);
        }

        public MissionReward ResolveMissionReward(string rewardSection, string missionName, string missionOutcome)
        {
            var reward = CreateEmptyMissionReward();
            if (string.IsNullOrEmpty(rewardSection) || string.IsNullOrEmpty(missionName) || string.IsNullOrEmpty(missionOutcome))
            {
                return reward;
            }

            var index = MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null);
            var currencies = new List<CurrencyReward>();

            int earnedValue;
            if (index.TryGetMissionCurrencyReward(rewardSection, missionName, missionOutcome, "Karma", out earnedValue) && earnedValue != 0)
            {
                currencies.Add(new CurrencyReward
                {
                    CurrencyId = CurrencyId.Karma,
                    EarnedValue = earnedValue,
                });
            }

            if (index.TryGetMissionCurrencyReward(rewardSection, missionName, missionOutcome, "Nuyen", out earnedValue) && earnedValue != 0)
            {
                currencies.Add(new CurrencyReward
                {
                    CurrencyId = CurrencyId.Nuyen,
                    EarnedValue = earnedValue,
                });
            }

            ItemChange[] itemChanges;
            if (index.TryGetMissionItemChanges(rewardSection, missionName, missionOutcome, out itemChanges) && itemChanges != null && itemChanges.Length > 0)
            {
                reward.ItemChanges = CloneItemChanges(itemChanges);
            }

            string[] grantedUnlocks;
            if (index.TryGetMissionRewardUnlocks(rewardSection, missionName, missionOutcome, out grantedUnlocks) && grantedUnlocks != null && grantedUnlocks.Length > 0)
            {
                reward.GrantedUnlocks = CloneStrings(grantedUnlocks);
            }

            reward.EarnedCurrencies = currencies.ToArray();
            return reward;
        }

        public string[] ResolveUnlockDeactivationsOnVictory(string missionName)
        {
            if (string.IsNullOrEmpty(missionName))
            {
                return new string[0];
            }

            var index = MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null);
            string[] unlocks;
            if (index.TryGetMissionUnlockDeactivationsOnVictory(missionName, out unlocks) && unlocks != null && unlocks.Length > 0)
            {
                return CloneStrings(unlocks);
            }

            return new string[0];
        }

        public MissionReward MergeRewardItemChanges(MissionReward reward, IEnumerable<ItemChange> additionalItemChanges)
        {
            var merged = CloneMissionReward(reward);
            if (additionalItemChanges == null)
            {
                return merged;
            }

            var combined = new List<ItemChange>();
            if (merged.ItemChanges != null && merged.ItemChanges.Length > 0)
            {
                combined.AddRange(CloneItemChanges(merged.ItemChanges));
            }

            foreach (var change in additionalItemChanges)
            {
                var cloned = CloneItemChange(change);
                if (cloned != null)
                {
                    combined.Add(cloned);
                }
            }

            merged.ItemChanges = combined.ToArray();
            return merged;
        }

        public MissionRewardApplicationResult Apply(CareerSlot slot, MissionReward missionReward, string[] deactivatedUnlocks)
        {
            var result = new MissionRewardApplicationResult();
            if (slot == null)
            {
                return result;
            }

            if (missionReward == null)
            {
                missionReward = CreateEmptyMissionReward();
            }

            result.KarmaBefore = slot.Karma;
            result.KarmaAfter = slot.Karma;
            result.NuyenBefore = slot.Nuyen;
            result.NuyenAfter = slot.Nuyen;

            result.AppliedGrantedUnlocks = AddActiveUnlocks(slot, missionReward.GrantedUnlocks);
            var removedUnlocks = RemoveActiveUnlocks(slot, deactivatedUnlocks);
            var advancedSequence = _repeatableUnlockService.AdvanceSequencesForConsumedUnlocks(slot, deactivatedUnlocks);
            result.AppliedDeactivatedUnlocks = MergeUniqueStrings(removedUnlocks, deactivatedUnlocks);

            var appliedCurrencies = new List<CurrencyReward>();
            var earnedCurrencies = missionReward.EarnedCurrencies ?? new CurrencyReward[0];
            for (var i = 0; i < earnedCurrencies.Length; i++)
            {
                var currency = earnedCurrencies[i];
                if (currency == null || currency.EarnedValue == 0)
                {
                    continue;
                }

                if (currency.CurrencyId == CurrencyId.Karma)
                {
                    slot.Karma = AddInt32Saturating(slot.Karma, currency.EarnedValue);
                    appliedCurrencies.Add(new CurrencyReward
                    {
                        CurrencyId = CurrencyId.Karma,
                        EarnedValue = currency.EarnedValue,
                    });
                    continue;
                }

                if (currency.CurrencyId == CurrencyId.Nuyen)
                {
                    slot.Nuyen = AddInt32Saturating(slot.Nuyen, currency.EarnedValue);
                    appliedCurrencies.Add(new CurrencyReward
                    {
                        CurrencyId = CurrencyId.Nuyen,
                        EarnedValue = currency.EarnedValue,
                    });
                }
            }

            var appliedItemChanges = ApplyItemChanges(slot, missionReward.ItemChanges);

            result.KarmaAfter = slot.Karma;
            result.NuyenAfter = slot.Nuyen;
            result.TransportReward = new MissionReward
            {
                GrantedUnlocks = result.AppliedGrantedUnlocks,
                EarnedCurrencies = appliedCurrencies.ToArray(),
                ItemChanges = appliedItemChanges.ToArray(),
            };
            result.Persisted = result.ShouldNotifyClient || result.AppliedDeactivatedUnlocks.Length > 0 || advancedSequence;
            return result;
        }

        private static string[] MergeUniqueStrings(string[] first, string[] second)
        {
            var merged = new List<string>();
            AddUniqueStrings(merged, first);
            AddUniqueStrings(merged, second);
            return merged.ToArray();
        }

        private static void AddUniqueStrings(List<string> target, string[] values)
        {
            if (target == null || values == null || values.Length == 0)
            {
                return;
            }

            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (!string.IsNullOrEmpty(value) && !target.Contains(value))
                {
                    target.Add(value);
                }
            }
        }

        private static List<ItemChange> ApplyItemChanges(CareerSlot slot, ItemChange[] itemChanges)
        {
            var applied = new List<ItemChange>();
            if (slot == null || itemChanges == null || itemChanges.Length == 0)
            {
                return applied;
            }

            if (slot.ItemPossessions == null)
            {
                slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            for (var i = 0; i < itemChanges.Length; i++)
            {
                var change = CloneItemChange(itemChanges[i]);
                if (change == null)
                {
                    continue;
                }

                var packedKey = BuildPossessionKey(change.ItemDefintionId, change.Quality, change.Flavour);
                int existing;
                if (!slot.ItemPossessions.TryGetValue(packedKey, out existing))
                {
                    existing = 0;
                }

                var next = existing + change.Delta;
                if (next <= 0)
                {
                    if (slot.ItemPossessions.ContainsKey(packedKey))
                    {
                        slot.ItemPossessions.Remove(packedKey);
                    }
                }
                else
                {
                    slot.ItemPossessions[packedKey] = next;
                }

                applied.Add(change);
            }

            return applied;
        }

        private static string[] AddActiveUnlocks(CareerSlot slot, string[] unlocks)
        {
            if (slot == null || unlocks == null || unlocks.Length == 0)
            {
                return new string[0];
            }

            if (slot.ActiveUnlocks == null)
            {
                slot.ActiveUnlocks = new List<string>();
            }

            var added = new List<string>();
            for (var i = 0; i < unlocks.Length; i++)
            {
                var unlock = unlocks[i];
                if (string.IsNullOrEmpty(unlock) || slot.ActiveUnlocks.Contains(unlock))
                {
                    continue;
                }

                slot.ActiveUnlocks.Add(unlock);
                added.Add(unlock);
            }

            return added.ToArray();
        }

        private static string[] RemoveActiveUnlocks(CareerSlot slot, string[] unlocks)
        {
            if (slot == null || slot.ActiveUnlocks == null || slot.ActiveUnlocks.Count == 0 || unlocks == null || unlocks.Length == 0)
            {
                return new string[0];
            }

            var removed = new List<string>();
            for (var i = 0; i < unlocks.Length; i++)
            {
                var unlock = unlocks[i];
                if (string.IsNullOrEmpty(unlock))
                {
                    continue;
                }

                if (slot.ActiveUnlocks.Remove(unlock))
                {
                    removed.Add(unlock);
                }
            }

            return removed.ToArray();
        }

        private static int AddInt32Saturating(int current, int delta)
        {
            var next = (long)current + (long)delta;
            if (next > int.MaxValue)
            {
                return int.MaxValue;
            }
            if (next < int.MinValue)
            {
                return int.MinValue;
            }
            return (int)next;
        }

        private static MissionReward CloneMissionReward(MissionReward reward)
        {
            if (reward == null)
            {
                return CreateEmptyMissionReward();
            }

            return new MissionReward
            {
                GrantedUnlocks = CloneStrings(reward.GrantedUnlocks),
                EarnedCurrencies = CloneCurrencies(reward.EarnedCurrencies),
                ItemChanges = CloneItemChanges(reward.ItemChanges),
            };
        }

        private static MissionReward CreateEmptyMissionReward()
        {
            return new MissionReward
            {
                GrantedUnlocks = new string[0],
                EarnedCurrencies = new CurrencyReward[0],
                ItemChanges = new ItemChange[0],
            };
        }

        private static string[] CloneStrings(string[] values)
        {
            if (values == null || values.Length == 0)
            {
                return new string[0];
            }

            var clone = new string[values.Length];
            Array.Copy(values, clone, values.Length);
            return clone;
        }

        private static CurrencyReward[] CloneCurrencies(CurrencyReward[] values)
        {
            if (values == null || values.Length == 0)
            {
                return new CurrencyReward[0];
            }

            var clone = new CurrencyReward[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (value == null)
                {
                    continue;
                }

                clone[i] = new CurrencyReward
                {
                    CurrencyId = value.CurrencyId,
                    EarnedValue = value.EarnedValue,
                };
            }

            return clone;
        }

        private static ItemChange[] CloneItemChanges(ItemChange[] values)
        {
            if (values == null || values.Length == 0)
            {
                return new ItemChange[0];
            }

            var clone = new List<ItemChange>();
            for (var i = 0; i < values.Length; i++)
            {
                var itemChange = CloneItemChange(values[i]);
                if (itemChange != null)
                {
                    clone.Add(itemChange);
                }
            }

            return clone.ToArray();
        }

        private static ItemChange CloneItemChange(ItemChange value)
        {
            if (value == null || string.IsNullOrEmpty(value.ItemDefintionId) || value.Delta == 0)
            {
                return null;
            }

            try
            {
                return new ItemChange(value.ItemDefintionId, value.Delta)
                {
                    Quality = value.Quality,
                    Flavour = value.Flavour,
                };
            }
            catch
            {
                return null;
            }
        }

        private static string BuildPossessionKey(string itemId, int quality, int flavour)
        {
            return (itemId ?? string.Empty)
                + "|"
                + quality.ToString(CultureInfo.InvariantCulture)
                + "|"
                + flavour.ToString(CultureInfo.InvariantCulture);
        }
    }
}
