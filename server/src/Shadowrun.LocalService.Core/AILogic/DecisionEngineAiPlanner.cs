using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Locomotion;
using SRO.Core.Compatibility.Math;
using SRO.Core.Compatibility.Utilities;

namespace Shadowrun.LocalService.Core.AILogic
{
    public sealed class DecisionEngineAiPlanner : IAiPlanner
    {
        private readonly bool _enableAiLogic;
        private readonly IAiDecisionEngine _aiDecisionEngine;
        private readonly IGameworldInstance _gameworld;

        public DecisionEngineAiPlanner(bool enableAiLogic, IAiDecisionEngine aiDecisionEngine, IGameworldInstance gameworld)
        {
            _enableAiLogic = enableAiLogic;
            _aiDecisionEngine = aiDecisionEngine;
            _gameworld = gameworld;
        }

        public PlannedAiAction Plan(Entity fallbackAgent, Entity[] activatableMembers, bool forceEndTurnForInactiveGroup)
        {
            if (fallbackAgent == null)
            {
                return null;
            }

            if (forceEndTurnForInactiveGroup)
            {
                return PlannedAiAction.CreateEndTurn(fallbackAgent, TryGetAgentGridPositionOrDefault(fallbackAgent), "inactive-group", "inactive-group", Simulation.ServerSimulationSession.EndTeamTurnSkillId);
            }

            if (!_enableAiLogic || _aiDecisionEngine == null || activatableMembers == null || activatableMembers.Length == 0)
            {
                return PlannedAiAction.CreateEndTurn(fallbackAgent, TryGetAgentGridPositionOrDefault(fallbackAgent), "no-action", _enableAiLogic ? "no-target" : "ai-disabled", Simulation.ServerSimulationSession.EndTeamTurnSkillId);
            }

            var orderedMembers = activatableMembers
                .Where(member => member != null)
                .OrderByDescending(member => GetInitiative(member))
                .ToArray();
            if (orderedMembers.Length == 0)
            {
                orderedMembers = new[] { fallbackAgent };
            }

            AiPlanningDiagnostics lastDiagnostics = null;
            Entity lastAgent = fallbackAgent;
            for (var i = 0; i < orderedMembers.Length; i++)
            {
                var candidate = orderedMembers[i];
                if (candidate == null)
                {
                    continue;
                }

                lastAgent = candidate;
                try
                {
                    var decision = _aiDecisionEngine.Decide(candidate, _gameworld);
                    if (decision != null && decision.HasAction && decision.IsMove)
                    {
                        return PlannedAiAction.CreateMove(candidate, decision.MoveTargetPosition, AiPlanningDiagnostics.FromDecision(decision, "move"));
                    }

                    if (decision != null && decision.HasAction && decision.SkillId != 0UL)
                    {
                        var targetPosition = decision.TargetEntity != null
                            ? TryGetAgentGridPositionOrDefault(decision.TargetEntity)
                            : decision.TargetPosition;
                        return PlannedAiAction.CreateSkill(
                            candidate,
                            decision.WeaponIndex.HasValue ? decision.WeaponIndex.Value : 0,
                            decision.SkillIndex.HasValue ? decision.SkillIndex.Value : 0,
                            decision.SkillId <= (ulong)int.MaxValue ? (int)decision.SkillId : Simulation.ServerSimulationSession.EndTeamTurnSkillId,
                            targetPosition,
                            AiPlanningDiagnostics.FromDecision(decision, "ok"));
                    }

                    lastDiagnostics = AiPlanningDiagnostics.FromDecision(decision, decision == null ? "null-decision" : "no-action");
                    if (decision == null && lastDiagnostics != null && string.IsNullOrEmpty(lastDiagnostics.DebugStage))
                    {
                        lastDiagnostics.DebugStage = "null-decision";
                    }
                }
                catch
                {
                    lastDiagnostics = new AiPlanningDiagnostics
                    {
                        DecisionNote = "exception",
                        DebugStage = "exception",
                    };
                }
            }

            var fallbackDiagnostics = lastDiagnostics ?? new AiPlanningDiagnostics
            {
                DecisionNote = "no-action",
                DebugStage = "no-target",
            };
            if (string.IsNullOrEmpty(fallbackDiagnostics.DecisionNote))
            {
                fallbackDiagnostics.DecisionNote = "no-action";
            }
            if (string.IsNullOrEmpty(fallbackDiagnostics.DebugStage))
            {
                fallbackDiagnostics.DebugStage = "no-target";
            }

            return PlannedAiAction.CreateEndTurn(lastAgent ?? fallbackAgent, TryGetAgentGridPositionOrDefault(lastAgent ?? fallbackAgent), fallbackDiagnostics.DecisionNote, fallbackDiagnostics.DebugStage, Simulation.ServerSimulationSession.EndTeamTurnSkillId);
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

        private IntVector2D TryGetAgentGridPositionOrDefault(Entity entity)
        {
            if (_gameworld == null || _gameworld.EntitySystem == null || entity == null)
            {
                return IntVector2D.Zero;
            }

            try
            {
                APositionComponent position;
                if (_gameworld.EntitySystem.TryGetComponent<APositionComponent>(entity, out position) && position != null)
                {
                    return position.GridPosition;
                }
            }
            catch
            {
            }

            return IntVector2D.Zero;
        }
    }
}