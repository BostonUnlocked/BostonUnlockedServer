using Cliffhanger.SRO.ServerClientCommons.Gameworld;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Placeholder AI: chooses no action.
    /// The simulation layer can interpret this as "end turn" (current behaviour in ServerSimulationSession.AiTurns).
    /// </summary>
    public sealed class NullAiDecisionEngine : IAiDecisionEngine
    {
        public AiDecision Decide(Entity agent, IGameworldInstance gameworld)
        {
            return new AiDecision(agent);
        }
    }
}
