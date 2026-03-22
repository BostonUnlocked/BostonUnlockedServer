using System;
using System.Collections.Generic;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class RepeatableMissionUnlockService
    {
        private const string SequenceRMission12 = "RMission1|RMission2";
        private const string SequenceRMission34 = "RMission3|RMission4";
        private const string SequenceERMission21 = "ERMission2|ERMission1";
        private const string SequenceERMission43 = "ERMission4|ERMission3";

        private readonly LocalServiceOptions _options;

        public RepeatableMissionUnlockService(LocalServiceOptions options)
        {
            _options = options;
        }

        public List<string> GetVirtualActiveUnlocks(CareerSlot slot, ICollection<string> effectiveUnlocks)
        {
            var virtualUnlocks = new List<string>();
            if (slot == null)
            {
                return virtualUnlocks;
            }

            var sequences = MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null).RepeatableUnlockSequences;
            if (sequences == null || sequences.Count == 0)
            {
                return virtualUnlocks;
            }

            EnsureSequenceState(slot);
            ApplyDailyRepeatableSequencePositions(slot, sequences);

            for (var i = 0; i < sequences.Count; i++)
            {
                var sequence = sequences[i];
                if (sequence == null || sequence.Unlocks == null || sequence.Unlocks.Count == 0 || !ShouldActivateSequence(sequence, effectiveUnlocks))
                {
                    continue;
                }

                if (FindActiveUnlockIndex(slot.ActiveUnlocks, sequence) >= 0)
                {
                    continue;
                }

                var nextIndex = 0;
                if (slot.RepeatableUnlockSequencePositions != null)
                {
                    slot.RepeatableUnlockSequencePositions.TryGetValue(sequence.Key, out nextIndex);
                }
                if (nextIndex < 0 || nextIndex >= sequence.Unlocks.Count)
                {
                    nextIndex = 0;
                }

                var nextUnlock = sequence.Unlocks[nextIndex];
                if (!IsNullOrWhiteSpace(nextUnlock) && (effectiveUnlocks == null || !ContainsUnlock(effectiveUnlocks, nextUnlock)))
                {
                    virtualUnlocks.Add(nextUnlock);
                }
            }

            return virtualUnlocks;
        }

        public bool AdvanceSequencesForConsumedUnlocks(CareerSlot slot, IEnumerable<string> consumedUnlocks)
        {
            if (slot == null || consumedUnlocks == null)
            {
                return false;
            }

            var sequences = MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null).RepeatableUnlockSequences;
            if (sequences == null || sequences.Count == 0)
            {
                return false;
            }

            EnsureSequenceState(slot);

            var changed = false;
            foreach (var consumedUnlock in consumedUnlocks)
            {
                if (IsNullOrWhiteSpace(consumedUnlock))
                {
                    continue;
                }

                for (var i = 0; i < sequences.Count; i++)
                {
                    var sequence = sequences[i];
                    if (sequence == null || sequence.Unlocks == null || sequence.Unlocks.Count == 0)
                    {
                        continue;
                    }

                    if (IsDailyRepeatableSequenceKey(sequence.Key))
                    {
                        // Daily sequences are selected from UTC day and should not be advanced by mission consumption.
                        continue;
                    }

                    var consumedIndex = FindUnlockIndex(sequence.Unlocks, consumedUnlock);
                    if (consumedIndex < 0)
                    {
                        continue;
                    }

                    var nextIndex = (consumedIndex + 1) % sequence.Unlocks.Count;
                    int currentIndex;
                    if (!slot.RepeatableUnlockSequencePositions.TryGetValue(sequence.Key, out currentIndex) || currentIndex != nextIndex)
                    {
                        slot.RepeatableUnlockSequencePositions[sequence.Key] = nextIndex;
                        changed = true;
                    }

                    break;
                }
            }

            return changed;
        }

        private static void EnsureSequenceState(CareerSlot slot)
        {
            if (slot.ActiveUnlocks == null)
            {
                slot.ActiveUnlocks = new List<string>();
            }

            if (slot.RepeatableUnlockSequencePositions == null)
            {
                slot.RepeatableUnlockSequencePositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void ApplyDailyRepeatableSequencePositions(CareerSlot slot, List<MetagameplayStaticDataIndex.UnlockSequenceInfo> sequences)
        {
            if (slot == null || sequences == null || sequences.Count == 0)
            {
                return;
            }

            var utcDay = DateTime.UtcNow.Date;
            var utcDayTicks = utcDay.Ticks;
            var dayChanged = slot.LastRepeatableMissionResetUtcTicks != utcDayTicks;
            if (dayChanged)
            {
                slot.LastRepeatableMissionResetUtcTicks = utcDayTicks;
            }

            var dayNumber = (int)(utcDayTicks / TimeSpan.TicksPerDay);
            // Flip mission variants every UTC day; pair 2 is phase-shifted for variety.
            var pairOneIndex = dayNumber & 1;
            var pairTwoIndex = (dayNumber + 1) & 1;

            for (var i = 0; i < sequences.Count; i++)
            {
                var sequence = sequences[i];
                if (sequence == null || IsNullOrWhiteSpace(sequence.Key) || sequence.Unlocks == null || sequence.Unlocks.Count <= 1)
                {
                    continue;
                }

                var forcedIndex = GetDailyIndexForSequence(sequence.Key, pairOneIndex, pairTwoIndex);
                if (!forcedIndex.HasValue)
                {
                    continue;
                }

                var normalizedIndex = forcedIndex.Value;
                if (normalizedIndex < 0 || normalizedIndex >= sequence.Unlocks.Count)
                {
                    normalizedIndex = 0;
                }

                int currentIndex;
                if (!slot.RepeatableUnlockSequencePositions.TryGetValue(sequence.Key, out currentIndex) || currentIndex != normalizedIndex || dayChanged)
                {
                    slot.RepeatableUnlockSequencePositions[sequence.Key] = normalizedIndex;
                }
            }
        }

        private static int? GetDailyIndexForSequence(string sequenceKey, int pairOneIndex, int pairTwoIndex)
        {
            if (string.Equals(sequenceKey, SequenceRMission12, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sequenceKey, SequenceERMission21, StringComparison.OrdinalIgnoreCase))
            {
                return pairOneIndex;
            }

            if (string.Equals(sequenceKey, SequenceRMission34, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sequenceKey, SequenceERMission43, StringComparison.OrdinalIgnoreCase))
            {
                return pairTwoIndex;
            }

            return null;
        }

        private static bool IsDailyRepeatableSequenceKey(string sequenceKey)
        {
            if (IsNullOrWhiteSpace(sequenceKey))
            {
                return false;
            }

            return string.Equals(sequenceKey, SequenceRMission12, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sequenceKey, SequenceRMission34, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sequenceKey, SequenceERMission21, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sequenceKey, SequenceERMission43, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldActivateSequence(MetagameplayStaticDataIndex.UnlockSequenceInfo sequence, ICollection<string> effectiveUnlocks)
        {
            if (sequence == null || sequence.Unlocks == null || sequence.Unlocks.Count == 0)
            {
                return false;
            }

            var requiresAddon2 = false;
            for (var i = 0; i < sequence.Unlocks.Count; i++)
            {
                var unlock = sequence.Unlocks[i];
                if (!string.IsNullOrEmpty(unlock)
                    && (unlock.StartsWith("RMission", StringComparison.OrdinalIgnoreCase)
                        || unlock.StartsWith("ERMission", StringComparison.OrdinalIgnoreCase)))
                {
                    requiresAddon2 = true;
                    break;
                }
            }

            if (!requiresAddon2)
            {
                return true;
            }

            return ContainsUnlock(effectiveUnlocks, "SRO-Addon2");
        }

        private static bool ContainsUnlock(ICollection<string> unlocks, string targetUnlock)
        {
            if (unlocks == null || IsNullOrWhiteSpace(targetUnlock))
            {
                return false;
            }

            foreach (var unlock in unlocks)
            {
                if (string.Equals(unlock, targetUnlock, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int FindActiveUnlockIndex(List<string> activeUnlocks, MetagameplayStaticDataIndex.UnlockSequenceInfo sequence)
        {
            if (activeUnlocks == null || sequence == null || sequence.Unlocks == null)
            {
                return -1;
            }

            for (var i = 0; i < sequence.Unlocks.Count; i++)
            {
                var unlock = sequence.Unlocks[i];
                if (ContainsUnlock(activeUnlocks, unlock))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindUnlockIndex(List<string> unlocks, string targetUnlock)
        {
            if (unlocks == null || IsNullOrWhiteSpace(targetUnlock))
            {
                return -1;
            }

            for (var i = 0; i < unlocks.Count; i++)
            {
                if (string.Equals(unlocks[i], targetUnlock, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return string.IsNullOrEmpty(value) || string.IsNullOrEmpty(value.Trim());
        }
    }
}