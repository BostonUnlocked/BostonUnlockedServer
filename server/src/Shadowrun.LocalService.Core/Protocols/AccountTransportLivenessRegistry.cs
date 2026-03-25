using System;
using System.Collections.Generic;

namespace Shadowrun.LocalService.Core.Protocols
{
    internal static class AccountTransportLivenessRegistry
    {
        internal struct AccountLivenessSnapshot
        {
            public Guid AccountId;
            public int APlayConnections;
            public int PhotonConnections;
            public bool IsSocialOnline;
            public bool IsHardOffline;
        }

        internal const string TransportAPlay = "aplay";
        internal const string TransportPhoton = "photon";

        private sealed class AccountTransportState
        {
            public int APlayConnections;
            public int PhotonConnections;

            public bool IsOnline
            {
                get { return APlayConnections > 0 || PhotonConnections > 0; }
            }
        }

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<Guid, AccountTransportState> StateByAccountId = new Dictionary<Guid, AccountTransportState>();

        public static void MarkConnected(Guid accountId, string transport)
        {
            if (accountId == Guid.Empty)
            {
                return;
            }

            lock (SyncRoot)
            {
                AccountTransportState state;
                if (!StateByAccountId.TryGetValue(accountId, out state) || state == null)
                {
                    state = new AccountTransportState();
                    StateByAccountId[accountId] = state;
                }

                if (string.Equals(transport, TransportAPlay, StringComparison.OrdinalIgnoreCase))
                {
                    state.APlayConnections++;
                    return;
                }

                if (string.Equals(transport, TransportPhoton, StringComparison.OrdinalIgnoreCase))
                {
                    state.PhotonConnections++;
                }
            }
        }

        public static void MarkDisconnected(Guid accountId, string transport)
        {
            if (accountId == Guid.Empty)
            {
                return;
            }

            lock (SyncRoot)
            {
                AccountTransportState state;
                if (!StateByAccountId.TryGetValue(accountId, out state) || state == null)
                {
                    return;
                }

                if (string.Equals(transport, TransportAPlay, StringComparison.OrdinalIgnoreCase))
                {
                    if (state.APlayConnections > 0)
                    {
                        state.APlayConnections--;
                    }
                }
                else if (string.Equals(transport, TransportPhoton, StringComparison.OrdinalIgnoreCase))
                {
                    if (state.PhotonConnections > 0)
                    {
                        state.PhotonConnections--;
                    }
                }

                if (state.APlayConnections <= 0 && state.PhotonConnections <= 0)
                {
                    StateByAccountId.Remove(accountId);
                }
            }
        }

        public static bool IsOnline(Guid accountId)
        {
            if (accountId == Guid.Empty)
            {
                return false;
            }

            lock (SyncRoot)
            {
                AccountTransportState state;
                return StateByAccountId.TryGetValue(accountId, out state)
                    && state != null
                    && state.IsOnline;
            }
        }

        public static AccountLivenessSnapshot Evaluate(Guid accountId)
        {
            var snapshot = new AccountLivenessSnapshot
            {
                AccountId = accountId,
                APlayConnections = 0,
                PhotonConnections = 0,
                IsSocialOnline = false,
                IsHardOffline = true,
            };

            if (accountId == Guid.Empty)
            {
                return snapshot;
            }

            lock (SyncRoot)
            {
                AccountTransportState state;
                if (!StateByAccountId.TryGetValue(accountId, out state) || state == null)
                {
                    return snapshot;
                }

                snapshot.APlayConnections = state.APlayConnections;
                snapshot.PhotonConnections = state.PhotonConnections;
                snapshot.IsSocialOnline = state.IsOnline;
                snapshot.IsHardOffline = !state.IsOnline;
                return snapshot;
            }
        }
    }
}