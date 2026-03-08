using System;
using System.Collections.Generic;

namespace Shadowrun.LocalService.Core.Protocols
{
    internal static class ConnectedPeerRegistry
    {
        private static readonly object SyncRoot = new object();
        private static readonly HashSet<string> ConnectedPeers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void MarkConnected(string peer)
        {
            if (string.IsNullOrEmpty(peer))
            {
                return;
            }

            lock (SyncRoot)
            {
                ConnectedPeers.Add(peer);
            }
        }

        public static void MarkDisconnected(string peer)
        {
            if (string.IsNullOrEmpty(peer))
            {
                return;
            }

            lock (SyncRoot)
            {
                ConnectedPeers.Remove(peer);
            }
        }

        public static bool IsConnected(string peer)
        {
            if (string.IsNullOrEmpty(peer))
            {
                return false;
            }

            lock (SyncRoot)
            {
                return ConnectedPeers.Contains(peer);
            }
        }
    }
}