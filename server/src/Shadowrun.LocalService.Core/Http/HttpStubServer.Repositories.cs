using System;
using System.Collections.Generic;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Http
{
    public sealed partial class HttpStubServer
    {
        private sealed class LocalUserStoreSessionIdentityMap : ISessionIdentityMap
        {
            private readonly LocalUserStore _store;

            public LocalUserStoreSessionIdentityMap(LocalUserStore store)
            {
                _store = store;
            }

            public void SetIdentityForSession(string sessionHash, string identityHash)
            {
                if (_store == null)
                {
                    return;
                }
                _store.SetIdentityForSession(sessionHash, identityHash);
            }

            public bool TryGetIdentityForSession(string sessionHash, out string identityHash)
            {
                identityHash = null;
                if (_store == null)
                {
                    return false;
                }
                return _store.TryGetIdentityForSession(sessionHash, out identityHash);
            }
        }

        private sealed class LocalUserStorePlayerInfoRepository : IPlayerInfoRepository
        {
            private readonly LocalUserStore _store;

            public LocalUserStorePlayerInfoRepository(LocalUserStore store)
            {
                _store = store;
            }

            public Dictionary<string, string> Get(string identityHash, string gameName)
            {
                if (_store == null)
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                return _store.GetPlayerInfo(identityHash, gameName);
            }

            public PlayerInfoChanges Set(string identityHash, string gameName, Dictionary<string, string> updates)
            {
                if (_store == null)
                {
                    return new PlayerInfoChanges();
                }

                var changes = _store.SetPlayerInfo(identityHash, gameName, updates);
                var result = new PlayerInfoChanges();
                foreach (var kvp in changes.Added) result.Added[kvp.Key] = kvp.Value;
                foreach (var kvp in changes.Updated) result.Updated[kvp.Key] = kvp.Value;
                for (var i = 0; i < changes.Deleted.Count; i++) result.Deleted.Add(changes.Deleted[i]);
                return result;
            }
        }

        private sealed class InMemorySessionIdentityMap : ISessionIdentityMap
        {
            private readonly object _lock = new object();
            private readonly Dictionary<string, string> _sessionToIdentity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public void SetIdentityForSession(string sessionHash, string identityHash)
            {
                if (IsNullOrWhiteSpace(sessionHash) || IsNullOrWhiteSpace(identityHash))
                {
                    return;
                }

                lock (_lock)
                {
                    _sessionToIdentity[NormalizeGuidish(sessionHash)] = NormalizeGuidish(identityHash);
                }
            }

            public bool TryGetIdentityForSession(string sessionHash, out string identityHash)
            {
                identityHash = null;
                if (IsNullOrWhiteSpace(sessionHash))
                {
                    return false;
                }

                lock (_lock)
                {
                    return _sessionToIdentity.TryGetValue(NormalizeGuidish(sessionHash), out identityHash);
                }
            }
        }

        private sealed class InMemoryPlayerInfoRepository : IPlayerInfoRepository
        {
            private readonly object _lock = new object();
            private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _data =
                new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, string> Get(string identityHash, string gameName)
            {
                if (IsNullOrWhiteSpace(identityHash) || IsNullOrWhiteSpace(gameName))
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                identityHash = NormalizeGuidish(identityHash);
                gameName = gameName.Trim();

                lock (_lock)
                {
                    Dictionary<string, Dictionary<string, string>> byGame;
                    if (!_data.TryGetValue(identityHash, out byGame))
                    {
                        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    Dictionary<string, string> info;
                    if (!byGame.TryGetValue(gameName, out info) || info == null)
                    {
                        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    return new Dictionary<string, string>(info, StringComparer.OrdinalIgnoreCase);
                }
            }

            public PlayerInfoChanges Set(string identityHash, string gameName, Dictionary<string, string> updates)
            {
                var changes = new PlayerInfoChanges();
                if (IsNullOrWhiteSpace(identityHash) || IsNullOrWhiteSpace(gameName) || updates == null)
                {
                    return changes;
                }

                identityHash = NormalizeGuidish(identityHash);
                gameName = gameName.Trim();

                lock (_lock)
                {
                    Dictionary<string, Dictionary<string, string>> byGame;
                    if (!_data.TryGetValue(identityHash, out byGame) || byGame == null)
                    {
                        byGame = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                        _data[identityHash] = byGame;
                    }

                    Dictionary<string, string> info;
                    if (!byGame.TryGetValue(gameName, out info) || info == null)
                    {
                        info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        byGame[gameName] = info;
                    }

                    foreach (var kvp in updates)
                    {
                        var key = kvp.Key;
                        if (IsNullOrWhiteSpace(key))
                        {
                            continue;
                        }

                        var value = kvp.Value;
                        string existing;
                        var hadExisting = info.TryGetValue(key, out existing);

                        if (value == null)
                        {
                            if (hadExisting)
                            {
                                info.Remove(key);
                                changes.Deleted.Add(key);
                            }
                            continue;
                        }

                        if (!hadExisting)
                        {
                            info[key] = value;
                            changes.Added[key] = value;
                            continue;
                        }

                        if (!string.Equals(existing, value, StringComparison.Ordinal))
                        {
                            info[key] = value;
                            changes.Updated[key] = value;
                        }
                    }
                }

                return changes;
            }
        }
    }
}
