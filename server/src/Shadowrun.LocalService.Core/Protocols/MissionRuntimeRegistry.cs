using System;
using System.Collections.Generic;

namespace Shadowrun.LocalService.Core.Protocols
{
    internal static class MissionRuntimeRegistry
    {
        internal struct MissionRuntimeParticipant
        {
            public Guid AccountId;
            public string MapName;
            public string CoopGroupName;
            public bool IsCoop;
            public string Peer;
        }

        private sealed class SoloMissionState
        {
            public string Peer;
            public Guid AccountId;
            public string MapName;
        }

        private sealed class CoopMissionState
        {
            public string GroupName;
            public string MapName;
            public HashSet<string> Peers;
            public Dictionary<string, Guid> AccountByPeer;
        }

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, SoloMissionState> ActiveSoloByPeer = new Dictionary<string, SoloMissionState>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, CoopMissionState> ActiveCoopByGroup = new Dictionary<string, CoopMissionState>(StringComparer.OrdinalIgnoreCase);

        private static string NormalizeCoopMissionKey(string coopGroupName)
        {
            if (string.IsNullOrEmpty(coopGroupName))
            {
                return string.Empty;
            }

            return CoopGroupHostRegistry.NormalizeGroupName(coopGroupName);
        }

        private static void SweepDisconnectedMissions_NoLock()
        {
            if (ActiveSoloByPeer.Count > 0)
            {
                var disconnectedSoloPeers = new List<string>();
                foreach (var kvp in ActiveSoloByPeer)
                {
                    if (string.IsNullOrEmpty(kvp.Key) || !ConnectedPeerRegistry.IsConnected(kvp.Key))
                    {
                        disconnectedSoloPeers.Add(kvp.Key);
                    }
                }

                for (var i = 0; i < disconnectedSoloPeers.Count; i++)
                {
                    ActiveSoloByPeer.Remove(disconnectedSoloPeers[i]);
                }
            }

            if (ActiveCoopByGroup.Count == 0)
            {
                return;
            }

            var emptyGroups = new List<string>();
            foreach (var kvp in ActiveCoopByGroup)
            {
                var group = kvp.Value;
                if (group == null)
                {
                    emptyGroups.Add(kvp.Key);
                    continue;
                }

                var peers = group.Peers;
                if (peers == null)
                {
                    emptyGroups.Add(kvp.Key);
                    continue;
                }

                var disconnectedPeers = new List<string>();
                foreach (var peer in peers)
                {
                    if (string.IsNullOrEmpty(peer) || !ConnectedPeerRegistry.IsConnected(peer))
                    {
                        disconnectedPeers.Add(peer);
                    }
                }

                for (var i = 0; i < disconnectedPeers.Count; i++)
                {
                    var disconnectedPeer = disconnectedPeers[i];
                    peers.Remove(disconnectedPeer);
                    if (group.AccountByPeer != null)
                    {
                        group.AccountByPeer.Remove(disconnectedPeer);
                    }
                }

                if (peers.Count == 0)
                {
                    emptyGroups.Add(kvp.Key);
                }
            }

            for (var i = 0; i < emptyGroups.Count; i++)
            {
                ActiveCoopByGroup.Remove(emptyGroups[i]);
            }
        }

        public static void MarkSoloMissionStarted(string peer)
        {
            MarkSoloMissionStarted(peer, Guid.Empty, null);
        }

        public static void MarkSoloMissionStarted(string peer, Guid accountId, string mapName)
        {
            if (string.IsNullOrEmpty(peer))
            {
                return;
            }

            lock (SyncRoot)
            {
                ActiveSoloByPeer[peer] = new SoloMissionState
                {
                    Peer = peer,
                    AccountId = accountId,
                    MapName = mapName ?? string.Empty,
                };
            }
        }

        public static void MarkSoloMissionEnded(string peer)
        {
            if (string.IsNullOrEmpty(peer))
            {
                return;
            }

            lock (SyncRoot)
            {
                ActiveSoloByPeer.Remove(peer);
            }
        }

        public static void MarkCoopMissionStarted(string coopGroupName)
        {
            MarkCoopMissionStarted(coopGroupName, null);
        }

        public static void MarkCoopMissionStarted(string coopGroupName, string mapName)
        {
            var coopKey = NormalizeCoopMissionKey(coopGroupName);
            if (string.IsNullOrEmpty(coopKey))
            {
                return;
            }

            lock (SyncRoot)
            {
                CoopMissionState group;
                if (!ActiveCoopByGroup.TryGetValue(coopKey, out group) || group == null)
                {
                    group = new CoopMissionState
                    {
                        GroupName = coopKey,
                        MapName = mapName ?? string.Empty,
                        Peers = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        AccountByPeer = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase),
                    };
                    ActiveCoopByGroup[coopKey] = group;
                    return;
                }

                if (!string.IsNullOrEmpty(mapName))
                {
                    group.MapName = mapName;
                }
            }
        }

