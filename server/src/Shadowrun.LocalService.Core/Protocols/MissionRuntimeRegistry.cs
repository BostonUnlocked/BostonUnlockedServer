using System;
using System.Collections.Generic;

namespace Shadowrun.LocalService.Core.Protocols
{
    internal static class MissionRuntimeRegistry
    {
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> ActiveSoloPeers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ActiveCoopGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            if (string.IsNullOrEmpty(coopGroupName))
            {
                return;
            }

            lock (SyncRoot)
            {
                ActiveCoopGroups.Add(coopGroupName);
            }
        }

        public static void MarkCoopMissionEnded(string coopGroupName)
        {
            if (string.IsNullOrEmpty(coopGroupName))
            {
                return;
            }

            lock (SyncRoot)
            {
                ActiveCoopGroups.Remove(coopGroupName);
            }
        }

        public static int GetActiveMissionCount()
        {
            lock (SyncRoot)
            {
                return ActiveSoloPeers.Count + ActiveCoopGroups.Count;
            }
        }
    }
}
