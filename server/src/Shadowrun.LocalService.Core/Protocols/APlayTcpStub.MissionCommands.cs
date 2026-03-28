using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;
using Shadowrun.LocalService.Core.Metagameplay;
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
            ulong missionInstanceEntityId,
            ulong missionCommandEntityId,
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
                    HandleMissionReadyCommand(
                        stream,
                        peer,
                        responseMsgNoBase,
                        missionInstanceEntityId,
                        currentCoopGroupName,
                        simulationSession,
                        simulationSessionSync);
                    return;

                case MissionCommandKind.LeaveMission:
                    HandleLeaveMissionCommand(
                        stream,
                        peer,
                        responseMsgNoBase,
                        gameworldEntityId,
                        missionInstanceEntityId,
                        missionCommandEntityId,
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
                        activeIdentityHash,
                        activeIdentityGuid,
                        activeCareerIndex,
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
                        activeIdentityHash,
                        activeIdentityGuid,
                        activeCareerIndex,
                        currentCoopGroupName,
                        simulationSession,
                        simulationSessionSync);
                    return;
            }
        }

        private void HandleMissionReadyCommand(
            NetworkStream stream,
            string peer,
            ulong responseMsgNoBase,
            ulong missionInstanceEntityId,
            string currentCoopGroupName,
            ServerSimulationSession simulationSession,
            object simulationSessionSync)
        {
            if (simulationSession == null)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-ready",
                    peer = peer,
                    status = "ignored-no-session",
                    coopGroup = currentCoopGroupName,
                });
                return;
            }

            if (!IsNullOrWhiteSpace(currentCoopGroupName))
            {
                HandleCoopMissionReadyCommand(stream, peer, responseMsgNoBase, missionInstanceEntityId, currentCoopGroupName, simulationSession, simulationSessionSync);
                return;
            }

            var started = false;
            try
            {
                if (simulationSessionSync != null)
                {
                    lock (simulationSessionSync)
                    {
                        started = simulationSession.StartMissionPlay();
                    }
                }
                else
                {
                    started = simulationSession.StartMissionPlay();
                }
            }
            catch (Exception ex)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-ready",
                    peer = peer,
                    status = "failed",
                    coopGroup = currentCoopGroupName,
                    message = ex.Message,
                });
                return;
            }

            if (!started)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-ready",
                    peer = peer,
                    status = simulationSession.IsMissionStarted ? "already-started" : "waiting",
                    coopGroup = currentCoopGroupName,
                });
                return;
            }

            MissionRuntimeRegistry.MarkSoloMissionStarted(peer);
            SendMissionStartForClients(stream, peer, responseMsgNoBase, missionInstanceEntityId, "sent MissionInstanceCommunicationObject StartMissionForClients");
        }

        private void HandleCoopMissionReadyCommand(
            NetworkStream stream,
            string peer,
            ulong responseMsgNoBase,
            ulong missionInstanceEntityId,
            string currentCoopGroupName,
            ServerSimulationSession simulationSession,
            object simulationSessionSync)
        {
            CoopMissionSessionState session;
            int readyCount;
            int expectedCount;
            int participantCount;
            var shouldStart = TryMarkCoopMissionReadyAndCheckAllReady(currentCoopGroupName, peer, out session, out readyCount, out expectedCount, out participantCount);

            if (!shouldStart)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-ready",
                    peer = peer,
                    status = simulationSession.IsMissionStarted ? "already-started" : "waiting-for-other-clients",
                    coopGroup = currentCoopGroupName,
                    readyCount = readyCount,
                    expectedCount = expectedCount,
                    participantCount = participantCount,
                });
                return;
            }

            var started = false;
            try
            {
                if (simulationSessionSync != null)
                {
                    lock (simulationSessionSync)
                    {
                        started = simulationSession.StartMissionPlay();
                    }
                }
                else
                {
                    started = simulationSession.StartMissionPlay();
                }
            }
            catch (Exception ex)
            {
                if (session != null)
                {
                    lock (session.SyncRoot)
                    {
                        session.StartMissionForClientsSent = false;
                    }
                }

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-ready",
                    peer = peer,
                    status = "failed",
                    coopGroup = currentCoopGroupName,
                    message = ex.Message,
                });
                return;
            }

            if (!started)
            {
                return;
            }

            MissionRuntimeRegistry.MarkCoopMissionStarted(currentCoopGroupName);
            var frame = BuildMissionStartForClientsFrame(responseMsgNoBase, missionInstanceEntityId);
            SendRawFrame(stream, peer, frame, "sent MissionInstanceCommunicationObject StartMissionForClients (coop)");
            BroadcastToCoopMissionPeers(currentCoopGroupName, peer, frame, "sent MissionInstanceCommunicationObject StartMissionForClients (coop bcast)");
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
            string activeIdentityHash,
            Guid activeIdentityGuid,
            int activeCareerIndex,
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

            var shouldBroadcast = true;
            IList<ServerSimulationSession.AiTurnAction> aiActions = null;
            if (simulationSession != null)
            {
                try
                {
                    if (simulationSessionSync != null)
                    {
                        lock (simulationSessionSync)
                        {
                            shouldBroadcast = simulationSession.ExecuteFollowPath(request.AgentId.Value, request.TargetX.Value, request.TargetY.Value);
                            if (shouldBroadcast)
                            {
                                aiActions = simulationSession.SkipAiTurnsIfNeeded();
                            }
                        }
                    }
                    else
                    {
                        shouldBroadcast = simulationSession.ExecuteFollowPath(request.AgentId.Value, request.TargetX.Value, request.TargetY.Value);
                        if (shouldBroadcast)
                        {
                            aiActions = simulationSession.SkipAiTurnsIfNeeded();
                        }
                    }
                }
                catch (Exception ex)
                {
                    shouldBroadcast = false;
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
            }

            if (!shouldBroadcast)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-command-suppressed",
                    peer = peer,
                    cmd = "FollowPath",
                    agentId = request.AgentId.Value,
                    targetX = request.TargetX.Value,
                    targetY = request.TargetY.Value,
                    reason = "authoritative-sim-did-not-accept-command",
                });
                return;
            }

            var followPathPayload = Concat(
                BitConverter.GetBytes(request.AgentId.Value),
                BitConverter.GetBytes(request.TargetX.Value),
                BitConverter.GetBytes(request.TargetY.Value));
            var followPathCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 1, followPathPayload), responseMsgNoBase + 1);

            SendRawFrame(stream, peer, PrefixLength(followPathCore), "echoed GameworldCommunicationObject FollowPath from MissionCommand");
            BroadcastToCoopMissionPeers(currentCoopGroupName, peer, PrefixLength(followPathCore), "echoed GameworldCommunicationObject FollowPath from MissionCommand (coop bcast)");

            TryBroadcastAiTurnActions(stream, peer, currentCoopGroupName, gameworldEntityId, responseMsgNoBase + 3, aiActions, simulationSession);
            SendPendingLootPreviews(simulationSession, stream, peer, responseMsgNoBase + 900, currentCoopGroupName, activeIdentityHash, activeIdentityGuid, activeCareerIndex);
        }

        private void HandleActivateActiveSkillMissionCommand(
            NetworkStream stream,
            string peer,
            ParsedMissionCommandRequest request,
            ulong responseMsgNoBase,
            ulong gameworldEntityId,
            ulong gameClientEntityId,
            string activeIdentityHash,
            Guid activeIdentityGuid,
            int activeCareerIndex,
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

            var fallbackSeeds = AllocateMissionSeeds("mission-command-fallback", peer, null, currentCoopGroupName);
            var seed0 = fallbackSeeds.Seed0;
            var seed1 = fallbackSeeds.Seed1;
            var seed2 = fallbackSeeds.Seed2;
            var seed3 = fallbackSeeds.Seed3;
            var seedPackage = new Cliffhanger.SRO.ServerClientCommons.Gameworld.Communication.SeedPackage(seed0, seed1, seed2, seed3);
            var shouldBroadcast = true;
            IList<ServerSimulationSession.AiTurnAction> aiActions = null;

            if (simulationSession != null)
            {
                try
                {
                    if (simulationSessionSync != null)
                    {
                        lock (simulationSessionSync)
                        {
                            seedPackage = simulationSession.CreateSeedPackage();
                            shouldBroadcast = simulationSession.ExecuteActivateSkill(
                                request.WeaponIndex.Value,
                                request.SkillIndex.Value,
                                request.SkillId.Value,
                                request.AgentId.Value,
                                request.TargetX.Value,
                                request.TargetY.Value,
                                seedPackage);
                            if (shouldBroadcast)
                            {
                                aiActions = simulationSession.SkipAiTurnsIfNeeded();
                            }
                        }
                    }
                    else
                    {
                        seedPackage = simulationSession.CreateSeedPackage();
                        shouldBroadcast = simulationSession.ExecuteActivateSkill(
                            request.WeaponIndex.Value,
                            request.SkillIndex.Value,
                            request.SkillId.Value,
                            request.AgentId.Value,
                            request.TargetX.Value,
                            request.TargetY.Value,
                            seedPackage);
                        if (shouldBroadcast)
                        {
                            aiActions = simulationSession.SkipAiTurnsIfNeeded();
                        }
                    }
                }
                catch (Exception ex)
                {
                    shouldBroadcast = false;
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

            if (!shouldBroadcast)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-command-suppressed",
                    peer = peer,
                    cmd = "ActivateActiveSkill",
                    skillId = request.SkillId.Value,
                    agentId = request.AgentId.Value,
                    targetX = request.TargetX.Value,
                    targetY = request.TargetY.Value,
                    reason = "authoritative-sim-did-not-accept-command",
                });
                return;
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

            TryBroadcastAiTurnActions(stream, peer, currentCoopGroupName, gameworldEntityId, responseMsgNoBase + 3, aiActions, simulationSession);
            SendPendingLootPreviews(simulationSession, stream, peer, responseMsgNoBase + 950, currentCoopGroupName, activeIdentityHash, activeIdentityGuid, activeCareerIndex);
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
            IList<ServerSimulationSession.AiTurnAction> aiActions,
            ServerSimulationSession simulationSession)
        {
            if (aiActions == null || aiActions.Count == 0)
            {
                return;
            }

            try
            {
                LogMissionReplicationBatch(peer, currentCoopGroupName, aiActions, simulationSession);

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
                    else if (aiAction.SkillId == ServerSimulationSession.EndActorTurnSkillId)
                    {
                        SendRawFrame(stream, peer, PrefixLength(activateCore), "sim: auto-ended AI actor turn (agentId=" + aiAction.AgentId + ")");
                        BroadcastToCoopMissionPeers(currentCoopGroupName, peer, PrefixLength(activateCore), "sim: auto-ended AI actor turn (coop bcast) (agentId=" + aiAction.AgentId + ")");
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

        private void LogMissionReplicationBatch(
            string peer,
            string currentCoopGroupName,
            IList<ServerSimulationSession.AiTurnAction> aiActions,
            ServerSimulationSession simulationSession)
        {
            if (simulationSession == null || aiActions == null || aiActions.Count == 0)
            {
                return;
            }

            try
            {
                var entityIds = new List<int>(aiActions.Count);
                var actionKinds = new List<string>(aiActions.Count);
                var skillIds = new List<int>(aiActions.Count);

                for (var i = 0; i < aiActions.Count; i++)
                {
                    var aiAction = aiActions[i];
                    if (aiAction == null)
                    {
                        continue;
                    }

                    entityIds.Add(aiAction.AgentId);
                    actionKinds.Add(aiAction.Kind.ToString());
                    skillIds.Add(aiAction.SkillId);
                }

                var snapshots = simulationSession.DescribeEntitiesForReplication(entityIds)
                    .Where(snapshot => snapshot != null && snapshot.HasSpawnInfo)
                    .ToArray();

                if (snapshots.Length == 0)
                {
                    return;
                }

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-replication-batch",
                    peer = peer,
                    coopGroup = currentCoopGroupName,
                    actionCount = aiActions.Count,
                    actionKinds = actionKinds.ToArray(),
                    skillIds = skillIds.ToArray(),
                    protocolFramesImplemented = new[] { "GameworldFieldEvent:FollowPath", "GameworldFieldEvent:ActivateActiveSkill" },
                    explicitDynamicEntityIntroductionsImplemented = false,
                    explicitDynamicEntityHealthFramesImplemented = false,
                    explicitDynamicEntityRemovalFramesImplemented = false,
                    spawnedEntities = snapshots,
                });
            }
            catch
            {
            }
        }

        private void HandleLeaveMissionCommand(
            NetworkStream stream,
            string peer,
            ulong responseMsgNoBase,
            ulong gameworldEntityId,
            ulong missionInstanceEntityId,
            ulong missionCommandEntityId,
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
            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "mission-party-transition-start",
                peer = peer,
                coopGroupName = currentCoopGroupName,
                participantId = gameClientEntityId,
                missionMapName = currentMissionMapName,
                hasSimulationSession = simulationSession != null,
            });

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

            var coopGroupNameForLeave = currentCoopGroupName;
            string coopSessionMapName = null;
            if (!IsNullOrWhiteSpace(coopGroupNameForLeave))
            {
                try
                {
                    lock (_coopMissionLock)
                    {
                        CoopMissionSessionState coopSession;
                        if (_coopMissionSessions.TryGetValue(coopGroupNameForLeave, out coopSession)
                            && coopSession != null
                            && !IsNullOrWhiteSpace(coopSession.MapName))
                        {
                            coopSessionMapName = coopSession.MapName;
                        }
                    }
                }
                catch
                {
                }
            }

            var progressionMissionMapName = currentMissionMapName;
            if (!IsNullOrWhiteSpace(coopSessionMapName))
            {
                if (!IsNullOrWhiteSpace(currentMissionMapName)
                    && !string.Equals(currentMissionMapName, coopSessionMapName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "mission-progression-map-mismatch",
                        peer = peer,
                        coopGroupName = coopGroupNameForLeave,
                        currentMissionMapName = currentMissionMapName,
                        coopSessionMapName = coopSessionMapName,
                    });
                }

                progressionMissionMapName = coopSessionMapName;
            }

            var isVictory = string.Equals(missionOutcome, "Victory", StringComparison.OrdinalIgnoreCase);
            var completedMapName = !IsNullOrWhiteSpace(progressionMissionMapName) ? progressionMissionMapName : "1_010_Prologue";
            var isRepeatableMission = IsRepeatableMission(completedMapName);
            var missionStateAfterLeave = StoryMissionstate.ReadyToReceiveRewards.ToString();
            var missionStatePersisted = false;

            if (!leavingMidMission && isVictory && _userStore != null)
            {
                try
                {
                    var progressSlot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                    if (progressSlot != null)
                    {
                        if (IsNullOrWhiteSpace(progressionMissionMapName))
                        {
                            _logger.Log(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "mission-progression-skipped",
                                peer = peer,
                                reason = "missing-mission-map",
                                currentMissionMapName = currentMissionMapName,
                                coopSessionMapName = coopSessionMapName,
                                outcome = missionOutcome,
                            });
                        }
                        else
                        {
                            var storyStateUpdate = _storyProgressionService.ApplyMissionState(progressSlot, "Main Campaign", progressionMissionMapName, StoryMissionstate.ReadyToReceiveRewards);
                            if (storyStateUpdate.Accepted)
                            {
                                missionStatePersisted = true;
                                missionStateAfterLeave = storyStateUpdate.TargetState.ToString();
                                if (!isRepeatableMission)
                                {
                                    completedStoryMissions.Add(progressionMissionMapName);
                                }

                                if (storyStateUpdate.Persisted)
                                {
                                    _userStore.UpsertCareer(activeIdentityHash, progressSlot);
                                }
                            }
                            else
                            {
                                _logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "mission-progression-skipped",
                                    peer = peer,
                                    reason = "invalid-state-transition",
                                    mission = progressionMissionMapName,
                                    previousState = storyStateUpdate.PreviousState.ToString(),
                                    targetState = storyStateUpdate.TargetState.ToString(),
                                    outcome = missionOutcome,
                                });
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            CareerSlot rewardSlot = null;
            if (_userStore != null)
            {
                try
                {
                    rewardSlot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                }
                catch
                {
                    rewardSlot = null;
                }
            }

            if (!IsNullOrWhiteSpace(coopGroupNameForLeave))
            {
                UnregisterCoopMissionParticipant(coopGroupNameForLeave, peer, false);
            }

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
                    var participantKey = !IsNullOrWhiteSpace(activeIdentityHash)
                        ? activeIdentityHash + ":" + activeCareerIndex.ToString(CultureInfo.InvariantCulture)
                        : activeIdentityGuid.ToString() + ":" + activeCareerIndex.ToString(CultureInfo.InvariantCulture);

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
                                bool already;
                                if (coopSession.LootAppliedToParticipants.TryGetValue(participantKey, out already) && already)
                                {
                                    coopLootAppliedAlready = true;
                                    grants = new LocalMissionLootController.LootGrant[0];
                                }
                                else
                                {
                                    coopSession.LootAppliedToParticipants[participantKey] = true;
                                    grants = simulationSession.ResolvePendingLootForParticipant(participantKey, _userStore, activeIdentityGuid, rewardSlot);
                                }
                            }
                        }
                    }

                    if (grants == null)
                    {
                        grants = simulationSession.ResolvePendingLootForParticipant(participantKey, _userStore, activeIdentityGuid, rewardSlot);
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
                            type = "mission-loot-grants-resolved",
                            peer = peer,
                            mapName = completedMapName,
                            participantKey = participantKey,
                            coopGroupName = currentCoopGroupName,
                            grants = grants.Length,
                            itemIds = lootItemIds,
                        });

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

            var resolvedMissionReward = !leavingMidMission
                ? _missionRewardService.ResolveMissionReward("Rewards", completedMapName, missionOutcome)
                : new MissionReward
                {
                    GrantedUnlocks = new string[0],
                    EarnedCurrencies = new CurrencyReward[0],
                    ItemChanges = new ItemChange[0],
                };

            if (lootItemChanges != null && lootItemChanges.Count > 0)
            {
                resolvedMissionReward = _missionRewardService.MergeRewardItemChanges(resolvedMissionReward, lootItemChanges);
            }

            var deactivatedUnlocks = new string[0];
            if (!leavingMidMission && isVictory)
            {
                deactivatedUnlocks = _missionRewardService.ResolveUnlockDeactivationsOnVictory(completedMapName);
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

            if (_userStore != null)
            {
                var rewardApplication = rewardSlot != null
                    ? _missionRewardService.Apply(rewardSlot, resolvedMissionReward, deactivatedUnlocks)
                    : new MissionRewardApplicationResult();

                if (rewardSlot != null && rewardApplication.Persisted)
                {
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
                        karmaDelta = rewardApplication.KarmaAfter - rewardApplication.KarmaBefore,
                        karmaTotal = rewardApplication.KarmaAfter,
                        nuyenDelta = rewardApplication.NuyenAfter - rewardApplication.NuyenBefore,
                        nuyenTotal = rewardApplication.NuyenAfter,
                        lootItemChanges = rewardApplication.AppliedItemChangeCount,
                        lootItemsApplied = rewardApplication.AppliedItemChangeCount,
                        grantedUnlocks = rewardApplication.AppliedGrantedUnlocks,
                        deactivatedUnlocks = rewardApplication.AppliedDeactivatedUnlocks,
                        careerIndex = activeCareerIndex,
                    });

                    if (rewardApplication.ShouldNotifyClient)
                    {
                        var lootPreviewCount = SendLootPreviewItems(stream, peer, responseMsgNoBase + 5, lootItemChanges, "after LeaveMission");
                        SendMissionReward(stream, peer, responseMsgNoBase + 6 + (ulong)lootPreviewCount, rewardApplication.TransportReward, "after LeaveMission");
                    }

                    if (rewardApplication.AppliedDeactivatedUnlocks.Length > 0)
                    {
                        try
                        {
                            SendUnlocksChanged(stream, peer, responseMsgNoBase + 20, new string[0], rewardApplication.AppliedDeactivatedUnlocks, "after LeaveMission");
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (!leavingMidMission && missionStatePersisted)
            {
                try
                {
                    var storyProgressChangeJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.MissionStateChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"Main Campaign\",\"Mission\":\"" + completedMapName + "\",\"NewState\":\"" + missionStateAfterLeave + "\"}";
                    var storyProgressChangePayload = BuildUtf16StringPayload(storyProgressChangeJson);
                    var storyProgressChangeCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, storyProgressChangePayload), responseMsgNoBase + 21);
                    SendRawFrame(stream, peer, PrefixLength(storyProgressChangeCore), "sent MetaGameplayCommunicationObject StoryprogressChanged (MissionStateChange " + missionStateAfterLeave + ") after LeaveMission");
                }
                catch
                {
                }
            }

            if (_userStore != null)
            {
                try
                {
                    var slotForSnapshot = rewardSlot ?? (!IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null);

                    if (slotForSnapshot != null)
                    {
                        var snapshotCharacterIdentifier = !IsNullOrWhiteSpace(slotForSnapshot.CharacterIdentifier)
                            ? slotForSnapshot.CharacterIdentifier
                            : (activeIdentityGuid.ToString() + ":" + activeCareerIndex.ToString(CultureInfo.InvariantCulture));
                        RetireDuplicateHubSessionForCharacter(peer, snapshotCharacterIdentifier, "leave-mission-hub-refresh-pre-transition");

                        string resolvedHubInstanceId;
                        PortedHubInstance resolvedHubInstance;
                        cachedHubStatePayload = BuildPortedHubStatePayloadForSlot(
                            slotForSnapshot,
                            activeIdentityGuid,
                            activeCareerIndex,
                            false,
                            null,
                            out resolvedHubInstanceId,
                            out resolvedHubInstance);
                    }
                }
                catch
                {
                }
            }

            var leavePayload = BitConverter.GetBytes(participantId);
            var leaveCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 3, leavePayload), responseMsgNoBase + 13);
            SendRawFrame(stream, peer, PrefixLength(leaveCore), "sent GameworldCommunicationObject LeaveMission (participantId=" + participantId + ")");
            if (!IsNullOrWhiteSpace(coopGroupNameForLeave))
            {
                // Mirror SRO mission-side leave signaling so remaining coop clients get onLeaveMission for the departed participant.
                BroadcastToCoopMissionPeers(coopGroupNameForLeave, peer, PrefixLength(leaveCore), "sent GameworldCommunicationObject LeaveMission (coop bcast, participantId=" + participantId + ")");
            }

            var stopCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameworldEntityId, 0, new byte[0]), responseMsgNoBase + 14);
            SendRawFrame(stream, peer, PrefixLength(stopCore), "sent GameworldCommunicationObject Stop after LeaveMission");

            var missionCommandUnsubscribeCore = BuildCoreDirectSystem(1, BuildApUnsubscribeRecursive(missionCommandEntityId), responseMsgNoBase + 15);
            SendRawFrame(stream, peer, PrefixLength(missionCommandUnsubscribeCore), "sent unsubscribe-recursive for mission command communication object");

            var missionInstanceUnsubscribeCore = BuildCoreDirectSystem(1, BuildApUnsubscribeRecursive(missionInstanceEntityId), responseMsgNoBase + 16);
            SendRawFrame(stream, peer, PrefixLength(missionInstanceUnsubscribeCore), "sent unsubscribe-recursive for mission instance communication object");

            var gameworldUnsubscribeCore = BuildCoreDirectSystem(1, BuildApUnsubscribeRecursive(gameworldEntityId), responseMsgNoBase + 17);
            SendRawFrame(stream, peer, PrefixLength(gameworldUnsubscribeCore), "sent unsubscribe-recursive for gameworld communication object");

            if (simulationSession == null)
            {
                return;
            }

            if (!IsNullOrWhiteSpace(coopGroupNameForLeave))
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-party-transition-end",
                    peer = peer,
                    coopGroupName = coopGroupNameForLeave,
                    participantId = participantId,
                    lifecycle = "coop-unregister",
                });

                currentCoopGroupName = null;
                simulationSession = null;
                simulationSessionSync = null;
                return;
            }

            try
            {
                StopAndForgetSoloMission(peer, "leave-mission");
            }
            catch
            {
            }

            simulationSession = null;
            simulationSessionSync = null;

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "mission-party-transition-end",
                peer = peer,
                coopGroupName = currentCoopGroupName,
                participantId = participantId,
                lifecycle = "solo-stop",
            });
        }
    }
}