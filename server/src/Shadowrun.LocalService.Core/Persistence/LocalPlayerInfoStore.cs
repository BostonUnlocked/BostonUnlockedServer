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

        public LocalPlayerInfoStore(LocalServiceOptions options, RequestLogger logger)
        {
            _logger = logger;

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

        public bool MutateRootNoThrow(Func<IDictionary, bool> mutator)
        {
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
    }
}