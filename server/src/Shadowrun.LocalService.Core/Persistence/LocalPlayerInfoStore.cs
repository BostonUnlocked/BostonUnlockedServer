using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed class LocalPlayerInfoStore
    {
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private readonly RequestLogger _logger;
        private readonly object _lock = new object();
        private readonly string _playerInfoPath;
        private readonly SqliteLocalStore _sqliteStore;

        public LocalPlayerInfoStore(LocalServiceOptions options, RequestLogger logger, SqliteLocalStore sqliteStore)
        {
            _logger = logger;
            _sqliteStore = sqliteStore;

            var dataDir = options != null ? options.DataDir : null;
            if (string.IsNullOrEmpty(dataDir))
            {
                dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
            }

            try
            {
                if (!Directory.Exists(dataDir))
                {
                    Directory.CreateDirectory(dataDir);
                }
            }
            catch
            {
            }

            _playerInfoPath = Path.Combine(dataDir, "playerinfo.json");
        }

        public Dictionary<string, string> Get(string identityHash, string gameName)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.GetPlayerInfo(identityHash, gameName);
            }

            var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (IsNullOrWhiteSpace(identityHash) || IsNullOrWhiteSpace(gameName))
            {
                return results;
            }

            lock (_lock)
            {
                var root = LoadPlayerInfoNoThrow();
                var byIdentity = GetDict(root, NormalizeGuidish(identityHash));
                var byGame = byIdentity != null ? GetDict(byIdentity, gameName.Trim()) : null;
                if (byGame == null)
                {
                    return results;
                }

                foreach (DictionaryEntry entry in byGame)
                {
                    var k = entry.Key as string;
                    if (IsNullOrWhiteSpace(k))
                    {
                        continue;
                    }

                    var v = entry.Value as string;
                    results[k] = v;
                }

                return results;
            }
        }

        public PlayerInfoChanges Set(string identityHash, string gameName, Dictionary<string, string> updates)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.SetPlayerInfo(identityHash, gameName, updates);
            }

            var changes = new PlayerInfoChanges();
            if (IsNullOrWhiteSpace(identityHash) || IsNullOrWhiteSpace(gameName) || updates == null)
            {
                return changes;
            }

            lock (_lock)
            {
                var root = LoadPlayerInfoNoThrow();
                var identityKey = NormalizeGuidish(identityHash);
                var gameKey = gameName.Trim();

                var byIdentity = GetOrCreateDict(root, identityKey);
                var byGame = GetOrCreateDict(byIdentity, gameKey);

                foreach (var kvp in updates)
                {
                    var key = kvp.Key;
                    if (IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var value = kvp.Value;
                    var hadExisting = byGame.Contains(key);

                    if (value == null)
                    {
                        if (hadExisting)
                        {
                            byGame.Remove(key);
                            changes.Deleted.Add(key);
                        }
                        continue;
                    }

                    if (!hadExisting)
                    {
                        byGame[key] = value;
                        changes.Added[key] = value;
                        continue;
                    }

                    var existing = byGame[key] as string;
                    if (!string.Equals(existing, value, StringComparison.Ordinal))
                    {
                        byGame[key] = value;
                        changes.Updated[key] = value;
                    }
                }

                SavePlayerInfoNoThrow(root);
                return changes;
            }
        }

        public int DeleteIdentity(string identityHash)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.DeletePlayerInfoForIdentity(identityHash);
            }

            var normalizedIdentity = NormalizeGuidish(identityHash);
            if (IsNullOrWhiteSpace(normalizedIdentity))
            {
                return 0;
            }

            lock (_lock)
            {
                var root = LoadPlayerInfoNoThrow();
                if (root == null || !root.Contains(normalizedIdentity))
                {
                    return 0;
                }

                root.Remove(normalizedIdentity);
                SavePlayerInfoNoThrow(root);
                return 1;
            }
        }

        public List<KeyValuePair<string, Dictionary<string, string>>> Search(string gameName, string searchString)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.SearchPlayerInfo(gameName, searchString);
            }

            var results = new List<KeyValuePair<string, Dictionary<string, string>>>();
            if (IsNullOrWhiteSpace(gameName) || IsNullOrWhiteSpace(searchString))
            {
                return results;
            }

            lock (_lock)
            {
                var root = LoadPlayerInfoNoThrow();
                if (root == null)
                {
                    return results;
                }

                var gameKey = gameName.Trim();
                var needle = searchString.Trim();
                foreach (DictionaryEntry identityEntry in root)
                {
                    var identityHash = identityEntry.Key as string;
                    var byIdentity = identityEntry.Value as IDictionary;
                    if (IsNullOrWhiteSpace(identityHash) || byIdentity == null)
                    {
                        continue;
                    }

                    var byGame = GetDict(byIdentity, gameKey);
                    if (byGame == null)
                    {
                        continue;
                    }

                    var info = ToStringDictionary(byGame);
                    var score = GetSearchScore(info, needle);
                    if (score == int.MaxValue)
                    {
                        continue;
                    }

                    results.Add(new KeyValuePair<string, Dictionary<string, string>>(NormalizeGuidish(identityHash), info));
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

        public bool MutateRootNoThrow(Func<IDictionary, bool> mutator)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.MutatePlayerInfoRootNoThrow(mutator);
            }

            if (mutator == null)
            {
                return false;
            }

            lock (_lock)
            {
                var root = LoadPlayerInfoNoThrow();
                if (root == null)
                {
                    return false;
                }

                var changed = false;
                try
                {
                    changed = mutator(root);
                }
                catch
                {
                    changed = false;
                }

                if (changed)
                {
                    SavePlayerInfoNoThrow(root);
                }

                return changed;
            }
        }

        private IDictionary LoadPlayerInfoNoThrow()
        {
            try
            {
                if (File.Exists(_playerInfoPath))
                {
                    var json = File.ReadAllText(_playerInfoPath, Encoding.UTF8);
                    var obj = Json.DeserializeObject(json) as IDictionary;
                    if (obj != null)
                    {
                        return obj;
                    }
                }
            }
            catch
            {
            }

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private void SavePlayerInfoNoThrow(IDictionary root)
        {
            try
            {
                var json = Json.Serialize(root);
                File.WriteAllText(_playerInfoPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try
                {
                    if (_logger != null)
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "persistence",
                            op = "save-playerinfo-failed",
                            path = _playerInfoPath,
                            message = ex.Message,
                        });
                    }
                }
                catch
                {
                }
            }
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        private static IDictionary GetDict(IDictionary root, string key)
        {
            if (root == null || IsNullOrWhiteSpace(key) || !root.Contains(key))
            {
                return null;
            }

            return root[key] as IDictionary;
        }

        private static IDictionary GetOrCreateDict(IDictionary root, string key)
        {
            var existing = GetDict(root, key);
            if (existing != null)
            {
                return existing;
            }

            var created = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            root[key] = created;
            return created;
        }

        private static string NormalizeGuidish(string value)
        {
            if (IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                var g = new Guid(value.Trim());
                return g.ToString();
            }
            catch
            {
                return value.Trim();
            }
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }

        private static Dictionary<string, string> ToStringDictionary(IDictionary source)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return result;
            }

            foreach (DictionaryEntry entry in source)
            {
                var key = entry.Key as string;
                if (IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (entry.Value == null)
                {
                    result[key] = null;
                    continue;
                }

                var stringValue = entry.Value as string;
                result[key] = stringValue ?? entry.Value.ToString();
            }

            return result;
        }

        private static int GetSearchScore(Dictionary<string, string> info, string searchString)
        {
            if (info == null || IsNullOrWhiteSpace(searchString))
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

            var trimmed = value.Trim();
            for (var i = 0; i < terms.Count; i++)
            {
                if (string.Equals(terms[i], trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            terms.Add(trimmed);
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