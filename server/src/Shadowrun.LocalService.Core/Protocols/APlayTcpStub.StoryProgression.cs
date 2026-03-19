using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
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
            
            {
                var missionName = ExtractJsonStringValue(rawMessage, "Mission");
                var targetState = ExtractJsonStringValue(rawMessage, "TargetState");
                if (!IsNullOrWhiteSpace(missionName) && !IsNullOrWhiteSpace(targetState))
                {
                    var parsedTarget = ParseStoryMissionStateOrDefault(targetState, StoryMissionstate.Available);
                    var isRepeatableMission = IsRepeatableMission(missionName);

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

                    if (storyStateUpdate.Accepted && !isRepeatableMission && parsedTarget >= StoryMissionstate.ReadyToReceiveRewards)
                    {
                        completedStoryMissions.Add(missionName);
                    }

                    if (!storyStateUpdate.Accepted)
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "story-state-ignored",
                            peer = peer,
                            mission = missionName,
                            previousState = storyStateUpdate.PreviousState.ToString(),
                            requestedState = parsedTarget.ToString(),
                            incomingMsgNo = incomingMsgNo,
                            careerIndex = activeCareerIndex,
                        });
                        return true;
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
                                    if (!IsNullOrWhiteSpace(activeIdentityHash))
                                    {
                                        _userStore.UpsertCareer(activeIdentityHash, slotForStoryRewards);
                                    }
                                    else
                                    {
                                        _userStore.UpsertCareer(slotForStoryRewards);
                                    }
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

                    var chapterAdvanced = false;
                    var chapterAdvanceNewIndex = 0;
                    if (slotForStoryRewards != null)
                    {
                        try
                        {
                            var chapterAdvance = _storyProgressionService.TryAdvanceIfEligible(activeIdentityGuid, slotForStoryRewards, "Main Campaign");
                            chapterAdvanced = chapterAdvance.Advanced;
                            if (chapterAdvanced)
                            {
                                chapterAdvanceNewIndex = chapterAdvance.NewChapterIndex;
                                if (_userStore != null && !IsNullOrWhiteSpace(activeIdentityHash))
                                {
                                    _userStore.UpsertCareer(activeIdentityHash, slotForStoryRewards);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }

                    // Keep storyprogress transitions and hub routing decoupled.
                    // Hub state/session transitions are driven by explicit hub request flows.

                    var shouldSendMissionReward = storyRewardApplication.ShouldNotifyClient && slotForStoryRewards != null;

                    // Keep the accepted story-state burst aligned with retail ordering:
                    // chapter advancement already emits a StoryprogressChanged(ChapterChange),
                    // so avoid appending an immediate field-26 snapshot in that same burst.
                    var shouldSendMetaSnapshot = slotForStoryRewards != null
                        && storyRewardApplication.ShouldNotifyClient
                        && !chapterAdvanced;

                    var minOutMsgNo = incomingMsgNo + 1UL;
                    if (minOutMsgNo == 0UL)
                    {
                        minOutMsgNo = 1UL;
                    }

                    var sendCount = 1
                        + (shouldSendMissionReward ? 1 : 0)
                        + (chapterAdvanced ? 1 : 0)
                        + (shouldSendMetaSnapshot ? 1 : 0);
                    outMsgNo = ReserveMetaGameplayMsgNosWithFloor(minOutMsgNo, sendCount);
                    var firstOutMsgNo = outMsgNo;

                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "story-state-accepted",
                        peer = peer,
                        mission = missionName,
                        previousState = storyStateUpdate.PreviousState.ToString(),
                        requestedState = parsedTarget.ToString(),
                        incomingMsgNo = incomingMsgNo,
                        expectedFirstOutMsgNo = minOutMsgNo,
                        firstOutMsgNo = firstOutMsgNo,
                        sendCount = sendCount,
                        chapterAdvanced = chapterAdvanced,
                        shouldSendMissionReward = shouldSendMissionReward,
                        shouldSendMetaSnapshot = shouldSendMetaSnapshot,
                        careerIndex = activeCareerIndex,
                    });

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

                    if (shouldSendMissionReward)
                    {
                        SendMissionReward(stream, peer, outMsgNo++, storyRewardApplication.TransportReward, "(StoryRewards redemption)");
                    }

                    if (chapterAdvanced)
                    {
                        try
                        {
                            var chapterChangeJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.ChapterChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"Main Campaign\",\"NewChapterIndex\":" + chapterAdvanceNewIndex.ToString(CultureInfo.InvariantCulture) + "}";
                            var chapterChangePayload = BuildUtf16StringPayload(chapterChangeJson);
                            var chapterChangeCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, chapterChangePayload), outMsgNo++);
                            SendRawFrame(stream, peer, PrefixLength(chapterChangeCore), "sent MetaGameplayCommunicationObject StoryprogressChanged (ChapterChange " + chapterAdvanceNewIndex.ToString(CultureInfo.InvariantCulture) + ")");
                        }
                        catch
                        {
                        }
                    }

                    if (shouldSendMetaSnapshot)
                    {
                        try
                        {
                            var zipped = _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, activeCareerIndex, slotForStoryRewards);
                            _logger.Log(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "field26-snapshot-send",
                                trigger = "set-story-mission-state",
                                mission = missionName,
                                targetState = parsedTarget.ToString(),
                                chapterAdvanced = chapterAdvanced,
                                shouldNotifyClient = storyRewardApplication.ShouldNotifyClient,
                                careerIndex = activeCareerIndex,
                                blobLength = !IsNullOrWhiteSpace(zipped) ? zipped.Length : 0,
                                slotMainCampaignCurrentChapter = slotForStoryRewards.MainCampaignCurrentChapter,
                            });
                            var metaSnapshotPayload = BuildUtf16StringPayload(zipped);
                            var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), outMsgNo++);
                            SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient after SetStoryMissionStateMessage");
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return true;
        }
    }
}
