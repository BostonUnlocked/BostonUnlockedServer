using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class StoryMissionStateUpdateResult
    {
        public bool Persisted;
        public StoryMissionstate PreviousState = StoryMissionstate.Available;
        public StoryMissionstate TargetState = StoryMissionstate.Available;
        public bool ShouldGrantStoryRewards;
        public bool MarkedCompletedEnough;
        public bool InteractedNpcsChanged;
    }

    internal sealed class StoryChapterAdvanceResult
    {
        public bool Advanced;
        public int PreviousChapterIndex = -1;
        public int NewChapterIndex = -1;
        public string NewHubId;
    }

    internal sealed class PortedStoryProgressionService
    {
        private readonly LocalServiceOptions _options;

        public PortedStoryProgressionService(LocalServiceOptions options)
        {
            _options = options;
        }

        public StoryMissionStateUpdateResult ApplyMissionState(CareerSlot slot, string storylineName, string missionName, StoryMissionstate targetState)
        {
            var result = new StoryMissionStateUpdateResult();
            result.TargetState = targetState;
            result.MarkedCompletedEnough = targetState >= StoryMissionstate.ReadyToReceiveRewards;

            if (slot == null || string.IsNullOrEmpty(storylineName) || string.IsNullOrEmpty(missionName))
            {
                return result;
            }

            if (slot.MainCampaignMissionStates == null)
            {
                slot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            string existing;
            if (slot.MainCampaignMissionStates.TryGetValue(missionName, out existing) && !string.IsNullOrEmpty(existing))
            {
                result.PreviousState = ParseStoryMissionStateOrDefault(existing, StoryMissionstate.Available);
            }

            result.ShouldGrantStoryRewards = result.PreviousState == StoryMissionstate.ReadyToReceiveRewards && targetState == StoryMissionstate.Completed;
            slot.MainCampaignMissionStates[missionName] = targetState.ToString();

            if (targetState == StoryMissionstate.ReadyToPlay || targetState == StoryMissionstate.Completed)
            {
                result.InteractedNpcsChanged = MarkCurrentChapterDialogNpcsAsInteracted(slot, storylineName);
            }

            result.Persisted = true;
            return result;
        }

        public StoryChapterAdvanceResult TryAdvanceIfEligible(CareerSlot slot, string storylineName)
        {
            var result = new StoryChapterAdvanceResult();
            if (slot == null || string.IsNullOrEmpty(storylineName))
            {
                return result;
            }

            MetagameplayStaticDataIndex.StorylineInfo storyline;
            if (!TryGetStoryline(storylineName, out storyline) || storyline == null || storyline.Chapters == null || storyline.Chapters.Count == 0)
            {
                return result;
            }

            var currentIndex = slot.MainCampaignCurrentChapter;
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }
            if (currentIndex >= storyline.Chapters.Count)
            {
                currentIndex = storyline.Chapters.Count - 1;
            }

            var chapter = storyline.Chapters[currentIndex];
            if (chapter == null)
            {
                return result;
            }

            var states = slot.MainCampaignMissionStates ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < chapter.RequiredMissions.Count; i++)
            {
                var missionName = chapter.RequiredMissions[i];
                if (string.IsNullOrEmpty(missionName))
                {
                    continue;
                }

                string raw;
                states.TryGetValue(missionName, out raw);
                if (ParseStoryMissionStateOrDefault(raw, StoryMissionstate.Available) < StoryMissionstate.Completed)
                {
                    return result;
                }
            }

            if (!HasAllActiveUnlocks(slot, chapter.RequiredUnlocks))
            {
                return result;
            }

            var nextIndex = currentIndex + 1;
            if (nextIndex >= storyline.Chapters.Count)
            {
                return result;
            }

            var nextChapter = storyline.Chapters[nextIndex];
            slot.MainCampaignCurrentChapter = nextIndex;
            if (slot.MainCampaignInteractedNpcs != null)
            {
                slot.MainCampaignInteractedNpcs.Clear();
            }

            if (nextChapter != null && !string.IsNullOrEmpty(nextChapter.Hub))
            {
                slot.HubId = nextChapter.Hub;
            }

            result.Advanced = true;
            result.PreviousChapterIndex = currentIndex;
            result.NewChapterIndex = nextIndex;
            result.NewHubId = slot.HubId;
            return result;
        }

        public bool TryGetRefreshTriggerChapterIndexWithDifferentHub(string storylineName, int currentIndex, string currentHub, out int triggerIndex)
        {
            triggerIndex = -1;

            MetagameplayStaticDataIndex.StorylineInfo storyline;
            if (!TryGetStoryline(storylineName, out storyline) || storyline == null || storyline.Chapters == null || storyline.Chapters.Count == 0)
            {
                return false;
            }

            var boundedCurrent = currentIndex;
            if (boundedCurrent < 0)
            {
                boundedCurrent = 0;
            }
            if (boundedCurrent >= storyline.Chapters.Count)
            {
                boundedCurrent = storyline.Chapters.Count - 1;
            }

            var currentHubResolved = currentHub;
            if (string.IsNullOrEmpty(currentHubResolved))
            {
                var currentChapter = storyline.Chapters[boundedCurrent];
                if (currentChapter != null && !string.IsNullOrEmpty(currentChapter.Hub))
                {
                    currentHubResolved = currentChapter.Hub;
                }
            }

            for (var i = 0; i < storyline.Chapters.Count; i++)
            {
                if (i == boundedCurrent)
                {
                    continue;
                }

                var chapter = storyline.Chapters[i];
                if (chapter == null || string.IsNullOrEmpty(chapter.Hub))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(currentHubResolved) && string.Equals(chapter.Hub, currentHubResolved, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                triggerIndex = i;
                return true;
            }

            return false;
        }

        private bool MarkCurrentChapterDialogNpcsAsInteracted(CareerSlot slot, string storylineName)
        {
            var npcIds = GetCurrentChapterDialogNpcIds(slot, storylineName);
            if (npcIds == null || npcIds.Count == 0)
            {
                return false;
            }

            if (slot.MainCampaignInteractedNpcs == null)
            {
                slot.MainCampaignInteractedNpcs = new List<string>();
            }

            var changed = false;
            for (var i = 0; i < npcIds.Count; i++)
            {
                var npcId = npcIds[i];
                if (string.IsNullOrEmpty(npcId))
                {
                    continue;
                }

                if (!slot.MainCampaignInteractedNpcs.Contains(npcId))
                {
                    slot.MainCampaignInteractedNpcs.Add(npcId);
                    changed = true;
                }
            }

            return changed;
        }

        private List<string> GetCurrentChapterDialogNpcIds(CareerSlot slot, string storylineName)
        {
            var npcIds = new List<string>();
            if (slot == null || string.IsNullOrEmpty(storylineName))
            {
                return npcIds;
            }

            MetagameplayStaticDataIndex.StorylineInfo storyline;
            if (!TryGetStoryline(storylineName, out storyline) || storyline == null || storyline.Chapters == null || storyline.Chapters.Count == 0)
            {
                return npcIds;
            }

            var currentIndex = slot.MainCampaignCurrentChapter;
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }
            if (currentIndex >= storyline.Chapters.Count)
            {
                currentIndex = storyline.Chapters.Count - 1;
            }

            var chapter = storyline.Chapters[currentIndex];
            if (chapter == null || chapter.DialogNpcIds == null || chapter.DialogNpcIds.Count == 0)
            {
                return npcIds;
            }

            for (var i = 0; i < chapter.DialogNpcIds.Count; i++)
            {
                var npcId = chapter.DialogNpcIds[i];
                if (!string.IsNullOrEmpty(npcId) && !npcIds.Contains(npcId))
                {
                    npcIds.Add(npcId);
                }
            }

            return npcIds;
        }

        private bool TryGetStoryline(string storylineName, out MetagameplayStaticDataIndex.StorylineInfo storyline)
        {
            return MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null).TryGetStoryline(storylineName, out storyline);
        }

        private static bool HasAllActiveUnlocks(CareerSlot slot, List<string> requiredUnlocks)
        {
            if (requiredUnlocks == null || requiredUnlocks.Count == 0)
            {
                return true;
            }

            if (slot == null || slot.ActiveUnlocks == null || slot.ActiveUnlocks.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < requiredUnlocks.Count; i++)
            {
                var requiredUnlock = requiredUnlocks[i];
                if (string.IsNullOrEmpty(requiredUnlock))
                {
                    continue;
                }

                if (!slot.ActiveUnlocks.Contains(requiredUnlock))
                {
                    return false;
                }
            }

            return true;
        }

        private static StoryMissionstate ParseStoryMissionStateOrDefault(string value, StoryMissionstate fallback)
        {
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }

            try
            {
                return (StoryMissionstate)Enum.Parse(typeof(StoryMissionstate), value, true);
            }
            catch
            {
            }

            return fallback;
        }
    }
}
