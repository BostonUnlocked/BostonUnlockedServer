using System;
using System.Collections.Generic;

namespace Shadowrun.LocalService.Core.Protocols
{
    internal static class MissionRuntimeRegistry
    {
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> ActiveSoloPeers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<string>> ActiveCoopGroups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

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
            ActiveSoloPeers.RemoveWhere(delegate (string peer)
            {
                return !ConnectedPeerRegistry.IsConnected(peer);
            });

            if (ActiveCoopGroups.Count == 0)
            {
                return;
            }

            var emptyGroups = new List<string>();
            foreach (var kvp in ActiveCoopGroups)
            {
                var peers = kvp.Value;
                if (peers == null)
                {
                    emptyGroups.Add(kvp.Key);
                    continue;
                }

                peers.RemoveWhere(delegate (string peer)
                {
                    return !ConnectedPeerRegistry.IsConnected(peer);
                });

                if (peers.Count == 0)
                {
                    emptyGroups.Add(kvp.Key);
                }
            }

            for (var i = 0; i < emptyGroups.Count; i++)
            {
                ActiveCoopGroups.Remove(emptyGroups[i]);
            }
        }

        public static void MarkSoloMissionStarted(string peer)
        {
            if (string.IsNullOrEmpty(peer))
            {
                return;
            }

            lock (SyncRoot)
            {
                ActiveSoloPeers.Add(peer);
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
                ActiveSoloPeers.Remove(peer);
            }
        }

        public static void MarkCoopMissionStarted(string coopGroupName)
        {
            var coopKey = NormalizeCoopMissionKey(coopGroupName);
            if (string.IsNullOrEmpty(coopKey))
            {
                return;
            }

            lock (SyncRoot)
            {
                if (!ActiveCoopGroups.ContainsKey(coopKey))
                {
                    ActiveCoopGroups[coopKey] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public static void MarkCoopMissionParticipantJoined(string coopGroupName, string peer)
        {
            var coopKey = NormalizeCoopMissionKey(coopGroupName);
            if (string.IsNullOrEmpty(coopKey) || string.IsNullOrEmpty(peer))
            {
                return;
            }

            lock (SyncRoot)
            {
                HashSet<string> peers;
                if (!ActiveCoopGroups.TryGetValue(coopKey, out peers) || peers == null)
                {
                    peers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ActiveCoopGroups[coopKey] = peers;
                }

                peers.Add(peer);
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
                HashSet<string> peers;
                if (!ActiveCoopGroups.TryGetValue(coopKey, out peers) || peers == null)
                {
                    return;
                }

                peers.Remove(peer);
                if (peers.Count == 0)
                {
                    ActiveCoopGroups.Remove(coopKey);
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
                ActiveCoopGroups.Remove(coopKey);
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
                return ActiveCoopGroups.ContainsKey(coopKey);
            }
        }

        public static int GetActiveMissionCount()
        {
            lock (SyncRoot)
            {
                SweepDisconnectedMissions_NoLock();
                return ActiveSoloPeers.Count + ActiveCoopGroups.Count;
            }
        }
    }
}
