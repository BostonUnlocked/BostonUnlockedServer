using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed class LocalSessionStore : ISessionIdentityMap
    {
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private readonly RequestLogger _logger;
        private readonly object _lock = new object();
        private readonly string _sessionsPath;

        public LocalSessionStore(LocalServiceOptions options, RequestLogger logger)
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

            _sessionsPath = Path.Combine(dataDir, "sessions.json");
        }

        public Guid CreateSessionForIdentity(string identityHash)
        {
            var normalizedIdentity = NormalizeGuidish(identityHash);
            if (IsNullOrWhiteSpace(normalizedIdentity))
            {
                return Guid.Empty;
            }

            lock (_lock)
            {
                var session = Guid.NewGuid();
                var sessions = LoadSessionsNoThrow();
                sessions[NormalizeGuidish(session.ToString())] = normalizedIdentity;
                SaveSessionsNoThrow(sessions);
                return session;
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
                var sessions = LoadSessionsNoThrow();
                string mapped;
                if (sessions.TryGetValue(NormalizeGuidish(sessionHash), out mapped) && !IsNullOrWhiteSpace(mapped))
                {
                    identityHash = NormalizeGuidish(mapped);
                    return true;
                }

                return false;
            }
        }

        public void SetIdentityForSession(string sessionHash, string identityHash)
        {
            if (IsNullOrWhiteSpace(sessionHash) || IsNullOrWhiteSpace(identityHash))
            {
                return;
            }

            lock (_lock)
            {
                var sessions = LoadSessionsNoThrow();
                sessions[NormalizeGuidish(sessionHash)] = NormalizeGuidish(identityHash);
                SaveSessionsNoThrow(sessions);
            }
        }

        private Dictionary<string, string> LoadSessionsNoThrow()
        {
            try
            {
                if (File.Exists(_sessionsPath))
                {
                    var json = File.ReadAllText(_sessionsPath, Encoding.UTF8);
                    var obj = Json.DeserializeObject(json) as IDictionary;
                    if (obj != null)
                    {
                        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (DictionaryEntry entry in obj)
                        {
                            var key = entry.Key as string;
                            var value = entry.Value as string;
                            if (!IsNullOrWhiteSpace(key) && !IsNullOrWhiteSpace(value))
                            {
                                dict[NormalizeGuidish(key)] = NormalizeGuidish(value);
                            }
                        }

                        return dict;
                    }
                }
            }
            catch
            {
            }

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private void SaveSessionsNoThrow(Dictionary<string, string> sessions)
        {
            try
            {
                var obj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in sessions)
                {
                    obj[kvp.Key] = kvp.Value;
                }

                var json = Json.Serialize(obj);
                File.WriteAllText(_sessionsPath, json, Encoding.UTF8);
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
                            op = "save-sessions-failed",
                            path = _sessionsPath,
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