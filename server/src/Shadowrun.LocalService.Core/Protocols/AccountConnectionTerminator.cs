using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed class AccountConnectionTerminator
    {
        private sealed class ConnectionHandle
        {
            public Guid AccountId;
            public string Protocol;
            public string ConnectionHash;
            public string Endpoint;
            public TcpClient Client;
            public NetworkStream Stream;
        }

        public sealed class TerminationResult
        {
            public int Requested;
            public int Closed;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, ConnectionHandle> _handlesByKey = new Dictionary<string, ConnectionHandle>(StringComparer.OrdinalIgnoreCase);

        public void Register(Guid accountId, string protocol, string connectionHash, string endpoint, TcpClient client, NetworkStream stream)
        {
            if (accountId == Guid.Empty || IsNullOrWhiteSpace(protocol) || IsNullOrWhiteSpace(connectionHash))
            {
                return;
            }

            var key = BuildKey(protocol, connectionHash);
            lock (_lock)
            {
                _handlesByKey[key] = new ConnectionHandle
                {
                    AccountId = accountId,
                    Protocol = protocol,
                    ConnectionHash = connectionHash,
                    Endpoint = endpoint,
                    Client = client,
                    Stream = stream,
                };
            }
        }

        public void Unregister(string protocol, string connectionHash)
        {
            if (IsNullOrWhiteSpace(protocol) || IsNullOrWhiteSpace(connectionHash))
            {
                return;
            }

            var key = BuildKey(protocol, connectionHash);
            lock (_lock)
            {
                _handlesByKey.Remove(key);
            }
        }

        public TerminationResult TerminateAccount(Guid accountId)
        {
            var result = new TerminationResult();
            if (accountId == Guid.Empty)
            {
                return result;
            }

            ConnectionHandle[] handles;
            lock (_lock)
            {
                var matched = new List<ConnectionHandle>();
                foreach (var kvp in _handlesByKey)
                {
                    var handle = kvp.Value;
                    if (handle != null && handle.AccountId == accountId)
                    {
                        matched.Add(handle);
                    }
                }

                handles = matched.ToArray();
            }

            result.Requested = handles.Length;
            for (var i = 0; i < handles.Length; i++)
            {
                if (CloseHandle(handles[i]))
                {
                    result.Closed++;
                }
            }

            return result;
        }

        private static bool CloseHandle(ConnectionHandle handle)
        {
            if (handle == null)
            {
                return false;
            }

            var closed = false;
            try
            {
                if (handle.Stream != null)
                {
                    handle.Stream.Close();
                    closed = true;
                }
            }
            catch
            {
            }

            try
            {
                if (handle.Client != null)
                {
                    handle.Client.Close();
                    closed = true;
                }
            }
            catch
            {
            }

            return closed;
        }

        private static string BuildKey(string protocol, string connectionHash)
        {
            return protocol + ":" + connectionHash;
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}