        public static void MarkCoopMissionParticipantJoined(string coopGroupName, string peer)
        {
            MarkCoopMissionParticipantJoined(coopGroupName, peer, Guid.Empty);
        }

        public static void MarkCoopMissionParticipantJoined(string coopGroupName, string peer, Guid accountId)
        {
            var coopKey = NormalizeCoopMissionKey(coopGroupName);
            if (string.IsNullOrEmpty(coopKey) || string.IsNullOrEmpty(peer))
            {
                return;
            }

            lock (SyncRoot)
            {
                CoopMissionState group;
                if (!ActiveCoopByGroup.TryGetValue(coopKey, out group) || group == null)
                {
                    group = new CoopMissionState
                    {
                        GroupName = coopKey,
                        MapName = string.Empty,
                        Peers = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        AccountByPeer = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase),
                    };
                    ActiveCoopByGroup[coopKey] = group;
                }

                if (group.Peers == null)
                {
                    group.Peers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                if (group.AccountByPeer == null)
                {
                    group.AccountByPeer = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
                }

                group.Peers.Add(peer);
                group.AccountByPeer[peer] = accountId;
            }
        }

        public static void MarkCoopMissionParticipantLeft(string coopGroupName, string peer)
        {
            var coopKey = NormalizeCoopMissionKey(coopGroupName);
            if (string.IsNullOrEmpty(coopKey) || string.IsNullOrEmpty(peer))
            {
                return;
            }

            lock (SyncRoot)
            {
                CoopMissionState group;
                if (!ActiveCoopByGroup.TryGetValue(coopKey, out group) || group == null)
                {
                    return;
                }

                var peers = group.Peers;
                if (peers == null)
                {
                    ActiveCoopByGroup.Remove(coopKey);
                    return;
                }

                peers.Remove(peer);

                if (group.AccountByPeer != null)
                {
                    group.AccountByPeer.Remove(peer);
                }

                if (peers.Count == 0)
                {
                    ActiveCoopByGroup.Remove(coopKey);
                }
            }
        }

        public static void MarkCoopMissionEnded(string coopGroupName)
        {
            var coopKey = NormalizeCoopMissionKey(coopGroupName);
            if (string.IsNullOrEmpty(coopKey))
            {
                return;
            }

            lock (SyncRoot)
            {
                ActiveCoopByGroup.Remove(coopKey);
            }
        }

        public static bool IsCoopMissionActive(string coopGroupName)
        {
            var coopKey = NormalizeCoopMissionKey(coopGroupName);
            if (string.IsNullOrEmpty(coopKey))
            {
                return false;
            }

            lock (SyncRoot)
            {
                return ActiveCoopByGroup.ContainsKey(coopKey);
            }
        }

        public static MissionRuntimeParticipant[] SnapshotParticipants()
        {
            lock (SyncRoot)
            {
                SweepDisconnectedMissions_NoLock();

                var participants = new List<MissionRuntimeParticipant>();
                foreach (var kvp in ActiveSoloByPeer)
                {
                    var solo = kvp.Value;
                    if (solo == null || solo.AccountId == Guid.Empty)
                    {
                        continue;
                    }

                    participants.Add(new MissionRuntimeParticipant
                    {
                        AccountId = solo.AccountId,
                        MapName = solo.MapName ?? string.Empty,
                        CoopGroupName = string.Empty,
                        IsCoop = false,
                        Peer = solo.Peer ?? string.Empty,
                    });
                }

                foreach (var kvp in ActiveCoopByGroup)
                {
                    var group = kvp.Value;
                    if (group == null || group.AccountByPeer == null || group.AccountByPeer.Count == 0)
                    {
                        continue;
                    }

                    foreach (var peerEntry in group.AccountByPeer)
                    {
                        var accountId = peerEntry.Value;
                        if (accountId == Guid.Empty)
                        {
                            continue;
                        }

                        participants.Add(new MissionRuntimeParticipant
                        {
                            AccountId = accountId,
                            MapName = group.MapName ?? string.Empty,
                            CoopGroupName = group.GroupName ?? string.Empty,
                            IsCoop = true,
                            Peer = peerEntry.Key ?? string.Empty,
                        });
                    }
                }

                return participants.ToArray();
            }
        }

        public static int GetActiveMissionCount()
        {
            lock (SyncRoot)
            {
                SweepDisconnectedMissions_NoLock();
                return ActiveSoloByPeer.Count + ActiveCoopByGroup.Count;
            }
        }
    }
}
