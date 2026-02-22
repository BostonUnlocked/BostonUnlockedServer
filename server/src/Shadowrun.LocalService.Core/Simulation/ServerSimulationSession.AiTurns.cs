using System.Collections.Generic;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.CommandProcessing;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Commands;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Locomotion;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.RandomNumbers;
using SRO.Core.Compatibility.Math;
using SRO.Core.Compatibility.Utilities;

namespace Shadowrun.LocalService.Core.Simulation
{
    public sealed partial class ServerSimulationSession
    {
        public IList<AiEndTurnAction> SkipAiTurnsIfNeeded()
        {
            var actions = new List<AiEndTurnAction>();

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

                AiEndTurnAction action;
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

        private bool TryExecuteAiTurnStep(Team team, out AiEndTurnAction action)
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

            var agent = _turnObserver.CurrentActivatableMembers[0];
            var targetPos = TryGetAgentGridPositionOrDefault(agent);

            var seeds = _random.CreateSeedPackage();

            var cmd = new ActivatePositionTargetedActiveSkillCommand(
                0,
                0,
                EndTeamTurnSkillId,
                agent.Id,
                targetPos,
                _gameworld,
                _random,
                seeds);

            ExecuteCommand(cmd, "AI.EndTeamTurn", 0, 0, EndTeamTurnSkillId, targetPos.X, targetPos.Y);

            action = new AiEndTurnAction(agent.Id, seeds);
            return true;
        }
    }
}
