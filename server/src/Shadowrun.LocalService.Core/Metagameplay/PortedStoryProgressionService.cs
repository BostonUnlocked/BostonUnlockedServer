using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class StoryMissionStateUpdateResult
    {
        public bool Persisted;
        public bool Accepted;
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
        private readonly EffectiveUnlockResolver _unlockResolver;

        public PortedStoryProgressionService(LocalServiceOptions options, LocalUserStore userStore)
        {
            _options = options;
            _unlockResolver = new EffectiveUnlockResolver(userStore, options);
        }

        public PortedStoryProgressionService(LocalServiceOptions options)
            : this(options, null)
        {
        }

        public StoryMissionStateUpdateResult ApplyMissionState(CareerSlot slot, string storylineName, string missionName, StoryMissionstate targetState)
        {
            var result = new StoryMissionStateUpdateResult();
            result.TargetState = targetState;

            if (slot == null || string.IsNullOrEmpty(storylineName) || string.IsNullOrEmpty(missionName))
            {
                return result;
            }

            var isRepeatableMission = IsRepeatableMission(missionName);
            result.MarkedCompletedEnough = !isRepeatableMission && targetState >= StoryMissionstate.ReadyToReceiveRewards;

            if (slot.MainCampaignMissionStates == null)
            {
                slot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            string existing;
            if (slot.MainCampaignMissionStates.TryGetValue(missionName, out existing) && !string.IsNullOrEmpty(existing))
            {
                result.PreviousState = ParseStoryMissionStateOrDefault(existing, StoryMissionstate.Available);
            }

            if (isRepeatableMission && result.PreviousState >= StoryMissionstate.ReadyToReceiveRewards)
            {
                result.PreviousState = StoryMissionstate.ReadyToPlay;
                slot.MainCampaignMissionStates[missionName] = StoryMissionstate.ReadyToPlay.ToString();
                result.Persisted = true;
            }

            if (isRepeatableMission && targetState == StoryMissionstate.ReadyToReceiveRewards)
            {
                return result;
            }

            if (!IsNextStateValid(result.PreviousState, targetState))
            {
                return result;
            }

            result.ShouldGrantStoryRewards = result.PreviousState == StoryMissionstate.ReadyToReceiveRewards && targetState == StoryMissionstate.Completed;
            slot.MainCampaignMissionStates[missionName] = targetState.ToString();
            result.Accepted = true;

            if (targetState == StoryMissionstate.ReadyToPlay || targetState == StoryMissionstate.Completed)
            {
                result.InteractedNpcsChanged = MarkCurrentChapterDialogNpcsAsInteracted(slot, storylineName);
            }

            result.Persisted = true;
            return result;
        }

        public bool NormalizeRepeatableMissionStates(CareerSlot slot)
        {
            if (slot == null)
            {
                return false;
            }

            var changed = false;
            var utcDayTicks = DateTime.UtcNow.Date.Ticks;
            var isNewDay = slot.LastRepeatableMissionResetUtcTicks != utcDayTicks;
            if (isNewDay)
            {
                slot.LastRepeatableMissionResetUtcTicks = utcDayTicks;
                changed = true;
            }

            if (slot.MainCampaignMissionStates == null || slot.MainCampaignMissionStates.Count == 0)
            {
                return changed;
            }

            var missionNames = new List<string>(slot.MainCampaignMissionStates.Keys);
            for (var i = 0; i < missionNames.Count; i++)
            {
                var missionName = missionNames[i];
                if (string.IsNullOrEmpty(missionName) || !IsRepeatableMission(missionName))
                {
                    continue;
                }

                string rawState;
                if (!slot.MainCampaignMissionStates.TryGetValue(missionName, out rawState) || string.IsNullOrEmpty(rawState))
                {
                    continue;
                }

                var parsedState = ParseStoryMissionStateOrDefault(rawState, StoryMissionstate.Available);
                if (isNewDay)
                {
                    if (parsedState != StoryMissionstate.ReadyToPlay)
                    {
                        slot.MainCampaignMissionStates[missionName] = StoryMissionstate.ReadyToPlay.ToString();
                        changed = true;
                    }
                    continue;
                }

                if (parsedState >= StoryMissionstate.ReadyToReceiveRewards)
                {
                    slot.MainCampaignMissionStates[missionName] = StoryMissionstate.ReadyToPlay.ToString();
                    changed = true;
                }
            }

            return changed;
        }

        private static bool IsNextStateValid(StoryMissionstate previousState, StoryMissionstate nextState)
        {
            return ((int)previousState + 1) == (int)nextState;
        }

        public StoryChapterAdvanceResult TryAdvanceIfEligible(CareerSlot slot, string storylineName)
        {
            return TryAdvanceIfEligible(Guid.Empty, slot, storylineName);
        }

        public StoryChapterAdvanceResult TryAdvanceIfEligible(Guid identityGuid, CareerSlot slot, string storylineName)
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

            if (!_unlockResolver.HasAllActiveUnlocks(identityGuid, slot, chapter.RequiredUnlocks))
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

        public int GetVirtualChapterIndex(CareerSlot slot, string storylineName)
        {
            return GetVirtualChapterIndex(Guid.Empty, slot, storylineName);
        }

        public int GetVirtualChapterIndex(Guid identityGuid, CareerSlot slot, string storylineName)
        {
            MetagameplayStaticDataIndex.StorylineInfo storyline;
            MetagameplayStaticDataIndex.ChapterInfo chapter;
            int currentIndex;
            if (!TryGetCurrentChapter(slot, storylineName, out storyline, out chapter, out currentIndex))
            {
                return 0;
            }

            if (!AreAllRequiredMissionsAtLeast(slot, chapter, StoryMissionstate.ReadyToReceiveRewards))
            {
                return currentIndex;
            }

            return Math.Min(currentIndex + 1, storyline.Chapters.Count);
        }

        public string GetCurrentStoryHubId(CareerSlot slot, string storylineName)
        {
            return GetCurrentStoryHubId(Guid.Empty, slot, storylineName);
        }

        public string GetCurrentStoryHubId(Guid identityGuid, CareerSlot slot, string storylineName)
        {
            MetagameplayStaticDataIndex.StorylineInfo storyline;
            MetagameplayStaticDataIndex.ChapterInfo chapter;
            int currentIndex;
            if (!TryGetCurrentChapter(slot, storylineName, out storyline, out chapter, out currentIndex))
            {
                return slot != null ? slot.HubId : null;
            }

            if (storyline.Chapters == null || storyline.Chapters.Count == 0)
            {
                return slot != null ? slot.HubId : null;
            }

            if (!AreAllRequiredMissionsAtLeast(slot, chapter, StoryMissionstate.ReadyToReceiveRewards))
            {
                return !string.IsNullOrEmpty(chapter.Hub) ? chapter.Hub : slot.HubId;
            }

            var nextIndex = Math.Min(currentIndex + 1, storyline.Chapters.Count - 1);
            var nextChapter = storyline.Chapters[nextIndex];
            if (nextChapter != null && !string.IsNullOrEmpty(nextChapter.Hub))
            {
                return nextChapter.Hub;
            }

            return !string.IsNullOrEmpty(chapter.Hub) ? chapter.Hub : slot.HubId;
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

        private bool TryGetCurrentChapter(CareerSlot slot, string storylineName, out MetagameplayStaticDataIndex.StorylineInfo storyline, out MetagameplayStaticDataIndex.ChapterInfo chapter, out int currentIndex)
        {
            storyline = null;
            chapter = null;
            currentIndex = 0;

            if (slot == null || string.IsNullOrEmpty(storylineName))
            {
                return false;
            }

            if (!TryGetStoryline(storylineName, out storyline) || storyline == null || storyline.Chapters == null || storyline.Chapters.Count == 0)
            {
                return false;
            }

            currentIndex = slot.MainCampaignCurrentChapter;
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }
            if (currentIndex >= storyline.Chapters.Count)
            {
                currentIndex = storyline.Chapters.Count - 1;
            }

            chapter = storyline.Chapters[currentIndex];
            return chapter != null;
        }

        private static bool AreAllRequiredMissionsAtLeast(CareerSlot slot, MetagameplayStaticDataIndex.ChapterInfo chapter, StoryMissionstate minimumState)
        {
            if (chapter == null)
            {
                return false;
            }

            var states = slot != null && slot.MainCampaignMissionStates != null
                ? slot.MainCampaignMissionStates
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < chapter.RequiredMissions.Count; i++)
            {
                var missionName = chapter.RequiredMissions[i];
                if (string.IsNullOrEmpty(missionName))
                {
                    continue;
                }

                string raw;
                states.TryGetValue(missionName, out raw);
                if (ParseStoryMissionStateOrDefault(raw, StoryMissionstate.Available) < minimumState)
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryGetStoryline(string storylineName, out MetagameplayStaticDataIndex.StorylineInfo storyline)
        {
            return MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null).TryGetStoryline(storylineName, out storyline);
        }

        private bool IsRepeatableMission(string missionName)
        {
            return MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null).IsMissionRepeatable(missionName);
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
