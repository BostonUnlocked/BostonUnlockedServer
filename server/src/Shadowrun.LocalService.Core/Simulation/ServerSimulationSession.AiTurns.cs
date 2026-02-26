using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.CommandProcessing;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Commands;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Communication;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Locomotion;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.RandomNumbers;
using SRO.Core.Compatibility.Math;
using SRO.Core.Compatibility.Utilities;
using Shadowrun.LocalService.Core.AILogic;

namespace Shadowrun.LocalService.Core.Simulation
{
    public sealed partial class ServerSimulationSession
    {
        public IList<AiTurnAction> SkipAiTurnsIfNeeded()
        {
            var actions = new List<AiTurnAction>();

            // Safety: avoid infinite loops if the sim gets into a bad state.
            for (var i = 0; i < 64; i++)
            {
                if (_simulation.IsMissionStopped)
                {
                    break;
                }

                var team = _turnObserver.CurrentTeam;
                if (team == null)
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "sim",
                        peer = _peer,
                        action = "skip-ai",
                        status = "no-current-team",
                    });
                    break;
                }

                if (!team.AIControlled)
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "sim",
                        peer = _peer,
                        action = "skip-ai",
                        status = "current-team-not-ai",
                        teamId = team.ID,
                    });
                    break;
                }

                AiTurnAction action;
                if (!TryExecuteAiTurnStep(team, out action))
                {
                    break;
                }

                actions.Add(action);
            }

            if (actions.Count > 0)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    action = "skip-ai",
                    count = actions.Count,
                    agents = actions.Select(a => a.AgentId).ToArray(),
                });
            }

            return actions;
        }

        private bool TryExecuteAiTurnStep(Team team, out AiTurnAction action)
        {
            action = null;

            // Choose a valid activatable member; never guess IDs.
            if (_turnObserver.CurrentActivatableMembers == null || _turnObserver.CurrentActivatableMembers.Length == 0)
            {
                // No-one can act, simulation should advance on its own. If it doesn't, break.
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    action = "skip-ai",
                    status = "no-activatable-members",
                    teamId = team.ID,
                });
                return false;
            }

            var activatableMembers = _turnObserver.CurrentActivatableMembers;
            var agent = activatableMembers[0];
            var hasCombatEligibleAgent = false;
            string inactiveSpawnManagerTag = null;
            for (var memberIndex = 0; memberIndex < activatableMembers.Length; memberIndex++)
            {
                var candidate = activatableMembers[memberIndex];
                string candidateSpawnManagerTag;
                if (IsAgentEligibleForCombatAction(candidate, out candidateSpawnManagerTag))
                {
                    agent = candidate;
                    hasCombatEligibleAgent = true;
                    inactiveSpawnManagerTag = null;
                    break;
                }

                if (!string.IsNullOrEmpty(candidateSpawnManagerTag)
                    && _encounterActivationTracker != null
                    && _encounterActivationTracker.TryEngageSpawnTagFromCurrentPlayerVisibility(candidateSpawnManagerTag))
                {
                    agent = candidate;
                    hasCombatEligibleAgent = true;
                    inactiveSpawnManagerTag = null;
                    break;
                }

                if (inactiveSpawnManagerTag == null && !string.IsNullOrEmpty(candidateSpawnManagerTag))
                {
                    inactiveSpawnManagerTag = candidateSpawnManagerTag;
                }
            }

            var forceEndTurnForInactiveGroup = !hasCombatEligibleAgent;
            if (forceEndTurnForInactiveGroup)
            {
                var engagedTagSnapshot = _encounterActivationTracker != null
                    ? _encounterActivationTracker.GetEngagedTagSnapshot()
                    : new string[0];

                var activatableDiagnostics = activatableMembers
                    .Select(member =>
                    {
                        string memberSpawnManagerTag;
                        var eligible = IsAgentEligibleForCombatAction(member, out memberSpawnManagerTag);
                        return new
                        {
                            entityId = member != null ? (int?)member.Id : null,
                            spawnManagerTag = memberSpawnManagerTag,
                            eligible = eligible,
                        };
                    })
                    .ToArray();

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    action = "skip-ai",
                    status = "no-engaged-activatable-members",
                    teamId = team.ID,
                    activatableCount = activatableMembers.Length,
                    inactiveSpawnManagerTag = inactiveSpawnManagerTag,
                    engagedTagCount = engagedTagSnapshot.Length,
                    engagedTags = engagedTagSnapshot,
                    activatableDiagnostics = activatableDiagnostics,
                });
            }

            var targetPos = TryGetAgentGridPositionOrDefault(agent);
            var commandName = "AI.EndTeamTurn";

            ulong desiredSkill = 0UL;
            int desiredWeaponIndex = 0;
            int desiredSkillIndex = 0;
            string decisionNote = null;
            string debugStage = null;
            string debugRotationType = null;
            int? debugRotationCount = null;
            ulong debugRawSelection = 0UL;
            ulong debugResolvedActivityId = 0UL;
            bool? debugHasAiConfig = null;
            bool? debugHasLoadout = null;
            int? debugSelectedWeaponIndex = null;
            int? debugSelectedWeaponSkillCount = null;
            int? debugPreferredEnemyId = null;
            int? debugChosenEnemyId = null;
            int? debugChosenEnemyTeamId = null;
            bool? debugChosenEnemyTeamAi = null;
            ulong? debugChosenEnemyControlPlayerId = null;
            bool? debugChosenEnemyControlAi = null;
            bool? debugChosenEnemyIsPlayersPlayerCharacter = null;
            bool? debugChosenEnemyInteractiveObject = null;
            string debugEnemyPick = null;
            string debugEnemyReason = null;
            int? debugEnemyX = null;
            int? debugEnemyY = null;
            int? debugEnemyCandidateCount = null;
            int? debugReachableCellCount = null;
            int? debugReducingCellCount = null;
            bool? debugAvoidedImmediateBacktrack = null;
            int? debugCurrentDistToEnemy = null;
            int? debugChosenMoveDistToEnemy = null;
            float? debugChosenMoveDefensiveCover = null;
            float? debugChosenMoveTargetCover = null;
            float? debugChosenMoveScore = null;
            float? debugChosenMoveChanceToHit = null;
            bool? debugChosenMoveWithinWalkRange = null;
            int? debugProfileRange = null;
            int? debugShotDistanceToTarget = null;
            float? debugShotChanceToHit = null;
            if (forceEndTurnForInactiveGroup)
            {
                decisionNote = "inactive-group";
                debugStage = "inactive-group";
            }
            else if (_enableAiLogic && _aiDecisionEngine != null)
            {
                var foundActionableDecision = false;

                for (var memberIndex = 0; memberIndex < activatableMembers.Length; memberIndex++)
                {
                    var candidate = activatableMembers[memberIndex];
                    string candidateSpawnManagerTag;
                    var candidateEligible = IsAgentEligibleForCombatAction(candidate, out candidateSpawnManagerTag);
                    if (!candidateEligible)
                    {
                        if (string.IsNullOrEmpty(candidateSpawnManagerTag)
                            || _encounterActivationTracker == null
                            || !_encounterActivationTracker.TryEngageSpawnTagFromCurrentPlayerVisibility(candidateSpawnManagerTag))
                        {
                            continue;
                        }
                    }

                    agent = candidate;

                    try
                    {
                        var decision = _aiDecisionEngine.Decide(agent, _gameworld);
                        if (decision != null && decision.HasAction && decision.IsMove)
                        {
                            commandName = "AI.Move";
                            decisionNote = "move";
                            targetPos = decision.MoveTargetPosition;

                            debugStage = decision.DebugStage;
                            debugRotationType = decision.DebugRotationType;
                            debugRotationCount = decision.DebugRotationCount;
                            debugRawSelection = decision.DebugRawSelection;
                            debugResolvedActivityId = decision.DebugResolvedActivityId;
                            debugHasAiConfig = decision.DebugHasAiConfig;
                            debugHasLoadout = decision.DebugHasLoadout;
                            debugSelectedWeaponIndex = decision.DebugSelectedWeaponIndex;
                            debugSelectedWeaponSkillCount = decision.DebugSelectedWeaponSkillCount;

                            debugPreferredEnemyId = decision.DebugPreferredEnemyId;
                            debugChosenEnemyId = decision.DebugChosenEnemyId;
                            debugChosenEnemyTeamId = decision.DebugChosenEnemyTeamId;
                            debugChosenEnemyTeamAi = decision.DebugChosenEnemyTeamAi;
                            debugChosenEnemyControlPlayerId = decision.DebugChosenEnemyControlPlayerId;
                            debugChosenEnemyControlAi = decision.DebugChosenEnemyControlAi;
                            debugChosenEnemyIsPlayersPlayerCharacter = decision.DebugChosenEnemyIsPlayersPlayerCharacter;
                            debugChosenEnemyInteractiveObject = decision.DebugChosenEnemyInteractiveObject;
                            debugEnemyPick = decision.DebugEnemyPick;
                            debugEnemyReason = decision.DebugEnemyReason;
                            debugEnemyX = decision.DebugEnemyX;
                            debugEnemyY = decision.DebugEnemyY;
                            debugEnemyCandidateCount = decision.DebugEnemyCandidateCount;
                            debugReachableCellCount = decision.DebugReachableCellCount;
                            debugReducingCellCount = decision.DebugReducingCellCount;
                            debugAvoidedImmediateBacktrack = decision.DebugAvoidedImmediateBacktrack;
                            debugCurrentDistToEnemy = decision.DebugCurrentDistToEnemy;
                            debugChosenMoveDistToEnemy = decision.DebugChosenMoveDistToEnemy;
                            debugChosenMoveDefensiveCover = decision.DebugChosenMoveDefensiveCover;
                            debugChosenMoveTargetCover = decision.DebugChosenMoveTargetCover;
                            debugChosenMoveScore = decision.DebugChosenMoveScore;
                            debugChosenMoveChanceToHit = decision.DebugChosenMoveChanceToHit;
                            debugChosenMoveWithinWalkRange = decision.DebugChosenMoveWithinWalkRange;
                            debugProfileRange = decision.DebugProfileRange;
                            debugShotDistanceToTarget = decision.DebugShotDistanceToTarget;
                            debugShotChanceToHit = decision.DebugShotChanceToHit;

                            foundActionableDecision = true;
                            break;
                        }

                        if (decision != null && decision.HasAction && decision.SkillId != 0UL)
                        {
                            desiredSkill = decision.SkillId;
                            commandName = "AI.Decision";
                            decisionNote = "ok";

                            if (decision.WeaponIndex.HasValue)
                            {
                                desiredWeaponIndex = decision.WeaponIndex.Value;
                            }
                            if (decision.SkillIndex.HasValue)
                            {
                                desiredSkillIndex = decision.SkillIndex.Value;
                            }

                            debugStage = decision.DebugStage;
                            debugRotationType = decision.DebugRotationType;
                            debugRotationCount = decision.DebugRotationCount;
                            debugRawSelection = decision.DebugRawSelection;
                            debugResolvedActivityId = decision.DebugResolvedActivityId;
                            debugHasAiConfig = decision.DebugHasAiConfig;
                            debugHasLoadout = decision.DebugHasLoadout;
                            debugSelectedWeaponIndex = decision.DebugSelectedWeaponIndex;
                            debugSelectedWeaponSkillCount = decision.DebugSelectedWeaponSkillCount;

                            debugPreferredEnemyId = decision.DebugPreferredEnemyId;
                            debugChosenEnemyId = decision.DebugChosenEnemyId;
                            debugChosenEnemyTeamId = decision.DebugChosenEnemyTeamId;
                            debugChosenEnemyTeamAi = decision.DebugChosenEnemyTeamAi;
                            debugChosenEnemyControlPlayerId = decision.DebugChosenEnemyControlPlayerId;
                            debugChosenEnemyControlAi = decision.DebugChosenEnemyControlAi;
                            debugChosenEnemyIsPlayersPlayerCharacter = decision.DebugChosenEnemyIsPlayersPlayerCharacter;
                            debugChosenEnemyInteractiveObject = decision.DebugChosenEnemyInteractiveObject;
                            debugEnemyPick = decision.DebugEnemyPick;
                            debugEnemyReason = decision.DebugEnemyReason;
                            debugEnemyX = decision.DebugEnemyX;
                            debugEnemyY = decision.DebugEnemyY;
                            debugEnemyCandidateCount = decision.DebugEnemyCandidateCount;
                            debugReachableCellCount = decision.DebugReachableCellCount;
                            debugReducingCellCount = decision.DebugReducingCellCount;
                            debugAvoidedImmediateBacktrack = decision.DebugAvoidedImmediateBacktrack;
                            debugCurrentDistToEnemy = decision.DebugCurrentDistToEnemy;
                            debugChosenMoveDistToEnemy = decision.DebugChosenMoveDistToEnemy;
                            debugChosenMoveDefensiveCover = decision.DebugChosenMoveDefensiveCover;
                            debugChosenMoveTargetCover = decision.DebugChosenMoveTargetCover;
                            debugChosenMoveScore = decision.DebugChosenMoveScore;
                            debugChosenMoveChanceToHit = decision.DebugChosenMoveChanceToHit;
                            debugChosenMoveWithinWalkRange = decision.DebugChosenMoveWithinWalkRange;
                            debugProfileRange = decision.DebugProfileRange;
                            debugShotDistanceToTarget = decision.DebugShotDistanceToTarget;
                            debugShotChanceToHit = decision.DebugShotChanceToHit;

                            if (decision.TargetEntity != null)
                            {
                                targetPos = TryGetAgentGridPositionOrDefault(decision.TargetEntity);
                            }
                            else
                            {
                                targetPos = decision.TargetPosition;
                            }

                            foundActionableDecision = true;
                            break;
                        }

                        decisionNote = decision == null ? "null-decision" : "no-action";

                        if (decision != null)
                        {
                            debugStage = decision.DebugStage;
                            debugRotationType = decision.DebugRotationType;
                            debugRotationCount = decision.DebugRotationCount;
                            debugRawSelection = decision.DebugRawSelection;
                            debugResolvedActivityId = decision.DebugResolvedActivityId;
                            debugHasAiConfig = decision.DebugHasAiConfig;
                            debugHasLoadout = decision.DebugHasLoadout;
                            debugSelectedWeaponIndex = decision.DebugSelectedWeaponIndex;
                            debugSelectedWeaponSkillCount = decision.DebugSelectedWeaponSkillCount;

                            debugPreferredEnemyId = decision.DebugPreferredEnemyId;
                            debugChosenEnemyId = decision.DebugChosenEnemyId;
                            debugChosenEnemyTeamId = decision.DebugChosenEnemyTeamId;
                            debugChosenEnemyTeamAi = decision.DebugChosenEnemyTeamAi;
                            debugChosenEnemyControlPlayerId = decision.DebugChosenEnemyControlPlayerId;
                            debugChosenEnemyControlAi = decision.DebugChosenEnemyControlAi;
                            debugChosenEnemyIsPlayersPlayerCharacter = decision.DebugChosenEnemyIsPlayersPlayerCharacter;
                            debugChosenEnemyInteractiveObject = decision.DebugChosenEnemyInteractiveObject;
                            debugEnemyPick = decision.DebugEnemyPick;
                            debugEnemyReason = decision.DebugEnemyReason;
                            debugEnemyX = decision.DebugEnemyX;
                            debugEnemyY = decision.DebugEnemyY;
                            debugEnemyCandidateCount = decision.DebugEnemyCandidateCount;
                            debugReachableCellCount = decision.DebugReachableCellCount;
                            debugReducingCellCount = decision.DebugReducingCellCount;
                            debugAvoidedImmediateBacktrack = decision.DebugAvoidedImmediateBacktrack;
                            debugCurrentDistToEnemy = decision.DebugCurrentDistToEnemy;
                            debugChosenMoveDistToEnemy = decision.DebugChosenMoveDistToEnemy;
                            debugChosenMoveDefensiveCover = decision.DebugChosenMoveDefensiveCover;
                            debugChosenMoveTargetCover = decision.DebugChosenMoveTargetCover;
                            debugChosenMoveScore = decision.DebugChosenMoveScore;
                            debugChosenMoveChanceToHit = decision.DebugChosenMoveChanceToHit;
                            debugChosenMoveWithinWalkRange = decision.DebugChosenMoveWithinWalkRange;
                            debugProfileRange = decision.DebugProfileRange;
                            debugShotDistanceToTarget = decision.DebugShotDistanceToTarget;
                            debugShotChanceToHit = decision.DebugShotChanceToHit;
                        }
                    }
                    catch
                    {
                        decisionNote = "exception";
                        debugStage = "exception";
                    }
                }

                if (!foundActionableDecision)
                {
                    desiredSkill = 0UL;
                    commandName = "AI.EndTeamTurn";
                    if (string.IsNullOrEmpty(decisionNote))
                    {
                        decisionNote = "no-action";
                        debugStage = "no-target";
                    }
                }
            }

            if (_enableAiLogic)
            {
                try
                {
                    var debugReasoning = BuildAiDecisionReasoning(
                        decisionNote,
                        commandName,
                        debugStage,
                        desiredSkill,
                        desiredWeaponIndex,
                        desiredSkillIndex,
                        targetPos,
                        debugResolvedActivityId,
                        debugRawSelection,
                        debugRotationType,
                        debugRotationCount,
                        debugChosenEnemyId,
                        debugEnemyPick,
                        debugEnemyReason,
                        debugEnemyX,
                        debugEnemyY,
                        debugEnemyCandidateCount,
                        debugReachableCellCount,
                        debugReducingCellCount,
                        debugAvoidedImmediateBacktrack,
                        debugCurrentDistToEnemy,
                        debugChosenMoveDistToEnemy,
                        debugChosenMoveDefensiveCover,
                        debugChosenMoveTargetCover,
                        debugChosenMoveScore,
                        debugChosenMoveChanceToHit,
                        debugChosenMoveWithinWalkRange,
                        debugProfileRange,
                        debugShotDistanceToTarget,
                        debugShotChanceToHit);

                    _logger.LogAi(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "ai",
                        peer = _peer,
                        teamId = team != null ? (int?)team.ID : null,
                        agentId = agent != null ? (int?)agent.Id : null,
                        decisionSkillId = desiredSkill,
                        decisionWeaponIndex = desiredWeaponIndex,
                        decisionSkillIndex = desiredSkillIndex,
                        decisionNote = decisionNote,
                        debugStage = debugStage,
                        debugRotationType = debugRotationType,
                        debugRotationCount = debugRotationCount,
                        debugRawSelection = debugRawSelection,
                        debugResolvedActivityId = debugResolvedActivityId,
                        debugHasAiConfig = debugHasAiConfig,
                        debugHasLoadout = debugHasLoadout,
                        debugSelectedWeaponIndex = debugSelectedWeaponIndex,
                        debugSelectedWeaponSkillCount = debugSelectedWeaponSkillCount,
                        debugPreferredEnemyId = debugPreferredEnemyId,
                        debugChosenEnemyId = debugChosenEnemyId,
                        debugChosenEnemyTeamId = debugChosenEnemyTeamId,
                        debugChosenEnemyTeamAi = debugChosenEnemyTeamAi,
                        debugChosenEnemyControlPlayerId = debugChosenEnemyControlPlayerId,
                        debugChosenEnemyControlAi = debugChosenEnemyControlAi,
                        debugChosenEnemyIsPlayersPlayerCharacter = debugChosenEnemyIsPlayersPlayerCharacter,
                        debugChosenEnemyInteractiveObject = debugChosenEnemyInteractiveObject,
                        debugEnemyPick = debugEnemyPick,
                        debugEnemyReason = debugEnemyReason,
                        debugEnemyX = debugEnemyX,
                        debugEnemyY = debugEnemyY,
                        debugEnemyCandidateCount = debugEnemyCandidateCount,
                        debugReachableCellCount = debugReachableCellCount,
                        debugReducingCellCount = debugReducingCellCount,
                        debugAvoidedImmediateBacktrack = debugAvoidedImmediateBacktrack,
                        debugCurrentDistToEnemy = debugCurrentDistToEnemy,
                        debugChosenMoveDistToEnemy = debugChosenMoveDistToEnemy,
                        debugChosenMoveDefensiveCover = debugChosenMoveDefensiveCover,
                        debugChosenMoveTargetCover = debugChosenMoveTargetCover,
                        debugChosenMoveScore = debugChosenMoveScore,
                        debugChosenMoveChanceToHit = debugChosenMoveChanceToHit,
                        debugChosenMoveWithinWalkRange = debugChosenMoveWithinWalkRange,
                        debugProfileRange = debugProfileRange,
                        debugShotDistanceToTarget = debugShotDistanceToTarget,
                        debugShotChanceToHit = debugShotChanceToHit,
                        inactiveSpawnManagerTag = inactiveSpawnManagerTag,
                        forceEndTurnForInactiveGroup = forceEndTurnForInactiveGroup,
                        debugReasoning = debugReasoning,
                        command = commandName,
                        targetX = targetPos.X,
                        targetY = targetPos.Y,
                    });
                }
                catch
                {
                }
            }

            var skillToUse = EndTeamTurnSkillId;
            int? skillToLog = EndTeamTurnSkillId;
            if (desiredSkill > 0UL && desiredSkill <= (ulong)int.MaxValue)
            {
                skillToUse = (int)desiredSkill;
                skillToLog = (int)desiredSkill;
            }

            var seeds = _random.CreateSeedPackage();

            ICommand cmd;
            try
            {
                if (commandName == "AI.Move")
                {
                    cmd = new FollowPathCommand(agent.Id, targetPos, _gameworld);
                }
                else
                {
                    cmd = new ActivatePositionTargetedActiveSkillCommand(
                        desiredWeaponIndex,
                        desiredSkillIndex,
                        skillToUse,
                        agent.Id,
                        targetPos,
                        _gameworld,
                        _random,
                        seeds);
                }
            }
            catch
            {
                // Fall back to end-turn if decision command construction fails.
                commandName = "AI.EndTeamTurn";
                skillToUse = EndTeamTurnSkillId;
                skillToLog = EndTeamTurnSkillId;
                cmd = new ActivatePositionTargetedActiveSkillCommand(
                    0,
                    0,
                    skillToUse,
                    agent.Id,
                    TryGetAgentGridPositionOrDefault(agent),
                    _gameworld,
                    _random,
                    seeds);
            }

            bool madeProgress;
            if (commandName == "AI.Move")
            {
                madeProgress = ExecuteCommand(cmd, commandName, null, null, null, targetPos.X, targetPos.Y);
            }
            else
            {
                madeProgress = ExecuteCommand(cmd, commandName, desiredWeaponIndex, desiredSkillIndex, skillToLog, targetPos.X, targetPos.Y);
            }

            // If the AI command was effectively a no-op (common when activity conditions fail), don't spin.
            // Instead, end the unit/team turn and move on.
            if (!madeProgress && commandName != "AI.EndTeamTurn")
            {
                var fallbackSeeds = _random.CreateSeedPackage();
                var fallbackPos = TryGetAgentGridPositionOrDefault(agent);
                var fallbackCmd = new ActivatePositionTargetedActiveSkillCommand(
                    0,
                    0,
                    EndTeamTurnSkillId,
                    agent.Id,
                    fallbackPos,
                    _gameworld,
                    _random,
                    fallbackSeeds);

                var fallbackProgress = ExecuteCommand(fallbackCmd, "AI.EndTeamTurn", 0, 0, EndTeamTurnSkillId, fallbackPos.X, fallbackPos.Y);
                if (!fallbackProgress)
                {
                    return false;
                }

                action = new AiTurnAction(
                    AiTurnActionKind.ActivateActiveSkill,
                    agent.Id,
                    0,
                    0,
                    0,
                    0,
                    EndTeamTurnSkillId,
                    fallbackSeeds);
                return true;
            }

            if (commandName == "AI.Move")
            {
                action = new AiTurnAction(
                    AiTurnActionKind.FollowPath,
                    agent.Id,
                    targetPos.X,
                    targetPos.Y,
                    0,
                    0,
                    0,
                    new SeedPackage(0u, 0u, 0u, 0u));
                return true;
            }

            action = new AiTurnAction(
                AiTurnActionKind.ActivateActiveSkill,
                agent.Id,
                targetPos.X,
                targetPos.Y,
                desiredWeaponIndex,
                desiredSkillIndex,
                skillToUse,
                seeds);
            return true;
        }

        private bool IsAgentEligibleForCombatAction(Entity agent, out string spawnManagerTag)
        {
            spawnManagerTag = null;
            if (_encounterActivationTracker == null || _gameworld == null || _gameworld.EntitySystem == null)
            {
                return true;
            }

            CharacterSpawnInfoComponent spawnInfo;
            if (!_gameworld.EntitySystem.TryGetComponent<CharacterSpawnInfoComponent>(agent, out spawnInfo))
            {
                return true;
            }

            if (spawnInfo == null || string.IsNullOrEmpty(spawnInfo.SpawnManagerTag))
            {
                return true;
            }

            spawnManagerTag = spawnInfo.SpawnManagerTag;
            return _encounterActivationTracker.IsGroupEngaged(spawnManagerTag);
        }

        private static string BuildAiDecisionReasoning(
            string decisionNote,
            string commandName,
            string debugStage,
            ulong desiredSkill,
            int desiredWeaponIndex,
            int desiredSkillIndex,
            IntVector2D targetPos,
            ulong debugResolvedActivityId,
            ulong debugRawSelection,
            string debugRotationType,
            int? debugRotationCount,
            int? debugChosenEnemyId,
            string debugEnemyPick,
            string debugEnemyReason,
            int? debugEnemyX,
            int? debugEnemyY,
            int? debugEnemyCandidateCount,
            int? debugReachableCellCount,
            int? debugReducingCellCount,
            bool? debugAvoidedImmediateBacktrack,
            int? debugCurrentDistToEnemy,
            int? debugChosenMoveDistToEnemy,
            float? debugChosenMoveDefensiveCover,
            float? debugChosenMoveTargetCover,
            float? debugChosenMoveScore,
            float? debugChosenMoveChanceToHit,
            bool? debugChosenMoveWithinWalkRange,
            int? debugProfileRange,
            int? debugShotDistanceToTarget,
            float? debugShotChanceToHit)
        {
            var sb = new StringBuilder(256);

            sb.Append("note=");
            sb.Append(string.IsNullOrEmpty(decisionNote) ? "none" : decisionNote);

            sb.Append(";command=");
            sb.Append(string.IsNullOrEmpty(commandName) ? "unknown" : commandName);

            if (!string.IsNullOrEmpty(debugStage))
            {
                sb.Append(";stage=");
                sb.Append(debugStage);
            }

            if (commandName == "AI.Move")
            {
                sb.Append(";moveTarget=(");
                sb.Append(targetPos.X);
                sb.Append(",");
                sb.Append(targetPos.Y);
                sb.Append(")");

                if (debugChosenEnemyId.HasValue)
                {
                    sb.Append(";enemyId=");
                    sb.Append(debugChosenEnemyId.Value);
                }
                if (!string.IsNullOrEmpty(debugEnemyPick))
                {
                    sb.Append(";enemyPick=");
                    sb.Append(debugEnemyPick);
                }
                if (!string.IsNullOrEmpty(debugEnemyReason))
                {
                    sb.Append(";enemyReason=");
                    sb.Append(debugEnemyReason);
                }
                if (debugEnemyX.HasValue && debugEnemyY.HasValue)
                {
                    sb.Append(";enemyPos=(");
                    sb.Append(debugEnemyX.Value);
                    sb.Append(",");
                    sb.Append(debugEnemyY.Value);
                    sb.Append(")");
                }
                if (debugCurrentDistToEnemy.HasValue)
                {
                    sb.Append(";distToEnemy=");
                    sb.Append(debugCurrentDistToEnemy.Value);
                }
                if (debugChosenMoveDistToEnemy.HasValue)
                {
                    sb.Append(";distAfterMove=");
                    sb.Append(debugChosenMoveDistToEnemy.Value);
                }
                if (debugReachableCellCount.HasValue)
                {
                    sb.Append(";reachable=");
                    sb.Append(debugReachableCellCount.Value);
                }
                if (debugReducingCellCount.HasValue)
                {
                    sb.Append(";reducing=");
                    sb.Append(debugReducingCellCount.Value);
                }
                if (debugAvoidedImmediateBacktrack.HasValue)
                {
                    sb.Append(";avoidedBacktrack=");
                    sb.Append(debugAvoidedImmediateBacktrack.Value ? "true" : "false");
                }
                if (debugChosenMoveDefensiveCover.HasValue)
                {
                    sb.Append(";defCover=");
                    sb.Append(debugChosenMoveDefensiveCover.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (debugChosenMoveTargetCover.HasValue)
                {
                    sb.Append(";targetCover=");
                    sb.Append(debugChosenMoveTargetCover.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(";targetExposure=");
                    sb.Append((1f - debugChosenMoveTargetCover.Value).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (debugChosenMoveScore.HasValue)
                {
                    sb.Append(";moveScore=");
                    sb.Append(debugChosenMoveScore.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (debugChosenMoveChanceToHit.HasValue)
                {
                    sb.Append(";offCth=");
                    sb.Append(debugChosenMoveChanceToHit.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (debugChosenMoveWithinWalkRange.HasValue)
                {
                    sb.Append(";walkMove=");
                    sb.Append(debugChosenMoveWithinWalkRange.Value ? "true" : "false");
                }
            }
            else
            {
                sb.Append(";skill=");
                sb.Append(desiredSkill);
                sb.Append(";weaponIndex=");
                sb.Append(desiredWeaponIndex);
                sb.Append(";skillIndex=");
                sb.Append(desiredSkillIndex);
                sb.Append(";target=(");
                sb.Append(targetPos.X);
                sb.Append(",");
                sb.Append(targetPos.Y);
                sb.Append(")");

                if (debugShotDistanceToTarget.HasValue)
                {
                    sb.Append(";shotDist=");
                    sb.Append(debugShotDistanceToTarget.Value);
                }
                if (debugShotChanceToHit.HasValue)
                {
                    sb.Append(";shotCth=");
                    sb.Append(debugShotChanceToHit.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            if (debugProfileRange.HasValue)
            {
                sb.Append(";profileRange=");
                sb.Append(debugProfileRange.Value);
            }

            if (debugResolvedActivityId != 0UL)
            {
                sb.Append(";resolvedActivity=");
                sb.Append(debugResolvedActivityId);
            }
            if (debugRawSelection != 0UL)
            {
                sb.Append(";rawSelection=");
                sb.Append(debugRawSelection);
            }
            if (!string.IsNullOrEmpty(debugRotationType))
            {
                sb.Append(";rotationType=");
                sb.Append(debugRotationType);
            }
            if (debugRotationCount.HasValue)
            {
                sb.Append(";rotationCount=");
                sb.Append(debugRotationCount.Value);
            }
            if (debugEnemyCandidateCount.HasValue)
            {
                sb.Append(";enemyCandidates=");
                sb.Append(debugEnemyCandidateCount.Value);
            }

            return sb.ToString();
        }
    }
}
