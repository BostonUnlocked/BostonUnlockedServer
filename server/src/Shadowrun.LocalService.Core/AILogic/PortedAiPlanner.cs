using System.Collections.Generic;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
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
        private readonly Dictionary<int, IAISkillSelection> _skillSelectors;

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
            _skillSelectors = new Dictionary<int, IAISkillSelection>();
        }

        public PlannedAiAction Plan(Entity fallbackAgent, Entity[] activatableMembers, bool forceEndTurnForInactiveGroup)
        {
            if (fallbackAgent == null)
            {
                return null;
            }

            if (!_enableAiLogic || forceEndTurnForInactiveGroup || _gameworld == null || _skillSelectionFactory == null)
            {
                return PlannedAiAction.CreateEndTurn(fallbackAgent, AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, fallbackAgent), forceEndTurnForInactiveGroup ? "inactive-group" : "no-action", forceEndTurnForInactiveGroup ? "inactive-group" : "no-target", Simulation.ServerSimulationSession.EndActorTurnSkillId);
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

                PopulateLoadoutDiagnostics(candidate, diagnostics);

                var selector = GetOrCreateSkillSelector(candidate, config, diagnostics);
                if (selector != null)
                {
                    PlannedAiAction attackPlan;

                    ulong selectedSkillId;
                    try
                    {
                        selectedSkillId = selector.SelectSkill();
                    }
                    catch
                    {
                        selectedSkillId = selector.DefaultSkill;
                    }

                    diagnostics.DebugRawSelection = selectedSkillId;

                    if (TryCreateAttackPlan(candidate, attackPlanner, selectedSkillId, "ported-attack", diagnostics, out attackPlan))
                    {
                        return attackPlan;
                    }

                    if (selectedSkillId != selector.DefaultSkill
                        && TryCreateAttackPlan(candidate, attackPlanner, selector.DefaultSkill, "ported-attack-fallback", diagnostics, out attackPlan))
                    {
                        return attackPlan;
                    }
                }
            }

            return PlannedAiAction.CreateEndTurn(fallbackAgent, AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, fallbackAgent), "no-action", "no-target", Simulation.ServerSimulationSession.EndActorTurnSkillId);
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
            if (!attackPlanner.TryPlanAttack(skillId, baseDiagnostics, out weaponIndex, out skillIndex, out targetPosition, out score))
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
                DebugChosenEnemyId = diagnostics.DebugChosenEnemyId,
                DebugChosenEnemyTeamId = diagnostics.DebugChosenEnemyTeamId,
                DebugChosenEnemyTeamAi = diagnostics.DebugChosenEnemyTeamAi,
                DebugChosenEnemyControlPlayerId = diagnostics.DebugChosenEnemyControlPlayerId,
                DebugChosenEnemyControlAi = diagnostics.DebugChosenEnemyControlAi,
                DebugChosenEnemyIsPlayersPlayerCharacter = diagnostics.DebugChosenEnemyIsPlayersPlayerCharacter,
                DebugChosenEnemyInteractiveObject = diagnostics.DebugChosenEnemyInteractiveObject,
                DebugChosenTargetRelationship = diagnostics.DebugChosenTargetRelationship,
                DebugEnemyPick = diagnostics.DebugEnemyPick,
                DebugEnemyReason = diagnostics.DebugEnemyReason,
                DebugEnemyX = diagnostics.DebugEnemyX,
                DebugEnemyY = diagnostics.DebugEnemyY,
                DebugEnemyCandidateCount = diagnostics.DebugEnemyCandidateCount,
                DebugAttackCandidateCount = diagnostics.DebugAttackCandidateCount,
                DebugAttackEvaluatedTargetCount = diagnostics.DebugAttackEvaluatedTargetCount,
                DebugAttackUsedSelfTarget = diagnostics.DebugAttackUsedSelfTarget,
                DebugAttackFailureReason = diagnostics.DebugAttackFailureReason,
                DebugShotChanceToHit = diagnostics.DebugShotChanceToHit,
            });
            return true;
        }

        private IAISkillSelection GetOrCreateSkillSelector(Entity candidate, AIBehaviourConfigurationComponent config, AiPlanningDiagnostics diagnostics)
        {
            if (candidate == null || config == null || config.SkillRotation == null || _skillSelectionFactory == null)
            {
                return null;
            }

            if (diagnostics != null)
            {
                diagnostics.DebugRotationType = config.SkillRotation.GetType().FullName;
                diagnostics.DebugRotationCount = TryGetRotationCount(config.SkillRotation);
            }

            IAISkillSelection selector;
            if (_skillSelectors.TryGetValue(candidate.Id, out selector) && selector != null)
            {
                return selector;
            }

            try
            {
                selector = config.SkillRotation.CreateFor(_skillSelectionFactory, candidate, _random);
            }
            catch
            {
                selector = null;
            }

            if (selector != null)
            {
                _skillSelectors[candidate.Id] = selector;
            }

            return selector;
        }

        private void PopulateLoadoutDiagnostics(Entity candidate, AiPlanningDiagnostics diagnostics)
        {
            if (diagnostics == null)
            {
                return;
            }

            ISkillLoadoutComponent loadout;
            if (_gameworld != null
                && _gameworld.EntitySystem != null
                && candidate != null
                && _gameworld.EntitySystem.TryGetComponent<ISkillLoadoutComponent>(candidate, out loadout)
                && loadout != null)
            {
                diagnostics.DebugHasLoadout = true;
                diagnostics.DebugSelectedWeaponIndex = loadout.SelectedWeaponIndex;
                diagnostics.DebugSelectedWeaponSkillCount = loadout.SelectedWeapon != null && loadout.SelectedWeapon.Skills != null
                    ? (int?)loadout.SelectedWeapon.Skills.Length
                    : null;
                return;
            }

            diagnostics.DebugHasLoadout = false;
            diagnostics.DebugSelectedWeaponIndex = null;
            diagnostics.DebugSelectedWeaponSkillCount = null;
        }

        private static int? TryGetRotationCount(ISkillSelectionConfiguration rotation)
        {
            var simple = rotation as RotationSkillSelection;
            if (simple != null)
            {
                return simple.Rotation != null ? (int?)simple.Rotation.Length : null;
            }

            var weighted = rotation as WeightedSkillSelection;
            if (weighted != null)
            {
                return weighted.Rotation != null ? (int?)weighted.Rotation.Length : null;
            }

            var conditional = rotation as ConditionalRotationSkillSelection;
            if (conditional != null)
            {
                return conditional.Rotation != null ? (int?)conditional.Rotation.Length : null;
            }

            var notOnCooldown = rotation as NextSkillNotOnCooldownSelection;
            if (notOnCooldown != null)
            {
                return notOnCooldown.Rotation != null ? (int?)notOnCooldown.Rotation.Length : null;
            }

            return null;
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
                DebugChosenTargetRelationship = source.DebugChosenTargetRelationship,
                DebugEnemyPick = source.DebugEnemyPick,
                DebugEnemyReason = source.DebugEnemyReason,
                DebugEnemyX = source.DebugEnemyX,
                DebugEnemyY = source.DebugEnemyY,
                DebugEnemyCandidateCount = source.DebugEnemyCandidateCount,
                DebugAttackCandidateCount = source.DebugAttackCandidateCount,
                DebugAttackEvaluatedTargetCount = source.DebugAttackEvaluatedTargetCount,
                DebugAttackUsedSelfTarget = source.DebugAttackUsedSelfTarget,
                DebugAttackFailureReason = source.DebugAttackFailureReason,
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