using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
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

            var activatableMembers = OrderByInitiative(_turnObserver.CurrentActivatableMembers);
            Entity actingEntity = null;
            PortedAiAgent actingAgent = null;
            string inactiveSpawnManagerTag = null;

            for (var memberIndex = 0; memberIndex < activatableMembers.Length; memberIndex++)
            {
                var candidate = activatableMembers[memberIndex];
                string candidateSpawnManagerTag;
                PortedAiAgent candidateAgent;
                if (_encounterActivationTracker != null
                    && _encounterActivationTracker.TryGetAiAgent(candidate, out candidateAgent, out candidateSpawnManagerTag))
                {
                    actingEntity = candidate;
                    actingAgent = candidateAgent;
                    inactiveSpawnManagerTag = candidateSpawnManagerTag;
                    break;
                }
            }

            if (actingAgent == null || actingEntity == null)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    action = "skip-ai",
                    status = "no-registered-ai-agent",
                    teamId = team.ID,
                    activatableCount = activatableMembers.Length,
                });
                return false;
            }

            if (_encounterActivationTracker != null)
            {
                _encounterActivationTracker.OnAiControlledAgentsTurn(actingEntity);
            }

            var forceEndTurnForInactiveGroup = _encounterActivationTracker != null
                && !_encounterActivationTracker.IsEntityGroupEngaged(actingEntity, out inactiveSpawnManagerTag);

            if (forceEndTurnForInactiveGroup)
            {
                var engagedTagSnapshot = _encounterActivationTracker != null
                    ? _encounterActivationTracker.GetEngagedTagSnapshot()
                    : new string[0];

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    action = "skip-ai",
                    status = "inactive-ai-group",
                    teamId = team.ID,
                    entityId = actingEntity.Id,
                    inactiveSpawnManagerTag = inactiveSpawnManagerTag,
                    engagedTagCount = engagedTagSnapshot.Length,
                    engagedTags = engagedTagSnapshot,
                });
            }

            var plan = actingAgent.Act(_aiPlanner, _gameworld, forceEndTurnForInactiveGroup);
            if (plan == null || plan.Agent == null)
            {
                return false;
            }

            var diagnostics = plan.Diagnostics ?? new AiPlanningDiagnostics();
            diagnostics.InactiveSpawnManagerTag = inactiveSpawnManagerTag;
            diagnostics.ForceEndTurnForInactiveGroup = forceEndTurnForInactiveGroup;

            if (_enableAiLogic)
            {
                try
                {
                    var debugReasoning = BuildAiDecisionReasoning(plan);

                    _logger.LogAi(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "ai",
                        peer = _peer,
                        teamId = team != null ? (int?)team.ID : null,
                        agentId = plan.Agent != null ? (int?)plan.Agent.Id : null,
                        decisionSkillId = plan.SkillId,
                        decisionWeaponIndex = plan.WeaponIndex,
                        decisionSkillIndex = plan.SkillIndex,
                        decisionNote = diagnostics.DecisionNote,
                        debugStage = diagnostics.DebugStage,
                        debugRotationType = diagnostics.DebugRotationType,
                        debugRotationCount = diagnostics.DebugRotationCount,
                        debugRawSelection = diagnostics.DebugRawSelection,
                        debugResolvedActivityId = diagnostics.DebugResolvedActivityId,
                        debugHasAiConfig = diagnostics.DebugHasAiConfig,
                        debugHasLoadout = diagnostics.DebugHasLoadout,
                        debugSelectedWeaponIndex = diagnostics.DebugSelectedWeaponIndex,
                        debugSelectedWeaponSkillCount = diagnostics.DebugSelectedWeaponSkillCount,
                        debugPreferredEnemyId = diagnostics.DebugPreferredEnemyId,
                        debugChosenEnemyId = diagnostics.DebugChosenEnemyId,
                        debugChosenEnemyTeamId = diagnostics.DebugChosenEnemyTeamId,
                        debugChosenEnemyTeamAi = diagnostics.DebugChosenEnemyTeamAi,
                        debugChosenEnemyControlPlayerId = diagnostics.DebugChosenEnemyControlPlayerId,
                        debugChosenEnemyControlAi = diagnostics.DebugChosenEnemyControlAi,
                        debugChosenEnemyIsPlayersPlayerCharacter = diagnostics.DebugChosenEnemyIsPlayersPlayerCharacter,
                        debugChosenEnemyInteractiveObject = diagnostics.DebugChosenEnemyInteractiveObject,
                        debugChosenTargetRelationship = diagnostics.DebugChosenTargetRelationship,
                        debugEnemyPick = diagnostics.DebugEnemyPick,
                        debugEnemyReason = diagnostics.DebugEnemyReason,
                        debugEnemyX = diagnostics.DebugEnemyX,
                        debugEnemyY = diagnostics.DebugEnemyY,
                        debugEnemyCandidateCount = diagnostics.DebugEnemyCandidateCount,
                        debugAttackCandidateCount = diagnostics.DebugAttackCandidateCount,
                        debugAttackEvaluatedTargetCount = diagnostics.DebugAttackEvaluatedTargetCount,
                        debugAttackUsedSelfTarget = diagnostics.DebugAttackUsedSelfTarget,
                        debugAttackFailureReason = diagnostics.DebugAttackFailureReason,
                        debugReachableCellCount = diagnostics.DebugReachableCellCount,
                        debugReducingCellCount = diagnostics.DebugReducingCellCount,
                        debugAvoidedImmediateBacktrack = diagnostics.DebugAvoidedImmediateBacktrack,
                        debugCurrentDistToEnemy = diagnostics.DebugCurrentDistToEnemy,
                        debugChosenMoveDistToEnemy = diagnostics.DebugChosenMoveDistToEnemy,
                        debugChosenMoveDefensiveCover = diagnostics.DebugChosenMoveDefensiveCover,
                        debugChosenMoveTargetCover = diagnostics.DebugChosenMoveTargetCover,
                        debugChosenMoveScore = diagnostics.DebugChosenMoveScore,
                        debugChosenMoveChanceToHit = diagnostics.DebugChosenMoveChanceToHit,
                        debugChosenMoveWithinWalkRange = diagnostics.DebugChosenMoveWithinWalkRange,
                        debugProfileRange = diagnostics.DebugProfileRange,
                        debugShotDistanceToTarget = diagnostics.DebugShotDistanceToTarget,
                        debugShotChanceToHit = diagnostics.DebugShotChanceToHit,
                        inactiveSpawnManagerTag = diagnostics.InactiveSpawnManagerTag,
                        forceEndTurnForInactiveGroup = diagnostics.ForceEndTurnForInactiveGroup,
                        debugReasoning = debugReasoning,
                        command = plan.CommandName,
                        targetX = plan.TargetPosition.X,
                        targetY = plan.TargetPosition.Y,
                    });
                }
                catch
                {
                }
            }

            var executedPlan = plan;
            var seeds = executedPlan.IsMove ? new SeedPackage(0u, 0u, 0u, 0u) : _random.CreateSeedPackage();

            ICommand cmd;
            try
            {
                if (executedPlan.IsMove)
                {
                    cmd = new FollowPathCommand(executedPlan.Agent.Id, executedPlan.TargetPosition, _gameworld);
                }
                else
                {
                    cmd = new ActivatePositionTargetedActiveSkillCommand(
                        executedPlan.WeaponIndex,
                        executedPlan.SkillIndex,
                        executedPlan.SkillId,
                        executedPlan.Agent.Id,
                        executedPlan.TargetPosition,
                        _gameworld,
                        _random,
                        seeds);
                }
            }
            catch
            {
                // Fall back to end-turn if decision command construction fails.
                executedPlan = PlannedAiAction.CreateEndTurn(plan.Agent, TryGetAgentGridPositionOrDefault(plan.Agent), "command-build-fallback", "command-build-fallback", EndActorTurnSkillId);
                seeds = _random.CreateSeedPackage();
                cmd = new ActivatePositionTargetedActiveSkillCommand(
                    0,
                    0,
                    EndActorTurnSkillId,
                    executedPlan.Agent.Id,
                    executedPlan.TargetPosition,
                    _gameworld,
                    _random,
                    seeds);
            }

            bool madeProgress;
            if (executedPlan.IsMove)
            {
                madeProgress = ExecuteCommand(cmd, executedPlan.CommandName, null, null, null, executedPlan.TargetPosition.X, executedPlan.TargetPosition.Y);
            }
            else
            {
                madeProgress = ExecuteCommand(cmd, executedPlan.CommandName, executedPlan.WeaponIndex, executedPlan.SkillIndex, executedPlan.SkillId, executedPlan.TargetPosition.X, executedPlan.TargetPosition.Y);
            }

            // If the AI command was effectively a no-op (common when activity conditions fail), don't spin.
            // Instead, end the unit/team turn and move on.
            if (!madeProgress && !executedPlan.IsEndTurn)
            {
                var fallbackSeeds = _random.CreateSeedPackage();
                var fallbackPos = TryGetAgentGridPositionOrDefault(executedPlan.Agent);
                var fallbackCmd = new ActivatePositionTargetedActiveSkillCommand(
                    0,
                    0,
                    EndTeamTurnSkillId,
                    executedPlan.Agent.Id,
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
                    executedPlan.Agent.Id,
                    0,
                    0,
                    0,
                    0,
                    EndTeamTurnSkillId,
                    fallbackSeeds);
                return true;
            }

            if (executedPlan.IsMove)
            {
                action = new AiTurnAction(
                    AiTurnActionKind.FollowPath,
                    executedPlan.Agent.Id,
                    executedPlan.TargetPosition.X,
                    executedPlan.TargetPosition.Y,
                    0,
                    0,
                    0,
                    new SeedPackage(0u, 0u, 0u, 0u));
                return true;
            }

            action = new AiTurnAction(
                AiTurnActionKind.ActivateActiveSkill,
                executedPlan.Agent.Id,
                executedPlan.TargetPosition.X,
                executedPlan.TargetPosition.Y,
                executedPlan.WeaponIndex,
                executedPlan.SkillIndex,
                executedPlan.SkillId,
                seeds);
            return true;
        }

        private Entity[] OrderByInitiative(Entity[] activatableMembers)
        {
            return (activatableMembers ?? new Entity[0])
                .Where(entity => entity != null)
                .OrderByDescending(entity => GetAiInitiative(entity))
                .ToArray();
        }

        private int GetAiInitiative(Entity entity)
        {
            if (_gameworld == null || _gameworld.EntitySystem == null || entity == null)
            {
                return 0;
            }

            AIBehaviourConfigurationComponent config;
            if (_gameworld.EntitySystem.TryGetComponent<AIBehaviourConfigurationComponent>(entity, out config) && config != null)
            {
                return config.Initiative;
            }

            return 0;
        }

        private bool IsAgentEligibleForCombatAction(Entity agent, out string spawnManagerTag)
        {
            spawnManagerTag = null;
            if (!HasHostileCombatTargetForAgent(agent))
            {
                return false;
            }

            if (_encounterActivationTracker == null || _gameworld == null || _gameworld.EntitySystem == null)
            {
                return true;
            }

            return _encounterActivationTracker.IsEntityGroupEngaged(agent, out spawnManagerTag);
        }

        private bool HasHostileCombatTargetForAgent(Entity agent)
        {
            if (_gameworld == null || _gameworld.EntitySystem == null || agent == null)
            {
                return true;
            }

            TeamComponent myTeam;
            if (!_gameworld.EntitySystem.TryGetComponent<TeamComponent>(agent, out myTeam) || myTeam == null)
            {
                return true;
            }

            TeamInfoComponent teamInfo;
            if (!_gameworld.EntitySystem.TryGetComponent<TeamInfoComponent>(EnvironmentEntity.Instance, out teamInfo) || teamInfo == null)
            {
                return true;
            }

            var hostileTeams = teamInfo.GetAllHostileTeamsFor(myTeam.TeamID);
            if (hostileTeams == null)
            {
                return false;
            }

            var hostileTeamSet = new HashSet<int>(hostileTeams);
            if (hostileTeamSet.Count == 0)
            {
                return false;
            }

            foreach (var other in _gameworld.EntitySystem.GetAllEntities())
            {
                if (other == null || other == agent)
                {
                    continue;
                }

                TeamComponent otherTeam;
                if (!_gameworld.EntitySystem.TryGetComponent<TeamComponent>(other, out otherTeam) || otherTeam == null)
                {
                    continue;
                }

                if (!hostileTeamSet.Contains(otherTeam.TeamID))
                {
                    continue;
                }

                GameplayPropertiesComponent gp;
                if (_gameworld.EntitySystem.TryGetComponent<GameplayPropertiesComponent>(other, out gp)
                    && gp != null
                    && gp.InteractiveObject)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static string BuildAiDecisionReasoning(PlannedAiAction action)
        {
            var diagnostics = action != null && action.Diagnostics != null ? action.Diagnostics : new AiPlanningDiagnostics();
            var targetPos = action != null ? action.TargetPosition : IntVector2D.Zero;
            var sb = new StringBuilder(256);

            sb.Append("note=");
            sb.Append(string.IsNullOrEmpty(diagnostics.DecisionNote) ? "none" : diagnostics.DecisionNote);

            sb.Append(";command=");
            sb.Append(action == null || string.IsNullOrEmpty(action.CommandName) ? "unknown" : action.CommandName);

            if (!string.IsNullOrEmpty(diagnostics.DebugStage))
            {
                sb.Append(";stage=");
                sb.Append(diagnostics.DebugStage);
            }

            if (action != null && action.IsMove)
            {
                sb.Append(";moveTarget=(");
                sb.Append(targetPos.X);
                sb.Append(",");
                sb.Append(targetPos.Y);
                sb.Append(")");

                if (diagnostics.DebugChosenEnemyId.HasValue)
                {
                    sb.Append(";enemyId=");
                    sb.Append(diagnostics.DebugChosenEnemyId.Value);
                }
                if (!string.IsNullOrEmpty(diagnostics.DebugEnemyPick))
                {
                    sb.Append(";enemyPick=");
                    sb.Append(diagnostics.DebugEnemyPick);
                }
                if (!string.IsNullOrEmpty(diagnostics.DebugEnemyReason))
                {
                    sb.Append(";enemyReason=");
                    sb.Append(diagnostics.DebugEnemyReason);
                }
                if (diagnostics.DebugEnemyX.HasValue && diagnostics.DebugEnemyY.HasValue)
                {
                    sb.Append(";enemyPos=(");
                    sb.Append(diagnostics.DebugEnemyX.Value);
                    sb.Append(",");
                    sb.Append(diagnostics.DebugEnemyY.Value);
                    sb.Append(")");
                }
                if (diagnostics.DebugCurrentDistToEnemy.HasValue)
                {
                    sb.Append(";distToEnemy=");
                    sb.Append(diagnostics.DebugCurrentDistToEnemy.Value);
                }
                if (diagnostics.DebugChosenMoveDistToEnemy.HasValue)
                {
                    sb.Append(";distAfterMove=");
                    sb.Append(diagnostics.DebugChosenMoveDistToEnemy.Value);
                }
                if (diagnostics.DebugReachableCellCount.HasValue)
                {
                    sb.Append(";reachable=");
                    sb.Append(diagnostics.DebugReachableCellCount.Value);
                }
                if (diagnostics.DebugReducingCellCount.HasValue)
                {
                    sb.Append(";reducing=");
                    sb.Append(diagnostics.DebugReducingCellCount.Value);
                }
                if (diagnostics.DebugAvoidedImmediateBacktrack.HasValue)
                {
                    sb.Append(";avoidedBacktrack=");
                    sb.Append(diagnostics.DebugAvoidedImmediateBacktrack.Value ? "true" : "false");
                }
                if (diagnostics.DebugChosenMoveDefensiveCover.HasValue)
                {
                    sb.Append(";defCover=");
                    sb.Append(diagnostics.DebugChosenMoveDefensiveCover.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (diagnostics.DebugChosenMoveTargetCover.HasValue)
                {
                    sb.Append(";targetCover=");
                    sb.Append(diagnostics.DebugChosenMoveTargetCover.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(";targetExposure=");
                    sb.Append((1f - diagnostics.DebugChosenMoveTargetCover.Value).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (diagnostics.DebugChosenMoveScore.HasValue)
                {
                    sb.Append(";moveScore=");
                    sb.Append(diagnostics.DebugChosenMoveScore.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (diagnostics.DebugChosenMoveChanceToHit.HasValue)
                {
                    sb.Append(";offCth=");
                    sb.Append(diagnostics.DebugChosenMoveChanceToHit.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (diagnostics.DebugChosenMoveWithinWalkRange.HasValue)
                {
                    sb.Append(";walkMove=");
                    sb.Append(diagnostics.DebugChosenMoveWithinWalkRange.Value ? "true" : "false");
                }
            }
            else
            {
                sb.Append(";skill=");
                sb.Append(action != null ? action.SkillId : 0);
                sb.Append(";weaponIndex=");
                sb.Append(action != null ? action.WeaponIndex : 0);
                sb.Append(";skillIndex=");
                sb.Append(action != null ? action.SkillIndex : 0);
                sb.Append(";target=(");
                sb.Append(targetPos.X);
                sb.Append(",");
                sb.Append(targetPos.Y);
                sb.Append(")");

                if (diagnostics.DebugShotDistanceToTarget.HasValue)
                {
                    sb.Append(";shotDist=");
                    sb.Append(diagnostics.DebugShotDistanceToTarget.Value);
                }
                if (diagnostics.DebugShotChanceToHit.HasValue)
                {
                    sb.Append(";shotCth=");
                    sb.Append(diagnostics.DebugShotChanceToHit.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (!string.IsNullOrEmpty(diagnostics.DebugChosenTargetRelationship))
                {
                    sb.Append(";targetRelation=");
                    sb.Append(diagnostics.DebugChosenTargetRelationship);
                }
                if (diagnostics.DebugAttackUsedSelfTarget.HasValue)
                {
                    sb.Append(";selfTarget=");
                    sb.Append(diagnostics.DebugAttackUsedSelfTarget.Value ? "true" : "false");
                }
                if (diagnostics.DebugAttackCandidateCount.HasValue)
                {
                    sb.Append(";attackCandidates=");
                    sb.Append(diagnostics.DebugAttackCandidateCount.Value);
                }
                if (diagnostics.DebugAttackEvaluatedTargetCount.HasValue)
                {
                    sb.Append(";attackEvaluated=");
                    sb.Append(diagnostics.DebugAttackEvaluatedTargetCount.Value);
                }
                if (!string.IsNullOrEmpty(diagnostics.DebugAttackFailureReason))
                {
                    sb.Append(";attackFailure=");
                    sb.Append(diagnostics.DebugAttackFailureReason);
                }
            }

            if (diagnostics.DebugProfileRange.HasValue)
            {
                sb.Append(";profileRange=");
                sb.Append(diagnostics.DebugProfileRange.Value);
            }

            if (diagnostics.DebugResolvedActivityId != 0UL)
            {
                sb.Append(";resolvedActivity=");
                sb.Append(diagnostics.DebugResolvedActivityId);
            }
            if (diagnostics.DebugRawSelection != 0UL)
            {
                sb.Append(";rawSelection=");
                sb.Append(diagnostics.DebugRawSelection);
            }
            if (!string.IsNullOrEmpty(diagnostics.DebugRotationType))
            {
                sb.Append(";rotationType=");
                sb.Append(diagnostics.DebugRotationType);
            }
            if (diagnostics.DebugRotationCount.HasValue)
            {
                sb.Append(";rotationCount=");
                sb.Append(diagnostics.DebugRotationCount.Value);
            }
            if (diagnostics.DebugEnemyCandidateCount.HasValue)
            {
                sb.Append(";enemyCandidates=");
                sb.Append(diagnostics.DebugEnemyCandidateCount.Value);
            }

            return sb.ToString();
        }
    }
}
