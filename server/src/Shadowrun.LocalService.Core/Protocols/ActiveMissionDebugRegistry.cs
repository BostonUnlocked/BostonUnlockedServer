using System;
using System.Collections.Generic;
using Shadowrun.LocalService.Core.Simulation;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed class ActiveMissionDebugRegistry
    {
        public sealed class ActiveMissionEntry
        {
            public string SessionId;
            public string Mode;
            public string Peer;
            public string CoopGroupName;
            public Guid AccountId;
            public Guid[] ParticipantAccountIds;
            public int CareerIndex;
            public string MapName;
            public uint Seed0;
            public uint Seed1;
            public uint Seed2;
            public uint Seed3;
            public DateTime CreatedUtc;
            public DateTime? EndedUtc;
            public ServerSimulationSession Simulation;
            public object SyncRoot;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, ActiveMissionEntry> _entries = new Dictionary<string, ActiveMissionEntry>(StringComparer.OrdinalIgnoreCase);
        private string _latestSessionId;
        private ActiveMissionEntry _lastCompletedEntry;

        private bool _hasNextSeedOverride;
        private bool _nextSeedSticky;
        private uint _nextSeed0;
        private uint _nextSeed1;
        private uint _nextSeed2;
        private uint _nextSeed3;

        public void RegisterMission(
            string sessionId,
            string mode,
            string peer,
            string coopGroupName,
            Guid accountId,
            int careerIndex,
            string mapName,
            uint seed0,
            uint seed1,
            uint seed2,
            uint seed3,
            ServerSimulationSession simulation,
            object syncRoot)
        {
            if (string.IsNullOrEmpty(sessionId) || simulation == null)
            {
                return;
            }

            lock (_lock)
            {
                _entries[sessionId] = new ActiveMissionEntry
                {
                    SessionId = sessionId,
                    Mode = mode ?? "solo",
                    Peer = peer ?? string.Empty,
                    CoopGroupName = coopGroupName ?? string.Empty,
                    AccountId = accountId,
                    ParticipantAccountIds = accountId != Guid.Empty ? new Guid[] { accountId } : new Guid[0],
                    CareerIndex = careerIndex,
                    MapName = mapName ?? string.Empty,
                    Seed0 = seed0,
                    Seed1 = seed1,
                    Seed2 = seed2,
                    Seed3 = seed3,
                    CreatedUtc = DateTime.UtcNow,
                    Simulation = simulation,
                    SyncRoot = syncRoot ?? new object(),
                };
                _latestSessionId = sessionId;
            }
        }

        public void SetParticipantAccounts(string sessionId, IEnumerable<Guid> accountIds)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            lock (_lock)
            {
                ActiveMissionEntry entry;
                if (!_entries.TryGetValue(sessionId, out entry) || entry == null)
                {
                    return;
                }

                var unique = new List<Guid>();
                if (accountIds != null)
                {
                    foreach (var id in accountIds)
                    {
                        if (id != Guid.Empty && !unique.Contains(id))
                        {
                            unique.Add(id);
                        }
                    }
                }
                if (entry.AccountId != Guid.Empty && !unique.Contains(entry.AccountId))
                {
                    unique.Add(entry.AccountId);
                }

                entry.ParticipantAccountIds = unique.ToArray();
            }
        }

        public void AddParticipantAccount(string sessionId, Guid accountId)
        {
            if (string.IsNullOrEmpty(sessionId) || accountId == Guid.Empty)
            {
                return;
            }

            lock (_lock)
            {
                ActiveMissionEntry entry;
                if (!_entries.TryGetValue(sessionId, out entry) || entry == null)
                {
                    return;
                }

                var existing = entry.ParticipantAccountIds;
                if (existing != null)
                {
                    for (var i = 0; i < existing.Length; i++)
                    {
                        if (existing[i] == accountId)
                        {
                            return;
                        }
                    }

                    var updated = new Guid[existing.Length + 1];
                    Array.Copy(existing, 0, updated, 0, existing.Length);
                    updated[existing.Length] = accountId;
                    entry.ParticipantAccountIds = updated;
                }
                else
                {
                    entry.ParticipantAccountIds = new Guid[] { accountId };
                }
            }
        }

        public void RemoveParticipantAccount(string sessionId, Guid accountId)
        {
            if (string.IsNullOrEmpty(sessionId) || accountId == Guid.Empty)
            {
                return;
            }

            lock (_lock)
            {
                ActiveMissionEntry entry;
                if (!_entries.TryGetValue(sessionId, out entry) || entry == null)
                {
                    return;
                }

                var existing = entry.ParticipantAccountIds;
                if (existing == null || existing.Length == 0)
                {
                    return;
                }

                var remaining = new List<Guid>(existing.Length);
                for (var i = 0; i < existing.Length; i++)
                {
                    if (existing[i] != accountId)
                    {
                        remaining.Add(existing[i]);
                    }
                }

                if (remaining.Count != existing.Length)
                {
                    entry.ParticipantAccountIds = remaining.ToArray();
                }
                if (entry.AccountId == accountId)
                {
                    entry.AccountId = remaining.Count > 0 ? remaining[0] : Guid.Empty;
                }
            }
        }

        public void UnregisterMission(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            lock (_lock)
            {
                ActiveMissionEntry removed;
                if (_entries.TryGetValue(sessionId, out removed) && removed != null)
                {
                    removed.EndedUtc = DateTime.UtcNow;
                    _lastCompletedEntry = removed;
                }
                _entries.Remove(sessionId);
                if (string.Equals(_latestSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    _latestSessionId = null;
                    DateTime newest = DateTime.MinValue;
                    foreach (var kvp in _entries)
                    {
                        if (kvp.Value != null && kvp.Value.CreatedUtc >= newest)
                        {
                            newest = kvp.Value.CreatedUtc;
                            _latestSessionId = kvp.Key;
                        }
                    }
                }
            }
        }

        public ActiveMissionEntry[] ListMissions()
        {
            lock (_lock)
            {
                var list = new List<ActiveMissionEntry>(_entries.Count);
                foreach (var kvp in _entries)
                {
                    if (kvp.Value != null)
                    {
                        list.Add(kvp.Value);
                    }
                }
                list.Sort(delegate (ActiveMissionEntry a, ActiveMissionEntry b)
                {
                    return b.CreatedUtc.CompareTo(a.CreatedUtc);
                });
                return list.ToArray();
            }
        }

        public bool TryResolveMission(string sessionIdOrAlias, out ActiveMissionEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(sessionIdOrAlias))
            {
                return false;
            }

            lock (_lock)
            {
                if (string.Equals(sessionIdOrAlias, "latest", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(_latestSessionId) && _entries.TryGetValue(_latestSessionId, out entry) && entry != null)
                    {
                        return true;
                    }
                    foreach (var kvp in _entries)
                    {
                        if (kvp.Value != null)
                        {
                            entry = kvp.Value;
                            return true;
                        }
                    }
                    if (_lastCompletedEntry != null)
                    {
                        entry = _lastCompletedEntry;
                        return true;
                    }
                    return false;
                }

                if (_entries.TryGetValue(sessionIdOrAlias, out entry) && entry != null)
                {
                    return true;
                }

                if (_lastCompletedEntry != null && string.Equals(_lastCompletedEntry.SessionId, sessionIdOrAlias, StringComparison.OrdinalIgnoreCase))
                {
                    entry = _lastCompletedEntry;
                    return true;
                }

                Guid accountGuid;
                if (TryParseGuid(sessionIdOrAlias, out accountGuid) && accountGuid != Guid.Empty)
                {
                    DateTime newest = DateTime.MinValue;
                    foreach (var kvp in _entries)
                    {
                        if (kvp.Value != null && EntryMatchesAccount(kvp.Value, accountGuid) && kvp.Value.CreatedUtc >= newest)
                        {
                            newest = kvp.Value.CreatedUtc;
                            entry = kvp.Value;
                        }
                    }
                    return entry != null;
                }

                return false;
            }
        }

        public bool IsAccountInMission(Guid accountId)
        {
            if (accountId == Guid.Empty)
            {
                return false;
            }

            lock (_lock)
            {
                foreach (var kvp in _entries)
                {
                    if (kvp.Value != null && EntryMatchesAccount(kvp.Value, accountId))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        private static bool EntryMatchesAccount(ActiveMissionEntry entry, Guid accountId)
        {
            if (entry == null || accountId == Guid.Empty)
            {
                return false;
            }

            if (entry.AccountId == accountId)
            {
                return true;
            }

            var participants = entry.ParticipantAccountIds;
            if (participants != null)
            {
                for (var i = 0; i < participants.Length; i++)
                {
                    if (participants[i] == accountId)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public void SetNextSeedOverride(uint seed0, uint seed1, uint seed2, uint seed3, bool sticky)
        {
            lock (_lock)
            {
                _hasNextSeedOverride = true;
                _nextSeedSticky = sticky;
                _nextSeed0 = seed0;
                _nextSeed1 = seed1;
                _nextSeed2 = seed2;
                _nextSeed3 = seed3;
            }
        }

        public void ClearNextSeedOverride()
        {
            lock (_lock)
            {
                _hasNextSeedOverride = false;
                _nextSeedSticky = false;
                _nextSeed0 = 0u;
                _nextSeed1 = 0u;
                _nextSeed2 = 0u;
                _nextSeed3 = 0u;
            }
        }

        public uint[] GetNextSeedOverrideSnapshot(out bool sticky)
        {
            lock (_lock)
            {
                sticky = _nextSeedSticky;
                if (!_hasNextSeedOverride)
                {
                    return null;
                }
                return new uint[] { _nextSeed0, _nextSeed1, _nextSeed2, _nextSeed3 };
            }
        }

        public bool TryConsumeNextSeeds(out uint seed0, out uint seed1, out uint seed2, out uint seed3)
        {
            lock (_lock)
            {
                if (!_hasNextSeedOverride)
                {
                    seed0 = 0u;
                    seed1 = 0u;
                    seed2 = 0u;
                    seed3 = 0u;
                    return false;
                }

                seed0 = _nextSeed0;
                seed1 = _nextSeed1;
                seed2 = _nextSeed2;
                seed3 = _nextSeed3;

                if (!_nextSeedSticky)
                {
                    _hasNextSeedOverride = false;
                }
                return true;
            }
        }

        private static bool TryParseGuid(string raw, out Guid result)
        {
            result = Guid.Empty;
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }
            try
            {
                result = new Guid(raw.Trim());
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
