using System;
using System.Collections.Generic;

namespace Shadowrun.LocalService.Core.Protocols
{
    internal static class AccountTransportLivenessRegistry
    {
        internal delegate void HardOfflineObserver(Guid accountId);

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
        private static readonly List<HardOfflineObserver> HardOfflineObservers = new List<HardOfflineObserver>();

        public static void RegisterHardOfflineObserver(HardOfflineObserver observer)
        {
            if (observer == null)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (!HardOfflineObservers.Contains(observer))
                {
                    HardOfflineObservers.Add(observer);
                }
            }
        }

        public static void NotifyHardOffline(Guid accountId)
        {
            if (accountId == Guid.Empty)
            {
                return;
            }

            HardOfflineObserver[] observers;
            lock (SyncRoot)
            {
                if (HardOfflineObservers.Count == 0)
                {
                    return;
                }

                observers = HardOfflineObservers.ToArray();
            }

            for (var i = 0; i < observers.Length; i++)
            {
                var observer = observers[i];
                if (observer == null)
                {
                    continue;
                }

                try
                {
                    observer(accountId);
                }
                catch
                {
                }
            }
        }

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

        public static Guid[] SnapshotOnlineAccountIds()
        {
            lock (SyncRoot)
            {
                if (StateByAccountId.Count == 0)
                {
                    return new Guid[0];
                }

                var online = new List<Guid>(StateByAccountId.Count);
                foreach (var kvp in StateByAccountId)
                {
                    var accountId = kvp.Key;
                    var state = kvp.Value;
                    if (accountId == Guid.Empty || state == null || !state.IsOnline)
                    {
                        continue;
                    }

                    online.Add(accountId);
                }

                return online.ToArray();
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