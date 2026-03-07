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
        private readonly IAiPlanner _fallbackPlanner;

        public PortedAiPlanner(
            bool enableAiLogic,
            IGameworldInstance gameworld,
            IRandomNumberGenerator random,
            IAiBehaviourConfigLookup configLookup,
            ISkillSelectionStrategyFactory skillSelectionFactory,
            IValuationFactory valuationFactory,
            IAiPlanner fallbackPlanner)
        {
            _enableAiLogic = enableAiLogic;
            _gameworld = gameworld;
            _random = random;
            _configLookup = configLookup;
            _skillSelectionFactory = skillSelectionFactory;
            _valuationFactory = valuationFactory;
            _fallbackPlanner = fallbackPlanner;
        }

        public PlannedAiAction Plan(Entity fallbackAgent, Entity[] activatableMembers, bool forceEndTurnForInactiveGroup)
        {
            if (!_enableAiLogic || forceEndTurnForInactiveGroup || _gameworld == null || _configLookup == null || _skillSelectionFactory == null)
            {
                return _fallbackPlanner != null ? _fallbackPlanner.Plan(fallbackAgent, activatableMembers, forceEndTurnForInactiveGroup) : PlannedAiAction.CreateEndTurn(fallbackAgent, AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, fallbackAgent), "no-action", "no-target", Simulation.ServerSimulationSession.EndTeamTurnSkillId);
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
                AIBehaviourConfigurationComponent config;
                if (!_configLookup.TryGetConfig(candidate, _gameworld, out config) || config == null)
                {
                    continue;
                }

                var movementPlanner = new PortedAIMovementPlanner(_gameworld, candidate, _valuationFactory);
                IntVector2D moveTarget;
                float moveScore;
                if (movementPlanner.TryPlanMove(config, out moveTarget, out moveScore))
                {
                    return PlannedAiAction.CreateMove(candidate, moveTarget, new AiPlanningDiagnostics
                    {
                        DecisionNote = "ported-move",
                        DebugStage = "ported-move",
                        DebugChosenMoveScore = moveScore,
                    });
                }

                var selector = config.SkillRotation != null ? config.SkillRotation.CreateFor(_skillSelectionFactory, candidate, _random) : null;
                var attackPlanner = new PortedAIAttackPlanner(_gameworld, candidate);

                var selectedSkill = selector != null ? selector.SelectSkill() : 0UL;
                PlannedAiAction attackPlan;
                if (TryCreateAttackPlan(candidate, attackPlanner, selectedSkill, "ported-attack", out attackPlan))
                {
                    return attackPlan;
                }

                var defaultSkill = selector != null ? selector.DefaultSkill : 0UL;
                if (defaultSkill != 0UL && defaultSkill != selectedSkill && TryCreateAttackPlan(candidate, attackPlanner, defaultSkill, "ported-default-attack", out attackPlan))
                {
                    return attackPlan;
                }
            }

            return _fallbackPlanner != null
                ? _fallbackPlanner.Plan(fallbackAgent, activatableMembers, forceEndTurnForInactiveGroup)
                : PlannedAiAction.CreateEndTurn(fallbackAgent, AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, fallbackAgent), "no-action", "no-target", Simulation.ServerSimulationSession.EndTeamTurnSkillId);
        }

        private bool TryCreateAttackPlan(Entity candidate, PortedAIAttackPlanner attackPlanner, ulong skillId, string note, out PlannedAiAction plan)
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

            plan = PlannedAiAction.CreateSkill(candidate, weaponIndex, skillIndex, skillId <= (ulong)int.MaxValue ? (int)skillId : Simulation.ServerSimulationSession.EndTeamTurnSkillId, targetPosition, new AiPlanningDiagnostics
            {
                DecisionNote = note,
                DebugStage = note,
                DebugShotChanceToHit = score,
            });
            return true;
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