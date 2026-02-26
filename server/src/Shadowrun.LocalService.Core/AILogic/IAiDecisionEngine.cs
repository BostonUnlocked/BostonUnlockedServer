using Cliffhanger.SRO.ServerClientCommons.Gameworld;

namespace Shadowrun.LocalService.Core.AILogic
{
    public interface IAiDecisionEngine
    {
        /// <summary>
        /// Computes a decision for a single agent at the current turn state.
        /// Placeholder implementation may return a decision with HasAction=false.
        /// </summary>
        AiDecision Decide(Entity agent, IGameworldInstance gameworld);
    }
}
