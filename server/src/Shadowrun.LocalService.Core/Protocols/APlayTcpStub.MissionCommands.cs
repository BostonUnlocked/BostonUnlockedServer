using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;
using Shadowrun.LocalService.Core.Persistence;
using Shadowrun.LocalService.Core.Protocols.MissionCommands;
using Shadowrun.LocalService.Core.Simulation;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private void HandleMissionCommandCall(
            NetworkStream stream,
            string peer,
            int fieldId,
            byte[] data,
            ulong directMessageNumber,
            ulong gameworldEntityId,
            ulong gameClientEntityId,
            string activeIdentityHash,
            Guid activeIdentityGuid,
            int activeCareerIndex,
            string activeCharacterName,
            string currentMissionMapName,
            HashSet<string> completedStoryMissions,
            ref string currentCoopGroupName,
            ref ServerSimulationSession simulationSession,
            ref object simulationSessionSync,
            ref byte[] cachedHubStatePayload,
            byte[] cachedCreationInfoPayload)
        {
            ParsedMissionCommandRequest request;
            object missionLog;
            if (!TryParseMissionCommandRequest(fieldId, data, out request, out missionLog) || request == null)
            {
                return;
            }

            if (missionLog != null)
            {
                _logger.Log(missionLog);
            }

            var responseMsgNoBase = directMessageNumber + 1000;

            switch (request.Kind)
            {
                case MissionCommandKind.MissionReady:
                    return;

                case MissionCommandKind.LeaveMission:
                    HandleLeaveMissionCommand(
                        stream,
                        peer,
                        responseMsgNoBase,
                        gameworldEntityId,
                        gameClientEntityId,
                        activeIdentityHash,
                        activeIdentityGuid,
                        activeCareerIndex,
                        activeCharacterName,
                        currentMissionMapName,
                        completedStoryMissions,
                        ref currentCoopGroupName,
                        ref simulationSession,
                        ref simulationSessionSync,
                        ref cachedHubStatePayload,
                        cachedCreationInfoPayload);
                    return;

                case MissionCommandKind.FollowPath:
                    HandleFollowPathMissionCommand(
                        stream,
                        peer,
                        request,
                        responseMsgNoBase,
                        gameworldEntityId,
                        gameClientEntityId,
                        currentCoopGroupName,
                        simulationSession,
                        simulationSessionSync);
                    return;

                case MissionCommandKind.ActivateActiveSkill:
                    HandleActivateActiveSkillMissionCommand(
                        stream,
                        peer,
                        request,
                        responseMsgNoBase,
                        gameworldEntityId,
                        gameClientEntityId,
                        currentCoopGroupName,
                        simulationSession,
                        simulationSessionSync);
                    return;
            }
        }

        private bool TryParseMissionCommandRequest(int fieldId, byte[] data, out ParsedMissionCommandRequest request, out object missionLog)
        {
            request = null;
            missionLog = null;

            ushort refType;
            ulong refId;
            int offset;

            switch (fieldId)
            {
                case 0:
                    if (TryReadGameClientRef(data, 0, out refType, out refId, out offset))
                    {
                        missionLog = new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "aplay-mission-command",
                            field = "MissionReady",
                            gameClientRefType = refType,
                            gameClientRefId = refId,
                        };

                        request = new ParsedMissionCommandRequest(MissionCommandKind.MissionReady, refType, refId, null, null, null, null, null, null);
                    }
                    else
                    {
                        request = new ParsedMissionCommandRequest(MissionCommandKind.MissionReady, 0, 0UL, null, null, null, null, null, null);
                    }
                    return true;

                case 1:
                    if (TryReadGameClientRef(data, 0, out refType, out refId, out offset))
                    {
                        missionLog = new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "aplay-mission-command",
                            field = "LeaveMission",
                            gameClientRefType = refType,
                            gameClientRefId = refId,
                        };

                        request = new ParsedMissionCommandRequest(MissionCommandKind.LeaveMission, refType, refId, null, null, null, null, null, null);
                    }
                    else
                    {
                        request = new ParsedMissionCommandRequest(MissionCommandKind.LeaveMission, 0, 0UL, null, null, null, null, null, null);
                    }
                    return true;

                case 2:
                    if (!TryReadGameClientRef(data, 0, out refType, out refId, out offset))
                    {
                        return false;
                    }

                    int followAgentId;
                    int followTargetX;
                    int followTargetY;
                    if (!TryReadInt32LE(data, ref offset, out followAgentId)
                        || !TryReadInt32LE(data, ref offset, out followTargetX)
                        || !TryReadInt32LE(data, ref offset, out followTargetY))
                    {
                        return false;
                    }

                    request = new ParsedMissionCommandRequest(MissionCommandKind.FollowPath, refType, refId, null, null, null, followAgentId, followTargetX, followTargetY);
                    return true;

                case 3:
                    if (!TryReadGameClientRef(data, 0, out refType, out refId, out offset))
                    {
                        return false;
                    }

                    int weaponIndex;
                    int skillIndex;
                    int skillId;
                    int activateAgentId;
                    int activateTargetX;
                    int activateTargetY;
                    if (!TryReadInt32LE(data, ref offset, out weaponIndex)
                        || !TryReadInt32LE(data, ref offset, out skillIndex)
                        || !TryReadInt32LE(data, ref offset, out skillId)
                        || !TryReadInt32LE(data, ref offset, out activateAgentId)
                        || !TryReadInt32LE(data, ref offset, out activateTargetX)
                        || !TryReadInt32LE(data, ref offset, out activateTargetY))
                    {
                        return false;
                    }

                    request = new ParsedMissionCommandRequest(
                        MissionCommandKind.ActivateActiveSkill,
                        refType,
                        refId,
                        weaponIndex,
                        skillIndex,
                        skillId,
                        activateAgentId,
                        activateTargetX,
                        activateTargetY);
                    return true;
            }

            return false;
        }

        private void HandleFollowPathMissionCommand(
            NetworkStream stream,
            string peer,
            ParsedMissionCommandRequest request,
            ulong responseMsgNoBase,
            ulong gameworldEntityId,
            ulong gameClientEntityId,
            string currentCoopGroupName,
            ServerSimulationSession simulationSession,
            object simulationSessionSync)
        {
            if (request == null || !request.AgentId.HasValue || !request.TargetX.HasValue || !request.TargetY.HasValue)
            {
                return;
            }

            if (!IsMissionAgentCommandAuthorized(peer, simulationSession, gameClientEntityId, request))
            {
                return;
            }

            var followPathPayload = Concat(
                BitConverter.GetBytes(request.AgentId.Value),
                BitConverter.GetBytes(request.TargetX.Value),
                BitConverter.GetBytes(request.TargetY.Value));
            var followPathCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 1, followPathPayload), responseMsgNoBase + 1);

            SendRawFrame(stream, peer, PrefixLength(followPathCore), "echoed GameworldCommunicationObject FollowPath from MissionCommand");
            BroadcastToCoopMissionPeers(currentCoopGroupName, peer, PrefixLength(followPathCore), "echoed GameworldCommunicationObject FollowPath from MissionCommand (coop bcast)");

            if (simulationSession == null)
            {
                return;
            }

            IList<ServerSimulationSession.AiTurnAction> aiActions = null;
            try
            {
                if (simulationSessionSync != null)
                {
                    lock (simulationSessionSync)
                    {
                        simulationSession.ExecuteFollowPath(request.AgentId.Value, request.TargetX.Value, request.TargetY.Value);
                        aiActions = simulationSession.SkipAiTurnsIfNeeded();
                    }
                }
                else
                {
                    simulationSession.ExecuteFollowPath(request.AgentId.Value, request.TargetX.Value, request.TargetY.Value);
                    aiActions = simulationSession.SkipAiTurnsIfNeeded();
                }
            }
            catch (Exception ex)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = peer,
                    status = "execute-failed",
                    cmd = "FollowPath",
                    agentId = request.AgentId.Value,
                    targetX = request.TargetX.Value,
                    targetY = request.TargetY.Value,
                    message = ex.Message,
                });
            }

            TryBroadcastAiTurnActions(stream, peer, currentCoopGroupName, gameworldEntityId, responseMsgNoBase + 3, aiActions);
            SendPendingLootPreviews(simulationSession, stream, peer, responseMsgNoBase + 900);
        }

        private void HandleActivateActiveSkillMissionCommand(
            NetworkStream stream,
            string peer,
            ParsedMissionCommandRequest request,
            ulong responseMsgNoBase,
            ulong gameworldEntityId,
            ulong gameClientEntityId,
            string currentCoopGroupName,
            ServerSimulationSession simulationSession,
            object simulationSessionSync)
        {
            if (request == null
                || !request.WeaponIndex.HasValue
                || !request.SkillIndex.HasValue
                || !request.SkillId.HasValue
                || !request.AgentId.HasValue
                || !request.TargetX.HasValue
                || !request.TargetY.HasValue)
            {
                return;
            }

            if (!IsMissionAgentCommandAuthorized(peer, simulationSession, gameClientEntityId, request))
            {
                return;
            }

            var seed0 = 0x11111111u;
            var seed1 = 0x22222222u;
            var seed2 = 0x33333333u;
            var seed3 = 0x44444444u;
            var seedPackage = new Cliffhanger.SRO.ServerClientCommons.Gameworld.Communication.SeedPackage(seed0, seed1, seed2, seed3);

            if (simulationSession != null)
            {
                try
                {
                    if (simulationSessionSync != null)
                    {
                        lock (simulationSessionSync)
                        {
                            seedPackage = simulationSession.CreateSeedPackage();
                            simulationSession.ExecuteActivateSkill(
                                request.WeaponIndex.Value,
                                request.SkillIndex.Value,
                                request.SkillId.Value,
                                request.AgentId.Value,
                                request.TargetX.Value,
                                request.TargetY.Value,
                                seedPackage);
                        }
                    }
                    else
                    {
                        seedPackage = simulationSession.CreateSeedPackage();
                        simulationSession.ExecuteActivateSkill(
                            request.WeaponIndex.Value,
                            request.SkillIndex.Value,
                            request.SkillId.Value,
                            request.AgentId.Value,
                            request.TargetX.Value,
                            request.TargetY.Value,
                            seedPackage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "sim",
                        peer = peer,
                        status = "execute-failed",
                        cmd = "ActivateActiveSkill",
                        skillId = request.SkillId.Value,
                        agentId = request.AgentId.Value,
                        message = ex.Message,
                    });
                }

                seed0 = seedPackage.Seed0;
                seed1 = seedPackage.Seed1;
                seed2 = seedPackage.Seed2;
                seed3 = seedPackage.Seed3;
            }

            var activatePayload = Concat(
                BitConverter.GetBytes(request.WeaponIndex.Value),
                BitConverter.GetBytes(request.SkillIndex.Value),
                BitConverter.GetBytes(request.SkillId.Value),
                BitConverter.GetBytes(request.AgentId.Value),
                BitConverter.GetBytes(request.TargetX.Value),
                BitConverter.GetBytes(request.TargetY.Value),
                BitConverter.GetBytes(seed0),
                BitConverter.GetBytes(seed1),
                BitConverter.GetBytes(seed2),
                BitConverter.GetBytes(seed3));

            var activateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 2, activatePayload), responseMsgNoBase + 2);
            SendRawFrame(stream, peer, PrefixLength(activateCore), "echoed GameworldCommunicationObject ActivateActiveSkill from MissionCommand");
            BroadcastToCoopMissionPeers(currentCoopGroupName, peer, PrefixLength(activateCore), "echoed GameworldCommunicationObject ActivateActiveSkill from MissionCommand (coop bcast)");

            if (simulationSession == null)
            {
                return;
            }

            IList<ServerSimulationSession.AiTurnAction> aiActions = null;
            try
            {
                if (simulationSessionSync != null)
                {
                    lock (simulationSessionSync)
                    {
                        aiActions = simulationSession.SkipAiTurnsIfNeeded();
                    }
                }
                else
                {
                    aiActions = simulationSession.SkipAiTurnsIfNeeded();
                }
            }
            catch (Exception ex)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = peer,
                    status = "skip-ai-failed",
                    message = ex.Message,
                });
            }

            TryBroadcastAiTurnActions(stream, peer, currentCoopGroupName, gameworldEntityId, responseMsgNoBase + 3, aiActions);
            SendPendingLootPreviews(simulationSession, stream, peer, responseMsgNoBase + 950);
        }

        private bool IsMissionAgentCommandAuthorized(string peer, ServerSimulationSession simulationSession, ulong gameClientEntityId, ParsedMissionCommandRequest request)
        {
            if (simulationSession == null || request == null || !request.AgentId.HasValue)
            {
                return true;
            }

            bool authorized;
            try
            {
                authorized = simulationSession.CanPlayerControlAgent(gameClientEntityId, request.AgentId.Value);
            }
            catch
            {
                authorized = true;
            }

            if (authorized)
            {
                return true;
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "aplay-mission-command",
                peer = peer,
                status = "rejected",
                reason = "agent-not-controlled-by-player",
                command = request.Kind.ToString(),
                gameClientEntityId = gameClientEntityId,
                agentId = request.AgentId.Value,
            });

            return false;
        }

        private void TryBroadcastAiTurnActions(
            NetworkStream stream,
            string peer,
            string currentCoopGroupName,
            ulong gameworldEntityId,
            ulong baseMessageNumber,
            IList<ServerSimulationSession.AiTurnAction> aiActions)
        {
            if (aiActions == null || aiActions.Count == 0)
            {
                return;
            }

            try
            {
                for (var i = 0; i < aiActions.Count; i++)
                {
                    var aiAction = aiActions[i];
                    if (aiAction == null)
                    {
                        continue;
                    }

                    if (aiAction.Kind == ServerSimulationSession.AiTurnActionKind.FollowPath)
                    {
                        var followPayload = Concat(
                            BitConverter.GetBytes(aiAction.AgentId),
                            BitConverter.GetBytes(aiAction.TargetX),
                            BitConverter.GetBytes(aiAction.TargetY));

                        var followCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 1, followPayload), baseMessageNumber + (ulong)i);
                        SendRawFrame(stream, peer, PrefixLength(followCore), "sim: AI FollowPath (agentId=" + aiAction.AgentId + ", x=" + aiAction.TargetX + ", y=" + aiAction.TargetY + ")");
                        BroadcastToCoopMissionPeers(currentCoopGroupName, peer, PrefixLength(followCore), "sim: AI FollowPath (coop bcast) (agentId=" + aiAction.AgentId + ")");
                        continue;
                    }

                    var seed0 = aiAction.Seeds.Seed0;
                    var seed1 = aiAction.Seeds.Seed1;
                    var seed2 = aiAction.Seeds.Seed2;
                    var seed3 = aiAction.Seeds.Seed3;

                    var activatePayload = Concat(
                        BitConverter.GetBytes(aiAction.WeaponIndex),
                        BitConverter.GetBytes(aiAction.SkillIndex),
                        BitConverter.GetBytes(aiAction.SkillId),
                        BitConverter.GetBytes(aiAction.AgentId),
                        BitConverter.GetBytes(aiAction.TargetX),
                        BitConverter.GetBytes(aiAction.TargetY),
                        BitConverter.GetBytes(seed0),
                        BitConverter.GetBytes(seed1),
                        BitConverter.GetBytes(seed2),
                        BitConverter.GetBytes(seed3));

                    var activateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 2, activatePayload), baseMessageNumber + (ulong)i);
                    if (aiAction.SkillId == ServerSimulationSession.EndTeamTurnSkillId)
                    {
                        SendRawFrame(stream, peer, PrefixLength(activateCore), "sim: auto-ended AI team turn (agentId=" + aiAction.AgentId + ")");
                        BroadcastToCoopMissionPeers(currentCoopGroupName, peer, PrefixLength(activateCore), "sim: auto-ended AI team turn (coop bcast) (agentId=" + aiAction.AgentId + ")");
                    }
                    else
                    {
                        SendRawFrame(stream, peer, PrefixLength(activateCore), "sim: AI ActivateActiveSkill (agentId=" + aiAction.AgentId + ", skillId=" + aiAction.SkillId + ")");
                        BroadcastToCoopMissionPeers(currentCoopGroupName, peer, PrefixLength(activateCore), "sim: AI ActivateActiveSkill (coop bcast) (agentId=" + aiAction.AgentId + ", skillId=" + aiAction.SkillId + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = peer,
                    status = "skip-ai-failed",
                    message = ex.Message,
                });
            }
        }

        private void HandleLeaveMissionCommand(
            NetworkStream stream,
            string peer,
            ulong responseMsgNoBase,
            ulong gameworldEntityId,
            ulong gameClientEntityId,
            string activeIdentityHash,
            Guid activeIdentityGuid,
            int activeCareerIndex,
            string activeCharacterName,
            string currentMissionMapName,
            HashSet<string> completedStoryMissions,
            ref string currentCoopGroupName,
            ref ServerSimulationSession simulationSession,
            ref object simulationSessionSync,
            ref byte[] cachedHubStatePayload,
            byte[] cachedCreationInfoPayload)
        {
            var participantId = gameClientEntityId;
            var leavingMidMission = false;
            if (simulationSession != null)
            {
                try
                {
                    leavingMidMission = simulationSession.IsMissionStarted && !simulationSession.IsMissionStopped;
                }
                catch
                {
                    leavingMidMission = false;
                }
            }

            var missionOutcome = leavingMidMission ? "Abort" : "Victory";
            if (!leavingMidMission && simulationSession != null)
            {
                try
                {
                    string simOutcome;
                    if (simulationSession.TryGetMissionOutcomeForPlayer(participantId, out simOutcome) && !IsNullOrWhiteSpace(simOutcome))
                    {
                        missionOutcome = simOutcome;
                    }
                    else if (simulationSession.IsMissionStopped)
                    {
                        missionOutcome = "Defeat";
                    }
                }
                catch
                {
                }
            }

            var isVictory = string.Equals(missionOutcome, "Victory", StringComparison.OrdinalIgnoreCase);
            var completedMapName = !IsNullOrWhiteSpace(currentMissionMapName) ? currentMissionMapName : "1_010_Prologue";

            if (isVictory)
            {
                completedStoryMissions.Add(completedMapName);
            }

            if (_userStore != null)
            {
                try
                {
                    var progressSlot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                    if (progressSlot != null)
                    {
                        if (progressSlot.MainCampaignMissionStates == null)
                        {
                            progressSlot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        }

                        progressSlot.MainCampaignMissionStates[completedMapName] = !isVictory
                            ? StoryMissionstate.ReadyToPlay.ToString()
                            : StoryMissionstate.ReadyToReceiveRewards.ToString();
                        _userStore.UpsertCareer(activeIdentityHash, progressSlot);

                        if (isVictory)
                        {
                            TryAdvanceMainCampaignIfEligible(activeIdentityHash, progressSlot, peer, stream, "Main Campaign", responseMsgNoBase + 9, ref cachedHubStatePayload, cachedCreationInfoPayload, false);
                        }
                    }
                }
                catch
                {
                }
            }

            int karmaReward = 0;
            int nuyenReward = 0;
            int lootNuyenReward = 0;
            string[] lootItemIds = new string[0];
            string[] lootTables = new string[0];
            var lootItemChanges = new List<ItemChange>();
            if (simulationSession != null)
            {
                try
                {
                    LocalMissionLootController.LootGrant[] grants = null;
                    var coopLootAppliedAlready = false;

                    if (!IsNullOrWhiteSpace(currentCoopGroupName))
                    {
                        CoopMissionSessionState coopSession = null;
                        lock (_coopMissionLock)
                        {
                            _coopMissionSessions.TryGetValue(currentCoopGroupName, out coopSession);
                        }

                        if (coopSession != null)
                        {
                            lock (coopSession.SyncRoot)
                            {
                                if (coopSession.LootAppliedToParticipants == null)
                                {
                                    coopSession.LootAppliedToParticipants = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                                }

                                if (coopSession.LootSnapshot == null)
                                {
                                    coopSession.LootSnapshot = simulationSession.DrainPendingLoot();
                                }

                                var participantKey = (activeIdentityHash ?? string.Empty) + ":" + activeCareerIndex.ToString(CultureInfo.InvariantCulture);
                                bool already;
                                if (coopSession.LootAppliedToParticipants.TryGetValue(participantKey, out already) && already)
                                {
                                    coopLootAppliedAlready = true;
                                    grants = new LocalMissionLootController.LootGrant[0];
                                }
                                else
                                {
                                    coopSession.LootAppliedToParticipants[participantKey] = true;
                                    grants = coopSession.LootSnapshot ?? new LocalMissionLootController.LootGrant[0];
                                }
                            }
                        }
                    }

                    if (grants == null)
                    {
                        grants = simulationSession.DrainPendingLoot();
                    }

                    if (grants != null && grants.Length > 0)
                    {
                        var items = new List<string>();
                        var tables = new List<string>();
                        for (var i = 0; i < grants.Length; i++)
                        {
                            var grant = grants[i];
                            if (grant == null)
                            {
                                continue;
                            }

                            if (!IsNullOrWhiteSpace(grant.LootTable))
                            {
                                tables.Add(grant.LootTable);
                            }

                            if (!IsNullOrWhiteSpace(grant.ItemId))
                            {
                                items.Add(grant.ItemId);
                                if (grant.Delta != 0)
                                {
                                    try
                                    {
                                        lootItemChanges.Add(new ItemChange(grant.ItemId, grant.Delta)
                                        {
                                            Quality = grant.Quality,
                                            Flavour = grant.Flavour,
                                        });
                                    }
                                    catch
                                    {
                                    }
                                }
                            }

                            if (grant.Nuyen > 0)
                            {
                                try
                                {
                                    checked
                                    {
                                        lootNuyenReward = lootNuyenReward + grant.Nuyen;
                                    }
                                }
                                catch
                                {
                                    lootNuyenReward = int.MaxValue;
                                }
                            }
                        }

                        lootItemIds = items.ToArray();
                        lootTables = tables.ToArray();

                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "mission-loot-drain",
                            peer = peer,
                            mapName = completedMapName,
                            coopGroupName = currentCoopGroupName,
                            coopAppliedAlready = coopLootAppliedAlready,
                            grants = grants.Length,
                            nuyenFromLoot = lootNuyenReward,
                            lootTables = lootTables,
                            itemIds = lootItemIds,
                            itemChanges = lootItemChanges != null ? lootItemChanges.Count : 0,
                        });
                    }
                }
                catch
                {
                }
            }

            if (!isVictory)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-exit",
                    peer = peer,
                    mapName = completedMapName,
                    outcome = missionOutcome,
                    note = leavingMidMission ? "LeaveMission mid-mission; skipping completion credit/rewards" : "LeaveMission after mission end; non-victory outcome",
                });
            }

            int found;
            if (!leavingMidMission && TryResolveMissionCurrencyReward(completedMapName, missionOutcome, "Karma", out found) && found > 0)
            {
                karmaReward = found;
            }

            if (!leavingMidMission && TryResolveMissionCurrencyReward(completedMapName, missionOutcome, "Nuyen", out found) && found > 0)
            {
                nuyenReward = found;
            }

            if (lootNuyenReward > 0)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-loot",
                    peer = peer,
                    mapName = completedMapName,
                    nuyenValueIfAutoSold = lootNuyenReward,
                    lootTables = lootTables,
                    itemIds = lootItemIds,
                });
            }

            CareerSlot rewardSlot = null;
            if (_userStore != null)
            {
                rewardSlot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;

                var appliedLootItems = 0;
                if (rewardSlot != null && lootItemChanges != null && lootItemChanges.Count > 0)
                {
                    if (rewardSlot.ItemPossessions == null)
                    {
                        rewardSlot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    }

                    for (var i = 0; i < lootItemChanges.Count; i++)
                    {
                        var change = lootItemChanges[i];
                        if (change == null || IsNullOrWhiteSpace(change.ItemDefintionId) || change.Delta == 0)
                        {
                            continue;
                        }

                        var packedKey = change.ItemDefintionId + "|" + change.Quality.ToString(CultureInfo.InvariantCulture) + "|" + change.Flavour.ToString(CultureInfo.InvariantCulture);
                        int existing;
                        if (!rewardSlot.ItemPossessions.TryGetValue(packedKey, out existing))
                        {
                            existing = 0;
                        }

                        var next = existing + change.Delta;
                        if (next <= 0)
                        {
                            if (rewardSlot.ItemPossessions.ContainsKey(packedKey))
                            {
                                rewardSlot.ItemPossessions.Remove(packedKey);
                            }
                        }
                        else
                        {
                            rewardSlot.ItemPossessions[packedKey] = next;
                        }
                        appliedLootItems++;
                    }
                }

                if (rewardSlot != null && (karmaReward > 0 || nuyenReward > 0 || appliedLootItems > 0))
                {
                    if (karmaReward > 0)
                    {
                        try
                        {
                            checked
                            {
                                rewardSlot.Karma = rewardSlot.Karma + karmaReward;
                            }
                        }
                        catch
                        {
                            rewardSlot.Karma = int.MaxValue;
                        }
                    }

                    if (nuyenReward > 0)
                    {
                        try
                        {
                            checked
                            {
                                rewardSlot.Nuyen = rewardSlot.Nuyen + nuyenReward;
                            }
                        }
                        catch
                        {
                            rewardSlot.Nuyen = int.MaxValue;
                        }
                    }

                    if (!IsNullOrWhiteSpace(activeIdentityHash))
                    {
                        _userStore.UpsertCareer(activeIdentityHash, rewardSlot);
                    }

                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "mission-reward",
                        peer = peer,
                        mapName = completedMapName,
                        outcome = missionOutcome,
                        karmaDelta = karmaReward,
                        karmaTotal = rewardSlot.Karma,
                        nuyenDelta = nuyenReward,
                        nuyenTotal = rewardSlot.Nuyen,
                        lootItemChanges = lootItemChanges != null ? lootItemChanges.Count : 0,
                        lootItemsApplied = appliedLootItems,
                        careerIndex = activeCareerIndex,
                    });

                    try
                    {
                        var earnedCurrencies = new List<object>();
                        if (karmaReward != 0)
                        {
                            earnedCurrencies.Add(new Dictionary<string, object>
                            {
                                { "CurrencyId", "Karma" },
                                { "EarnedValue", karmaReward },
                            });
                        }
                        if (nuyenReward != 0)
                        {
                            earnedCurrencies.Add(new Dictionary<string, object>
                            {
                                { "CurrencyId", "Nuyen" },
                                { "EarnedValue", nuyenReward },
                            });
                        }

                        var itemChangesPayload = new object[0];
                        if (lootItemChanges != null && lootItemChanges.Count > 0)
                        {
                            var list = new List<object>();
                            for (var i = 0; i < lootItemChanges.Count; i++)
                            {
                                var change = lootItemChanges[i];
                                if (change == null || IsNullOrWhiteSpace(change.ItemDefintionId) || change.Delta == 0)
                                {
                                    continue;
                                }

                                list.Add(new Dictionary<string, object>
                                {
                                    { "ItemDefintionId", change.ItemDefintionId },
                                    { "Delta", change.Delta },
                                    { "Quality", change.Quality },
                                    { "Flavour", change.Flavour },
                                });
                            }
                            itemChangesPayload = list.ToArray();
                        }

                        var missionRewardJson = Json.Serialize(new Dictionary<string, object>
                        {
                            { "GrantedUnlocks", new string[0] },
                            { "EarnedCurrencies", earnedCurrencies.ToArray() },
                            { "ItemChanges", itemChangesPayload },
                        });

                        var missionRewardPayload = BuildUtf16StringPayload(missionRewardJson);
                        var missionRewardCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 35, missionRewardPayload), responseMsgNoBase + 6);
                        SendRawFrame(stream, peer, PrefixLength(missionRewardCore), "sent MetaGameplayCommunicationObject GotMissionReward after LeaveMission");
                    }
                    catch
                    {
                    }
                }
            }

            if (_userStore != null)
            {
                try
                {
                    var slotForSnapshot = rewardSlot ?? (!IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null);
                    var zippedCareerInfo = slotForSnapshot != null
                        ? _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, activeCareerIndex, slotForSnapshot)
                        : _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, activeCareerIndex, activeCharacterName, false);

                    var metaSnapshotPayload = BuildUtf16StringPayload(zippedCareerInfo);
                    var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), responseMsgNoBase + 10);
                    SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient after LeaveMission (reward sync)");

                    if (slotForSnapshot != null)
                    {
                        var hubId = !IsNullOrWhiteSpace(slotForSnapshot.HubId) ? slotForSnapshot.HubId : DefaultHubId;
                        var characterIdentifier = !IsNullOrWhiteSpace(slotForSnapshot.CharacterIdentifier)
                            ? slotForSnapshot.CharacterIdentifier
                            : (activeIdentityGuid.ToString() + ":" + activeCareerIndex.ToString());
                        cachedHubStatePayload = BuildMetaHubPushPayload(4, SerializeHubStateOrFallback(hubId, characterIdentifier, slotForSnapshot.CharacterName, slotForSnapshot));
                    }
                }
                catch
                {
                }
            }

            var leavePayload = BitConverter.GetBytes(participantId);
            var leaveCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 3, leavePayload), responseMsgNoBase + 13);
            SendRawFrame(stream, peer, PrefixLength(leaveCore), "sent GameworldCommunicationObject LeaveMission (participantId=" + participantId + ")");

            var stopCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 0, new byte[0]), responseMsgNoBase + 14);
            SendRawFrame(stream, peer, PrefixLength(stopCore), "sent GameworldCommunicationObject Stop after LeaveMission");

            if (simulationSession == null)
            {
                return;
            }

            if (!IsNullOrWhiteSpace(currentCoopGroupName))
            {
                UnregisterCoopMissionParticipant(currentCoopGroupName, peer);
                currentCoopGroupName = null;
                simulationSession = null;
                simulationSessionSync = null;
                return;
            }

            try
            {
                simulationSession.Stop();
            }
            catch
            {
            }

            MissionRuntimeRegistry.MarkSoloMissionEnded(peer);
            simulationSession = null;
            simulationSessionSync = null;
        }
    }
}