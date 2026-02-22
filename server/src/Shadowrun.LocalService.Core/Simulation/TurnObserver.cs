using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.CommandProcessing;

namespace Shadowrun.LocalService.Core.Simulation
{
    internal sealed class TurnObserver : ITurnPhaseListener
    {
        public TurnObserver()
        {
            CurrentActivatableMembers = new Entity[0];
        }

        public Team CurrentTeam { get; private set; }
        public Entity[] CurrentActivatableMembers { get; private set; }

        public void PrepareStartTurn(StartTurnEvent @event)
        {
        }

        public void StartTurn(StartTurnEvent @event)
        {
            CurrentTeam = @event.Team;
            CurrentActivatableMembers = @event.ActivatableTeamMembers ?? new Entity[0];
        }

        public void ContinueTurn(ContinueTurnEvent @event)
        {
            CurrentTeam = @event.Team;
            CurrentActivatableMembers = @event.ActivatableTeamMembers ?? new Entity[0];
        }

        public void EndTurn(EndTurnEvent @event)
        {
            CurrentTeam = @event.Team;
        }
    }
}
