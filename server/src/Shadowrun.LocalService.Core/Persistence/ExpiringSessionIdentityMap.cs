using System;
using System.Collections.Generic;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed class ExpiringSessionIdentityMap : ISessionIdentityMap
    {
        private sealed class Entry
        {
            public string IdentityHash;
            public DateTime ExpiresUtc;
            public DateTime LastSeenUtc;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _sessionToIdentity = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private readonly TimeSpan _ttl;
        private readonly TimeSpan _pruneInterval;
        private DateTime _lastPruneUtc;

        public ExpiringSessionIdentityMap()
            : this(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1))
        {
        }

        public ExpiringSessionIdentityMap(TimeSpan ttl, TimeSpan pruneInterval)
        {
            if (ttl.Ticks <= 0)
            {
                ttl = TimeSpan.FromMinutes(10);
            }
            if (pruneInterval.Ticks <= 0)
            {
                pruneInterval = TimeSpan.FromSeconds(30);
            }

            _ttl = ttl;
            _pruneInterval = pruneInterval;
            _lastPruneUtc = DateTime.UtcNow;
        }

        public void SetIdentityForSession(string sessionHash, string identityHash)
        {
            if (IsNullOrWhiteSpace(sessionHash) || IsNullOrWhiteSpace(identityHash))
            {
                return;
            }

            var now = DateTime.UtcNow;
            sessionHash = NormalizeGuidish(sessionHash);
            identityHash = NormalizeGuidish(identityHash);

            lock (_lock)
            {
                MaybePruneNoThrow(now);
                _sessionToIdentity[sessionHash] = new Entry
                {
                    IdentityHash = identityHash,
                    LastSeenUtc = now,
                    ExpiresUtc = now.Add(_ttl),
                };
            }
        }

        public bool TryGetIdentityForSession(string sessionHash, out string identityHash)
        {
            identityHash = null;
            if (IsNullOrWhiteSpace(sessionHash))
            {
                return false;
            }

            var now = DateTime.UtcNow;
            sessionHash = NormalizeGuidish(sessionHash);

            lock (_lock)
            {
                MaybePruneNoThrow(now);

                Entry entry;
                if (!_sessionToIdentity.TryGetValue(sessionHash, out entry) || entry == null)
                {
                    return false;
                }

                if (entry.ExpiresUtc <= now)
                {
                    _sessionToIdentity.Remove(sessionHash);
                    return false;
                }

                // Sliding expiration: keep the session alive while the client is active.
                entry.LastSeenUtc = now;
                entry.ExpiresUtc = now.Add(_ttl);
                identityHash = entry.IdentityHash;
                return !IsNullOrWhiteSpace(identityHash);
            }
        }

        private void MaybePruneNoThrow(DateTime now)
        {
            if (now.Subtract(_lastPruneUtc) < _pruneInterval)
            {
                return;
            }

            _lastPruneUtc = now;

            try
            {
                if (_sessionToIdentity.Count == 0)
                {
                    return;
                }

                var expiredKeys = new List<string>();
                foreach (var kvp in _sessionToIdentity)
                {
                    var e = kvp.Value;
                    if (e == null || e.ExpiresUtc <= now)
                    {
                        expiredKeys.Add(kvp.Key);
                    }
                }

                for (var i = 0; i < expiredKeys.Count; i++)
                {
                    _sessionToIdentity.Remove(expiredKeys[i]);
                }
            }
            catch
            {
                // Best-effort only.
            }
        }

        private static bool IsNullOrWhiteSpace(string s)
        {
            if (s == null)
            {
                return true;
            }

            for (var i = 0; i < s.Length; i++)
            {
                if (!char.IsWhiteSpace(s[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static string NormalizeGuidish(string value)
        {
            if (value == null)
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                return trimmed;
            }

            try
            {
                var guid = new Guid(trimmed);
                return guid.ToString("D");
            }
            catch
            {
                return trimmed;
            }
        }
    }
}
