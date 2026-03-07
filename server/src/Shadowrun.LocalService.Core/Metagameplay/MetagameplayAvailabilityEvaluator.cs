using System;
using System.Collections.Generic;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class MetagameplayAvailabilityCondition
    {
        public string TypeName;
        public string StorylineReference;
        public int ChapterIndex;
        public List<MetagameplayAvailabilityCondition> InnerConditions;
        public MetagameplayAvailabilityCondition InnerCondition;
        public List<string> Unlocks;
        public DateTime From;
        public DateTime To;
    }

    internal sealed class MetagameplayAvailabilityContext
    {
        public string StoryLine;
        public int Chapter;
        public int VirtualChapter;
        public DateTime CurrentTimeUtc;
        public HashSet<string> Unlocks;

        public static MetagameplayAvailabilityContext Create(CareerSlot slot)
        {
            return Create(slot, slot != null ? slot.MainCampaignCurrentChapter : 0);
        }

        public static MetagameplayAvailabilityContext Create(CareerSlot slot, int virtualChapter)
        {
            var unlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (slot != null && slot.ActiveUnlocks != null)
            {
                for (var i = 0; i < slot.ActiveUnlocks.Count; i++)
                {
                    var unlock = slot.ActiveUnlocks[i];
                    if (!string.IsNullOrEmpty(unlock))
                    {
                        unlocks.Add(unlock);
                    }
                }
            }

            return new MetagameplayAvailabilityContext
            {
                StoryLine = "Main Campaign",
                Chapter = slot != null ? slot.MainCampaignCurrentChapter : 0,
                VirtualChapter = virtualChapter,
                CurrentTimeUtc = DateTime.UtcNow,
                Unlocks = unlocks,
            };
        }
    }

    internal static class MetagameplayAvailabilityEvaluator
    {
        public static bool IsFulfilled(MetagameplayAvailabilityCondition condition, MetagameplayAvailabilityContext context)
        {
            if (condition == null)
            {
                return true;
            }

            var typeName = condition.TypeName ?? string.Empty;
            if (typeName.IndexOf("AndAvailabilityCondition", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AreAllFulfilled(condition.InnerConditions, context);
            }

            if (typeName.IndexOf("OrAvailabilityCondition", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return IsAnyFulfilled(condition.InnerConditions, context);
            }

            if (typeName.IndexOf("NotAvailabilityCondition", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return !IsFulfilled(condition.InnerCondition, context);
            }

            if (typeName.IndexOf("StoryAndVirtualChapterAvailabilityCondition", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return IsStoryAndChapterFulfilled(condition, context, true);
            }

            if (typeName.IndexOf("StoryAndChapterAvailabilityCondition", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return IsStoryAndChapterFulfilled(condition, context, false);
            }

            if (typeName.IndexOf("UnlockRequirementCondition", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AreUnlocksFulfilled(condition, context);
            }

            if (typeName.IndexOf("YearlyAvailabilityCondition", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return IsYearlyFulfilled(condition, context);
            }

            if (typeName.IndexOf("TrueAvailabilityCondition", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private static bool AreAllFulfilled(List<MetagameplayAvailabilityCondition> conditions, MetagameplayAvailabilityContext context)
        {
            if (conditions == null || conditions.Count == 0)
            {
                return true;
            }

            for (var i = 0; i < conditions.Count; i++)
            {
                if (!IsFulfilled(conditions[i], context))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAnyFulfilled(List<MetagameplayAvailabilityCondition> conditions, MetagameplayAvailabilityContext context)
        {
            if (conditions == null || conditions.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < conditions.Count; i++)
            {
                if (IsFulfilled(conditions[i], context))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsStoryAndChapterFulfilled(MetagameplayAvailabilityCondition condition, MetagameplayAvailabilityContext context, bool useVirtualChapter)
        {
            if (condition == null || context == null)
            {
                return false;
            }

            var chapter = useVirtualChapter ? context.VirtualChapter : context.Chapter;
            if (!string.IsNullOrEmpty(context.StoryLine))
            {
                if (!string.Equals(context.StoryLine, condition.StorylineReference, StringComparison.Ordinal))
                {
                    return false;
                }

                return chapter >= condition.ChapterIndex;
            }

            return false;
        }

        private static bool AreUnlocksFulfilled(MetagameplayAvailabilityCondition condition, MetagameplayAvailabilityContext context)
        {
            if (condition == null || condition.Unlocks == null || condition.Unlocks.Count == 0)
            {
                return true;
            }

            if (context == null || context.Unlocks == null)
            {
                return false;
            }

            for (var i = 0; i < condition.Unlocks.Count; i++)
            {
                var unlock = condition.Unlocks[i];
                if (string.IsNullOrEmpty(unlock))
                {
                    continue;
                }

                if (!context.Unlocks.Contains(unlock))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsYearlyFulfilled(MetagameplayAvailabilityCondition condition, MetagameplayAvailabilityContext context)
        {
            if (condition == null || context == null)
            {
                return false;
            }

            var currentTime = context.CurrentTimeUtc;
            var from = new DateTime(currentTime.Year, condition.From.Month, condition.From.Day, condition.From.Hour, condition.From.Minute, condition.From.Second, condition.From.Kind);
            var to = new DateTime(currentTime.Year, condition.To.Month, condition.To.Day, condition.To.Hour, condition.To.Minute, condition.To.Second, condition.To.Kind);

            if (condition.From.Year == condition.To.Year)
            {
                return currentTime >= from && currentTime <= to;
            }

            if (condition.To.Year > condition.From.Year)
            {
                return currentTime >= from || currentTime <= to;
            }

            return false;
        }
    }
}
