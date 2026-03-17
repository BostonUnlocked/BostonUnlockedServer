using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class PortedStorySnapshotBuilder
    {
        private const string MainCampaignStoryline = "Main Campaign";

        private readonly LocalServiceOptions _options;

        public PortedStorySnapshotBuilder(LocalServiceOptions options)
        {
            _options = options;
        }

        public StoryProgressRuntimestate BuildStoryProgress(CareerSlot slot)
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

            MetagameplayStaticDataIndex.StorylineInfo storyline;
            var index = MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null);
            if (index == null || !index.TryGetStoryline(MainCampaignStoryline, out storyline) || storyline == null || storyline.Chapters == null || storyline.Chapters.Count == 0)
            {
                story.Storylines.Add(BuildFallbackRuntimeStoryline(slot));
                return story;
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
                story.Storylines.Add(BuildFallbackRuntimeStoryline(slot));
                return story;
            }

            var runtime = new RuntimeStoryline();
            runtime.Storyline = MainCampaignStoryline;
            runtime.CurrentChapter = currentIndex;
            runtime.InteractedNpcs = CloneNpcList(slot.MainCampaignInteractedNpcs);
            runtime.RequiredUnlocksForCurrentChapter = new List<string>(chapter.RequiredUnlocks);
            runtime.RuntimeMissions = new List<RuntimeMission>();

            AddRuntimeMissions(runtime.RuntimeMissions, chapter.RequiredMissions, slot, false);
            AddRuntimeMissions(runtime.RuntimeMissions, chapter.SideMissions, slot, true);

            story.Storylines.Add(runtime);
            return story;
        }

        private static RuntimeStoryline BuildFallbackRuntimeStoryline(CareerSlot slot)
        {
            var main = new RuntimeStoryline();
            main.Storyline = MainCampaignStoryline;
            main.CurrentChapter = slot.MainCampaignCurrentChapter;
            main.InteractedNpcs = CloneNpcList(slot.MainCampaignInteractedNpcs);
            main.RequiredUnlocksForCurrentChapter = new List<string>();
            main.RuntimeMissions = new List<RuntimeMission>();

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

                    main.RuntimeMissions.Add(new RuntimeMission
                    {
                        Mission = kvp.Key,
                        State = parsed,
                        IsOptionalMission = false,
                    });
                }
            }

            return main;
        }

        private static void AddRuntimeMissions(List<RuntimeMission> runtimeMissions, List<string> missionNames, CareerSlot slot, bool isOptional)
        {
            if (runtimeMissions == null || missionNames == null)
            {
                return;
            }

            for (var i = 0; i < missionNames.Count; i++)
            {
                var missionName = missionNames[i];
                if (IsNullOrWhiteSpace(missionName) || ContainsMission(runtimeMissions, missionName))
                {
                    continue;
                }

                string rawState = null;
                if (slot != null && slot.MainCampaignMissionStates != null)
                {
                    slot.MainCampaignMissionStates.TryGetValue(missionName, out rawState);
                }

                runtimeMissions.Add(new RuntimeMission
                {
                    Mission = missionName,
                    State = ParseStoryMissionStateOrDefault(rawState, StoryMissionstate.Available),
                    IsOptionalMission = isOptional,
                });
            }
        }

        private static bool ContainsMission(List<RuntimeMission> runtimeMissions, string missionName)
        {
            for (var i = 0; i < runtimeMissions.Count; i++)
            {
                var mission = runtimeMissions[i];
                if (mission != null && string.Equals(mission.Mission, missionName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> CloneNpcList(List<string> npcIds)
        {
            var result = new List<string>();
            if (npcIds == null)
            {
                return result;
            }

            for (var i = 0; i < npcIds.Count; i++)
            {
                var npcId = npcIds[i];
                if (!IsNullOrWhiteSpace(npcId) && !result.Contains(npcId))
                {
                    result.Add(npcId);
                }
            }

            return result;
        }

        private static StoryMissionstate ParseStoryMissionStateOrDefault(string value, StoryMissionstate fallback)
        {
            if (IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            try
            {
                return (StoryMissionstate)Enum.Parse(typeof(StoryMissionstate), value.Trim(), true);
            }
            catch
            {
                return fallback;
            }
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