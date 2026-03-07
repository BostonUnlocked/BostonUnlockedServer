using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.RandomNumbers;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic
{
    public sealed class PortedAiPlanner : IAiPlanner
    {
        private readonly bool _enableAiLogic;
        private readonly IGameworldInstance _gameworld;
        private readonly IRandomNumberGenerator _random;
        private readonly IAiBehaviourConfigLookup _configLookup;
        private readonly ISkillSelectionStrategyFactory _skillSelectionFactory;
        private readonly IValuationFactory _valuationFactory;

        public PortedAiPlanner(
            bool enableAiLogic,
            IGameworldInstance gameworld,
            IRandomNumberGenerator random,
            IAiBehaviourConfigLookup configLookup,
            ISkillSelectionStrategyFactory skillSelectionFactory,
            IValuationFactory valuationFactory)
        {
            _enableAiLogic = enableAiLogic;
            _gameworld = gameworld;
            _random = random;
            _configLookup = configLookup;
            _skillSelectionFactory = skillSelectionFactory;
            _valuationFactory = valuationFactory;
        }

        public PlannedAiAction Plan(Entity fallbackAgent, Entity[] activatableMembers, bool forceEndTurnForInactiveGroup)
        {
            if (fallbackAgent == null)
            {
                return null;
            }

            if (!_enableAiLogic || forceEndTurnForInactiveGroup || _gameworld == null || _skillSelectionFactory == null)
            {
                return PlannedAiAction.CreateEndTurn(fallbackAgent, AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, fallbackAgent), forceEndTurnForInactiveGroup ? "inactive-group" : "no-action", forceEndTurnForInactiveGroup ? "inactive-group" : "no-target", Simulation.ServerSimulationSession.EndTeamTurnSkillId);
            }

            var orderedMembers = (activatableMembers ?? new Entity[0])
                .Where(entity => entity != null)
                .OrderByDescending(entity => GetInitiative(entity))
                .ToArray();
            if (orderedMembers.Length == 0 && fallbackAgent != null)
            {
                orderedMembers = new[] { fallbackAgent };
            }

            for (var i = 0; i < orderedMembers.Length; i++)
            {
                var candidate = orderedMembers[i];
                var diagnostics = new AiPlanningDiagnostics();
                AIBehaviourConfigurationComponent config;
                if (_configLookup == null || !_configLookup.TryGetConfig(candidate, _gameworld, out config) || config == null)
                {
                    config = null;
                }

                diagnostics.DebugHasAiConfig = config != null;

                if (config != null)
                {
                    var movementPlanner = new PortedAIMovementPlanner(_gameworld, candidate, _valuationFactory);
                    IntVector2D moveTarget;
                    float moveScore;
                    if (movementPlanner.TryPlanMove(config, out moveTarget, out moveScore))
                    {
                        diagnostics.DecisionNote = "ported-move";
                        diagnostics.DebugStage = "ported-move";
                        diagnostics.DebugChosenMoveScore = moveScore;
                        diagnostics.DebugChosenMoveWithinWalkRange = IsWithinWalkRange(candidate, moveTarget);
                        return PlannedAiAction.CreateMove(candidate, moveTarget, diagnostics);
                    }
                }

                var attackPlanner = new PortedAIAttackPlanner(_gameworld, candidate);

                var skillCandidates = AiSkillCatalog.CollectCandidateSkills(candidate, _gameworld, _skillSelectionFactory, _random, config, diagnostics);
                for (var skillIndex = 0; skillIndex < skillCandidates.Length; skillIndex++)
                {
                    PlannedAiAction attackPlan;
                    var note = skillIndex == 0 ? "ported-attack" : "ported-attack-fallback";
                    if (TryCreateAttackPlan(candidate, attackPlanner, skillCandidates[skillIndex], note, diagnostics, out attackPlan))
                    {
                        return attackPlan;
                    }
                }

                var genericMovementPlanner = new GenericAiMovementPlanner(_gameworld, candidate);
                IntVector2D fallbackMoveTarget;
                AiPlanningDiagnostics moveDiagnostics;
                if (genericMovementPlanner.TryPlanAdvance(out fallbackMoveTarget, out moveDiagnostics))
                {
                    CopyUnsetDiagnostics(moveDiagnostics, diagnostics);
                    return PlannedAiAction.CreateMove(candidate, fallbackMoveTarget, moveDiagnostics);
                }
            }

            return PlannedAiAction.CreateEndTurn(fallbackAgent, AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, fallbackAgent), "no-action", "no-target", Simulation.ServerSimulationSession.EndTeamTurnSkillId);
        }

        private bool TryCreateAttackPlan(Entity candidate, PortedAIAttackPlanner attackPlanner, ulong skillId, string note, AiPlanningDiagnostics baseDiagnostics, out PlannedAiAction plan)
        {
            plan = null;
            if (skillId == 0UL || attackPlanner == null)
            {
                return false;
            }

            int weaponIndex;
            int skillIndex;
            IntVector2D targetPosition;
            float score;
            if (!attackPlanner.TryPlanAttack(skillId, out weaponIndex, out skillIndex, out targetPosition, out score))
            {
                return false;
            }

            var diagnostics = CloneDiagnostics(baseDiagnostics);
            diagnostics.DecisionNote = note;
            diagnostics.DebugStage = note;
            diagnostics.DebugResolvedActivityId = skillId;
            diagnostics.DebugShotChanceToHit = score;

            plan = PlannedAiAction.CreateSkill(candidate, weaponIndex, skillIndex, skillId <= (ulong)int.MaxValue ? (int)skillId : Simulation.ServerSimulationSession.EndTeamTurnSkillId, targetPosition, new AiPlanningDiagnostics
            {
                DecisionNote = diagnostics.DecisionNote,
                DebugStage = diagnostics.DebugStage,
                DebugRotationType = diagnostics.DebugRotationType,
                DebugRotationCount = diagnostics.DebugRotationCount,
                DebugRawSelection = diagnostics.DebugRawSelection,
                DebugResolvedActivityId = diagnostics.DebugResolvedActivityId,
                DebugHasAiConfig = diagnostics.DebugHasAiConfig,
                DebugHasLoadout = diagnostics.DebugHasLoadout,
                DebugSelectedWeaponIndex = diagnostics.DebugSelectedWeaponIndex,
                DebugSelectedWeaponSkillCount = diagnostics.DebugSelectedWeaponSkillCount,
                DebugShotChanceToHit = diagnostics.DebugShotChanceToHit,
            });
            return true;
        }

        private bool IsWithinWalkRange(Entity agent, IntVector2D position)
        {
            if (_gameworld == null || _gameworld.ReachableRangesCalculator == null || agent == null)
            {
                return false;
            }

            try
            {
                var ranges = _gameworld.ReachableRangesCalculator.GetReachableRanges(agent);
                return ranges != null && ranges.WalkRange != null && ranges.WalkRange.Contains(position);
            }
            catch
            {
                return false;
            }
        }

        private static AiPlanningDiagnostics CloneDiagnostics(AiPlanningDiagnostics source)
        {
            if (source == null)
            {
                return new AiPlanningDiagnostics();
            }

            return new AiPlanningDiagnostics
            {
                DecisionNote = source.DecisionNote,
                DebugStage = source.DebugStage,
                DebugRotationType = source.DebugRotationType,
                DebugRotationCount = source.DebugRotationCount,
                DebugRawSelection = source.DebugRawSelection,
                DebugResolvedActivityId = source.DebugResolvedActivityId,
                DebugHasAiConfig = source.DebugHasAiConfig,
                DebugHasLoadout = source.DebugHasLoadout,
                DebugSelectedWeaponIndex = source.DebugSelectedWeaponIndex,
                DebugSelectedWeaponSkillCount = source.DebugSelectedWeaponSkillCount,
                DebugPreferredEnemyId = source.DebugPreferredEnemyId,
                DebugChosenEnemyId = source.DebugChosenEnemyId,
                DebugChosenEnemyTeamId = source.DebugChosenEnemyTeamId,
                DebugChosenEnemyTeamAi = source.DebugChosenEnemyTeamAi,
                DebugChosenEnemyControlPlayerId = source.DebugChosenEnemyControlPlayerId,
                DebugChosenEnemyControlAi = source.DebugChosenEnemyControlAi,
                DebugChosenEnemyIsPlayersPlayerCharacter = source.DebugChosenEnemyIsPlayersPlayerCharacter,
                DebugChosenEnemyInteractiveObject = source.DebugChosenEnemyInteractiveObject,
                DebugEnemyPick = source.DebugEnemyPick,
                DebugEnemyReason = source.DebugEnemyReason,
                DebugEnemyX = source.DebugEnemyX,
                DebugEnemyY = source.DebugEnemyY,
                DebugEnemyCandidateCount = source.DebugEnemyCandidateCount,
                DebugReachableCellCount = source.DebugReachableCellCount,
                DebugReducingCellCount = source.DebugReducingCellCount,
                DebugAvoidedImmediateBacktrack = source.DebugAvoidedImmediateBacktrack,
                DebugCurrentDistToEnemy = source.DebugCurrentDistToEnemy,
                DebugChosenMoveDistToEnemy = source.DebugChosenMoveDistToEnemy,
                DebugChosenMoveDefensiveCover = source.DebugChosenMoveDefensiveCover,
                DebugChosenMoveTargetCover = source.DebugChosenMoveTargetCover,
                DebugChosenMoveScore = source.DebugChosenMoveScore,
                DebugChosenMoveChanceToHit = source.DebugChosenMoveChanceToHit,
                DebugChosenMoveWithinWalkRange = source.DebugChosenMoveWithinWalkRange,
                DebugProfileRange = source.DebugProfileRange,
                DebugShotDistanceToTarget = source.DebugShotDistanceToTarget,
                DebugShotChanceToHit = source.DebugShotChanceToHit,
                InactiveSpawnManagerTag = source.InactiveSpawnManagerTag,
                ForceEndTurnForInactiveGroup = source.ForceEndTurnForInactiveGroup,
            };
        }

        private static void CopyUnsetDiagnostics(AiPlanningDiagnostics target, AiPlanningDiagnostics source)
        {
            if (target == null || source == null)
            {
                return;
            }

            if (!target.DebugHasAiConfig.HasValue)
            {
                target.DebugHasAiConfig = source.DebugHasAiConfig;
            }

            if (!target.DebugHasLoadout.HasValue)
            {
                target.DebugHasLoadout = source.DebugHasLoadout;
            }

            if (target.DebugSelectedWeaponIndex == null)
            {
                target.DebugSelectedWeaponIndex = source.DebugSelectedWeaponIndex;
            }

            if (target.DebugSelectedWeaponSkillCount == null)
            {
                target.DebugSelectedWeaponSkillCount = source.DebugSelectedWeaponSkillCount;
            }

            if (target.DebugRotationType == null)
            {
                target.DebugRotationType = source.DebugRotationType;
            }

            if (target.DebugRotationCount == null)
            {
                target.DebugRotationCount = source.DebugRotationCount;
            }
        }

        private int GetInitiative(Entity entity)
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
    }
}