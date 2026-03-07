using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Shadowrun.LocalService.Core.AILogic;

namespace Shadowrun.LocalService.Core.Simulation
{
    internal sealed class PortedAiAgent
    {
        private enum AgentState
        {
            Peaceful = 0,
            TransitionToCombat = 1,
            InCombat = 2,
        }

        private readonly Entity _entity;
        private AgentState _state;

        public PortedAiAgent(Entity entity)
        {
            _entity = entity;
            _state = AgentState.Peaceful;
        }

        public Entity Entity
        {
            get { return _entity; }
        }

        public PlannedAiAction Act(IAiPlanner planner, IGameworldInstance gameworld)
        {
            var gridPosition = AiAgentSnapshotFactory.TryGetGridPositionOrDefault(gameworld, _entity);

            switch (_state)
            {
                case AgentState.TransitionToCombat:
                    _state = AgentState.InCombat;
                    return PlannedAiAction.CreateSwitchToCombat(_entity, gridPosition);

                case AgentState.InCombat:
                    if (planner != null)
                    {
                        var plannedAction = planner.Plan(_entity, new[] { _entity }, false);
                        if (plannedAction != null && plannedAction.Agent != null)
                        {
                            return plannedAction;
                        }
                    }

                    return PlannedAiAction.CreateEndTurn(_entity, gridPosition, "no-action", "no-action", ServerSimulationSession.EndActorTurnSkillId);

                default:
                    return PlannedAiAction.CreateEndTurn(_entity, gridPosition, "peaceful", "peaceful", ServerSimulationSession.EndActorTurnSkillId);
            }
        }

        public void SetHostile()
        {
            if (_state == AgentState.Peaceful)
            {
                _state = AgentState.TransitionToCombat;
            }
        }
    }
}