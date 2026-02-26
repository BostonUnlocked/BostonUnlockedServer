using System;
using System.Collections.Generic;

namespace Shadowrun.LocalService.Core.Protocols
{
    internal static class CoopGroupHostRegistry
    {
        private sealed class Entry
        {
            public Guid Leader;
            public DateTime UpdatedUtc;

            public Entry(Guid leader)
            {
                Leader = leader;
                UpdatedUtc = DateTime.UtcNow;
            }
        }

        private static readonly object LockObj = new object();
        private static readonly Dictionary<string, Entry> LeaderByGroupName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static string NormalizeGroupName(string coopGroupName)
        {
            if (string.IsNullOrEmpty(coopGroupName))
            {
                return string.Empty;
            }

            var trimmed = coopGroupName.Trim();
            var onIdx = trimmed.IndexOf("_On_", StringComparison.OrdinalIgnoreCase);
            if (onIdx > 0)
            {
                return trimmed.Substring(0, onIdx);
            }

            return trimmed;
        }

        public static void SetLeader(string coopGroupName, Guid leaderAccountId)
        {
            if (string.IsNullOrEmpty(coopGroupName) || leaderAccountId == Guid.Empty)
            {
                return;
            }

            var key = NormalizeGroupName(coopGroupName);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            lock (LockObj)
            {
                Entry existing;
                if (LeaderByGroupName.TryGetValue(key, out existing) && existing != null)
                {
                    existing.Leader = leaderAccountId;
                    existing.UpdatedUtc = DateTime.UtcNow;
                    return;
                }
                LeaderByGroupName[key] = new Entry(leaderAccountId);
            }
        }

        public static bool TryGetLeader(string coopGroupName, out Guid leaderAccountId)
        {
            leaderAccountId = Guid.Empty;
            if (string.IsNullOrEmpty(coopGroupName))
            {
                return false;
            }

            var key = NormalizeGroupName(coopGroupName);
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            lock (LockObj)
            {
                Entry existing;
                if (!LeaderByGroupName.TryGetValue(key, out existing) || existing == null)
                {
                    return false;
                }
                leaderAccountId = existing.Leader;
                return leaderAccountId != Guid.Empty;
            }
        }
    }
}
