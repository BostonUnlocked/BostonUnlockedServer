using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Threading;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private bool TryHandleSetStoryMissionStateMessage(
            bool isMetaGameplayWrappedMessage,
            string rawMessage,
            ulong incomingMsgNo,
            NetworkStream stream,
            string peer,
            HashSet<string> completedStoryMissions,
            string activeIdentityHash,
            Guid activeIdentityGuid,
            int activeCareerIndex,
            ref string currentHubInstanceId,
            ref PortedHubInstance currentHubInstance,
            ref byte[] cachedHubStatePayload,
            byte[] cachedCreationInfoPayload)
        {
            if (!isMetaGameplayWrappedMessage || rawMessage == null || rawMessage.IndexOf("SetStoryMissionStateMessage", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            ulong outMsgNo = 0;
            try
            {
                var minOutMsgNo = incomingMsgNo + 1;
                var lastSent = Interlocked.Read(ref _metaGameplayOutMsgNoHighWatermark);
                var lastSentU = lastSent > 0 ? (ulong)lastSent : 0UL;
                outMsgNo = lastSentU + 1UL;
                if (outMsgNo < minOutMsgNo)
                {
                    outMsgNo = minOutMsgNo;
                }

                var missionName = ExtractJsonStringValue(rawMessage, "Mission");
                var targetState = ExtractJsonStringValue(rawMessage, "TargetState");
                if (!IsNullOrWhiteSpace(missionName) && !IsNullOrWhiteSpace(targetState))
                {
                    var parsedTarget = ParseStoryMissionStateOrDefault(targetState, StoryMissionstate.Available);
                    if (parsedTarget >= StoryMissionstate.ReadyToReceiveRewards)
                    {
                        completedStoryMissions.Add(missionName);
                    }

                    var storyStateUpdate = new StoryMissionStateUpdateResult();
                    CareerSlot slotForStoryRewards = null;
                    if (_userStore != null)
                    {
                        try
                        {
                            var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                            if (slot != null)
                            {
                                slotForStoryRewards = slot;
                                storyStateUpdate = _storyProgressionService.ApplyMissionState(slot, "Main Campaign", missionName, parsedTarget);
                                _userStore.UpsertCareer(activeIdentityHash, slot);
                            }
                        }
                        catch
                        {
                        }
                    }

                    var storyRewardApplication = new MissionRewardApplicationResult();
                    if (storyStateUpdate.ShouldGrantStoryRewards && slotForStoryRewards != null)
                    {
                        try
                        {
                            var resolvedStoryReward = _missionRewardService.ResolveMissionReward("StoryRewards", missionName, "Victory");
                            storyRewardApplication = _missionRewardService.Apply(slotForStoryRewards, resolvedStoryReward, new string[0]);
                            if (storyRewardApplication.Persisted)
                            {
                                try
                                {
                                    _userStore.UpsertCareer(slotForStoryRewards);
                                }
                                catch
                                {
                                }

                                _logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "story-reward",
                                    peer = peer,
                                    mission = missionName,
                                    previousState = storyStateUpdate.PreviousState.ToString(),
                                    newState = parsedTarget.ToString(),
                                    karmaDelta = storyRewardApplication.KarmaAfter - storyRewardApplication.KarmaBefore,
                                    karmaTotal = storyRewardApplication.KarmaAfter,
                                    nuyenDelta = storyRewardApplication.NuyenAfter - storyRewardApplication.NuyenBefore,
                                    nuyenTotal = storyRewardApplication.NuyenAfter,
                                    grantedUnlocks = storyRewardApplication.AppliedGrantedUnlocks,
                                    itemChanges = storyRewardApplication.AppliedItemChangeCount,
                                    careerIndex = activeCareerIndex,
                                });
                            }
                        }
                        catch
                        {
                            storyRewardApplication = new MissionRewardApplicationResult();
                        }
                    }

                    try
                    {
                        var storyProgressChangeJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.MissionStateChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"Main Campaign\",\"Mission\":\"" + missionName + "\",\"NewState\":\"" + parsedTarget.ToString() + "\"}";
                        var storyProgressChangePayload = BuildUtf16StringPayload(storyProgressChangeJson);
                        var storyProgressChangeCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, storyProgressChangePayload), outMsgNo++);
                        SendRawFrame(stream, peer, PrefixLength(storyProgressChangeCore), "sent MetaGameplayCommunicationObject StoryprogressChanged (MissionStateChange " + parsedTarget.ToString() + ")");
                    }
                    catch
                    {
                    }

                    if (storyRewardApplication.ShouldNotifyClient && slotForStoryRewards != null)
                    {
                        SendMissionReward(stream, peer, outMsgNo++, storyRewardApplication.TransportReward, "(StoryRewards redemption)");
                    }

                    var chapterAdvanced = false;
                    if (slotForStoryRewards != null)
                    {
                        try
                        {
                            var chapterAdvance = _storyProgressionService.TryAdvanceIfEligible(slotForStoryRewards, "Main Campaign");
                            chapterAdvanced = chapterAdvance.Advanced;
                            if (chapterAdvanced)
                            {
                                if (_userStore != null && !IsNullOrWhiteSpace(activeIdentityHash))
                                {
                                    _userStore.UpsertCareer(activeIdentityHash, slotForStoryRewards);
                                }

                                var chapterChangeJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.ChapterChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"Main Campaign\",\"NewChapterIndex\":" + chapterAdvance.NewChapterIndex.ToString(CultureInfo.InvariantCulture) + "}";
                                var chapterChangePayload = BuildUtf16StringPayload(chapterChangeJson);
                                var chapterChangeCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, chapterChangePayload), outMsgNo++);
                                SendRawFrame(stream, peer, PrefixLength(chapterChangeCore), "sent MetaGameplayCommunicationObject StoryprogressChanged (ChapterChange " + chapterAdvance.NewChapterIndex.ToString(CultureInfo.InvariantCulture) + ")");
                            }
                        }
                        catch
                        {
                        }
                    }

                    if (!chapterAdvanced
                        && slotForStoryRewards != null
                        && (parsedTarget == StoryMissionstate.ReadyToPlay || parsedTarget == StoryMissionstate.Completed)
                        && slotForStoryRewards.MainCampaignCurrentChapter >= 0)
                    {
                        try
                        {
                            var currentChapterIndex = slotForStoryRewards.MainCampaignCurrentChapter;
                            var currentHubId = !IsNullOrWhiteSpace(slotForStoryRewards.HubId) ? slotForStoryRewards.HubId : DefaultHubId;

                            int triggerChapterIndex;
                            if (_storyProgressionService.TryGetRefreshTriggerChapterIndexWithDifferentHub("Main Campaign", currentChapterIndex, currentHubId, out triggerChapterIndex))
                            {
                                var triggerChapterJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.ChapterChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"Main Campaign\",\"NewChapterIndex\":" + triggerChapterIndex.ToString(CultureInfo.InvariantCulture) + "}";
                                var triggerChapterPayload = BuildUtf16StringPayload(triggerChapterJson);
                                var triggerChapterCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, triggerChapterPayload), outMsgNo++);
                                SendRawFrame(stream, peer, PrefixLength(triggerChapterCore), "sent MetaGameplayCommunicationObject StoryprogressChanged (ChapterChange " + triggerChapterIndex.ToString(CultureInfo.InvariantCulture) + ") refresh trigger after SetStoryMissionStateMessage");

                                var restoreChapterJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.ChapterChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"Main Campaign\",\"NewChapterIndex\":" + currentChapterIndex.ToString(CultureInfo.InvariantCulture) + "}";
                                var restoreChapterPayload = BuildUtf16StringPayload(restoreChapterJson);
                                var restoreChapterCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, restoreChapterPayload), outMsgNo++);
                                SendRawFrame(stream, peer, PrefixLength(restoreChapterCore), "sent MetaGameplayCommunicationObject StoryprogressChanged (ChapterChange " + currentChapterIndex.ToString(CultureInfo.InvariantCulture) + ") refresh restore after SetStoryMissionStateMessage");
                            }
                        }
                        catch
                        {
                        }
                    }

                    if (slotForStoryRewards != null)
                    {
                        try
                        {
                            var forceNewHubInstanceId = (parsedTarget == StoryMissionstate.ReadyToPlay || parsedTarget == StoryMissionstate.Completed || chapterAdvanced);
                            cachedHubStatePayload = BuildPortedHubStatePayloadForSlot(
                                slotForStoryRewards,
                                activeIdentityGuid,
                                activeCareerIndex,
                                forceNewHubInstanceId,
                                currentHubInstance,
                                out currentHubInstanceId,
                                out currentHubInstance);
                        }
                        catch
                        {
                        }
                    }

                    if (slotForStoryRewards != null)
                    {
                        try
                        {
                            var zipped = _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, activeCareerIndex, slotForStoryRewards);
                            var metaSnapshotPayload = BuildUtf16StringPayload(zipped);
                            var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), outMsgNo++);
                            SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient after SetStoryMissionStateMessage");
                        }
                        catch
                        {
                        }
                    }
                }

                var echoPayload = BuildUtf16StringPayload(rawMessage);
                var echoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 1, echoPayload), outMsgNo++);
                SendRawFrame(stream, peer, PrefixLength(echoCore), "echoed MetaGameplayCommunicationObject Message (SetStoryMissionStateMessage)");
            }
            finally
            {
                if (outMsgNo > 0)
                {
                    var lastUsed = outMsgNo - 1UL;
                    while (true)
                    {
                        var observed = Interlocked.Read(ref _metaGameplayOutMsgNoHighWatermark);
                        var observedU = observed > 0 ? (ulong)observed : 0UL;
                        if (observedU >= lastUsed)
                        {
                            break;
                        }

                        if (Interlocked.CompareExchange(ref _metaGameplayOutMsgNoHighWatermark, (long)lastUsed, observed) == observed)
                        {
                            break;
                        }
                    }
                }
            }

            return true;
        }
    }
}
