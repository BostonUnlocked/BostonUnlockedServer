using System;
using System.Collections.Generic;
using System.Threading;
using Shadowrun.LocalService.Core.Simulation;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private static readonly TimeSpan MissionCleanupInterval = TimeSpan.FromSeconds(30);

        private readonly object _soloMissionLock = new object();
        private readonly Dictionary<string, ServerSimulationSession> _soloMissionSessions = new Dictionary<string, ServerSimulationSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _missionCleanupTimer;
        private int _missionCleanupInProgress;

        private void RegisterSoloMissionSession(string peer, ServerSimulationSession simulationSession)
        {
            if (IsNullOrWhiteSpace(peer) || simulationSession == null)
            {
                return;
            }

            ServerSimulationSession previous = null;
            lock (_soloMissionLock)
            {
                if (_soloMissionSessions.TryGetValue(peer, out previous) && object.ReferenceEquals(previous, simulationSession))
                {
                    previous = null;
                }

                _soloMissionSessions[peer] = simulationSession;
            }

            if (previous != null)
            {
                try
                {
                    previous.Stop();
                }
                catch
                {
                }
            }
        }

        private void StopAndForgetSoloMission(string peer, string reason)
        {
            if (IsNullOrWhiteSpace(peer))
            {
                return;
            }

            ServerSimulationSession simulationSession = null;
            lock (_soloMissionLock)
            {
                if (_soloMissionSessions.TryGetValue(peer, out simulationSession) && simulationSession != null)
                {
                    _soloMissionSessions.Remove(peer);
                }
                else
                {
                    simulationSession = null;
                }
            }

            if (simulationSession != null)
            {
                try
                {
                    simulationSession.Stop();
                }
                catch
                {
                }

                try
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "sim-cleanup",
                        peer = peer,
                        reason = reason ?? string.Empty,
                    });
                }
                catch
                {
                }
            }

            MissionRuntimeRegistry.MarkSoloMissionEnded(peer);
        }

        private void SweepDisconnectedMissionSessions(object state)
        {
            if (Interlocked.Exchange(ref _missionCleanupInProgress, 1) != 0)
            {
                return;
            }

            try
            {
                CleanupDisconnectedSoloMissionSessions();
                CleanupDisconnectedCoopMissionSessions();
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _missionCleanupInProgress, 0);
            }
        }

        private void CleanupDisconnectedSoloMissionSessions()
        {
            List<string> disconnectedPeers = null;
            lock (_soloMissionLock)
            {
                foreach (var kvp in _soloMissionSessions)
                {
                    if (!ConnectedPeerRegistry.IsConnected(kvp.Key))
                    {
                        if (disconnectedPeers == null)
                        {
                            disconnectedPeers = new List<string>();
                        }

                        disconnectedPeers.Add(kvp.Key);
                    }
                }
            }

            if (disconnectedPeers == null)
            {
                return;
            }

            for (var i = 0; i < disconnectedPeers.Count; i++)
            {
                StopAndForgetSoloMission(disconnectedPeers[i], "background-disconnect-sweep");
            }
        }

        private void CleanupDisconnectedCoopMissionSessions()
        {
            List<KeyValuePair<string, string>> staleParticipants = null;
            List<string> emptyGroups = null;

            lock (_coopMissionLock)
            {
                foreach (var kvp in _coopMissionParticipants)
                {
                    var coopGroupName = kvp.Key;
                    var list = kvp.Value;
                    var hasConnectedParticipant = false;

                    if (list != null)
                    {
                        for (var i = 0; i < list.Count; i++)
                        {
                            var participant = list[i];
                            if (participant == null || IsNullOrWhiteSpace(participant.Peer) || !ConnectedPeerRegistry.IsConnected(participant.Peer))
                            {
                                if (staleParticipants == null)
                                {
                                    staleParticipants = new List<KeyValuePair<string, string>>();
                                }

                                staleParticipants.Add(new KeyValuePair<string, string>(coopGroupName, participant != null ? participant.Peer : null));
                                continue;
                            }

                            hasConnectedParticipant = true;
                        }
                    }

                    if (!hasConnectedParticipant)
                    {
                        if (emptyGroups == null)
                        {
                            emptyGroups = new List<string>();
                        }

                        emptyGroups.Add(coopGroupName);
                    }
                }

                foreach (var kvp in _coopMissionSessions)
                {
                    if (_coopMissionParticipants.ContainsKey(kvp.Key))
                    {
                        continue;
                    }

                    if (emptyGroups == null)
                    {
                        emptyGroups = new List<string>();
                    }

                    emptyGroups.Add(kvp.Key);
                }
            }

            if (staleParticipants != null)
            {
                for (var i = 0; i < staleParticipants.Count; i++)
                {
                    var stale = staleParticipants[i];
                    if (!IsNullOrWhiteSpace(stale.Value))
                    {
                        UnregisterCoopMissionParticipant(stale.Key, stale.Value);
                    }
                }
            }

            if (emptyGroups != null)
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < emptyGroups.Count; i++)
                {
                    var coopGroupName = emptyGroups[i];
                    if (IsNullOrWhiteSpace(coopGroupName) || !visited.Add(coopGroupName))
                    {
                        continue;
                    }

                    TryStopEmptyCoopMissionGroup(coopGroupName, "background-disconnect-sweep");
                }
            }
        }
    }
}