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

            public List<KeyValuePair<string, Dictionary<string, string>>> Search(string gameName, string searchString)
            {
                if (_store == null)
                {
                    return new List<KeyValuePair<string, Dictionary<string, string>>>();
                }

                return _store.SearchPlayerInfo(gameName, searchString);
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

            public List<KeyValuePair<string, Dictionary<string, string>>> Search(string gameName, string searchString)
            {
                var results = new List<KeyValuePair<string, Dictionary<string, string>>>();
                if (IsNullOrWhiteSpace(gameName) || IsNullOrWhiteSpace(searchString))
                {
                    return results;
                }

                gameName = gameName.Trim();
                searchString = searchString.Trim();

                lock (_lock)
                {
                    foreach (var identityEntry in _data)
                    {
                        if (identityEntry.Value == null)
                        {
                            continue;
                        }

                        Dictionary<string, string> info;
                        if (!identityEntry.Value.TryGetValue(gameName, out info) || info == null)
                        {
                            continue;
                        }

                        if (GetSearchScore(info, searchString) == int.MaxValue)
                        {
                            continue;
                        }

                        results.Add(new KeyValuePair<string, Dictionary<string, string>>(
                            identityEntry.Key,
                            new Dictionary<string, string>(info, StringComparer.OrdinalIgnoreCase)));
                    }
                }

                results.Sort(delegate (KeyValuePair<string, Dictionary<string, string>> left, KeyValuePair<string, Dictionary<string, string>> right)
                {
                    var leftScore = GetSearchScore(left.Value, searchString);
                    var rightScore = GetSearchScore(right.Value, searchString);
                    var scoreCompare = leftScore.CompareTo(rightScore);
                    if (scoreCompare != 0)
                    {
                        return scoreCompare;
                    }

                    var leftText = GetPrimaryDisplayText(left.Value);
                    var rightText = GetPrimaryDisplayText(right.Value);
                    var textCompare = string.Compare(leftText, rightText, StringComparison.OrdinalIgnoreCase);
                    if (textCompare != 0)
                    {
                        return textCompare;
                    }

                    return string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);
                });
                return results;
            }

            private static int GetSearchScore(Dictionary<string, string> info, string searchString)
            {
                if (info == null)
                {
                    return int.MaxValue;
                }

                var terms = BuildSearchTerms(info);
                if (terms.Count == 0)
                {
                    return int.MaxValue;
                }

                var needle = searchString.Trim();
                var best = int.MaxValue;
                for (var i = 0; i < terms.Count; i++)
                {
                    var term = terms[i];
                    if (IsNullOrWhiteSpace(term))
                    {
                        continue;
                    }

                    if (string.Equals(term, needle, StringComparison.OrdinalIgnoreCase))
                    {
                        best = Math.Min(best, 0);
                        continue;
                    }

                    if (term.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        best = Math.Min(best, 1);
                        continue;
                    }

                    if (term.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        best = Math.Min(best, 2);
                    }
                }

                return best;
            }

            private static List<string> BuildSearchTerms(Dictionary<string, string> info)
            {
                var terms = new List<string>();
                AddSearchTerm(terms, TryGet(info, "LauncherDisplayName"));

                var displayName = TryGet(info, "DisplayName");
                AddSearchTerm(terms, displayName);
                AddSearchTerm(terms, ExtractCharacterName(displayName));

                AddSearchTerm(terms, TryGet(info, "CharacterName"));
                AddSearchTerm(terms, TryGet(info, "AccountName"));
                return terms;
            }

            private static void AddSearchTerm(List<string> terms, string value)
            {
                if (terms == null || IsNullOrWhiteSpace(value))
                {
                    return;
                }

                for (var i = 0; i < terms.Count; i++)
                {
                    if (string.Equals(terms[i], value, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                terms.Add(value.Trim());
            }

            private static string GetPrimaryDisplayText(Dictionary<string, string> info)
            {
                if (info == null)
                {
                    return string.Empty;
                }

                var launcherDisplayName = TryGet(info, "LauncherDisplayName");
                if (!IsNullOrWhiteSpace(launcherDisplayName))
                {
                    return launcherDisplayName;
                }

                var displayName = TryGet(info, "DisplayName");
                if (!IsNullOrWhiteSpace(displayName))
                {
                    return displayName;
                }

                return TryGet(info, "AccountName") ?? string.Empty;
            }

            private static string ExtractCharacterName(string displayName)
            {
                if (IsNullOrWhiteSpace(displayName))
                {
                    return null;
                }

                var semi = displayName.IndexOf(';');
                if (semi < 0 || semi + 1 >= displayName.Length)
                {
                    return null;
                }

                return displayName.Substring(semi + 1).Trim();
            }

            private static string TryGet(Dictionary<string, string> info, string key)
            {
                if (info == null || IsNullOrWhiteSpace(key))
                {
                    return null;
                }

                string value;
                return info.TryGetValue(key, out value) ? value : null;
            }
        }
    }
}
