using Cliffhanger.SRO.ServerClientCommons.Gameworld;

namespace Shadowrun.LocalService.Core.AILogic
{
    public interface IAiPlanner
    {
        PlannedAiAction Plan(Entity fallbackAgent, Entity[] activatableMembers, bool forceEndTurnForInactiveGroup);
    }
}