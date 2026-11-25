using System;
using System.Collections.Generic;

namespace Shadowrun.LocalService.Core.Protocols
{
    internal static class PartyHubFollowRegistry
    {
        internal delegate bool GroupMembershipValidator(Guid memberAccountId, Guid hostAccountId);

        private sealed class PendingHostVictoryEntry
        {
            public readonly string MissionMapName;
            public readonly DateTime RecordedUtc;

            public PendingHostVictoryEntry(string missionMapName, DateTime recordedUtc)
            {
                MissionMapName = missionMapName;
                RecordedUtc = recordedUtc;
            }
        }

        private static readonly TimeSpan PendingHostVictoryTtl = TimeSpan.FromMinutes(5);
        private static readonly object LockObj = new object();
        private static readonly Dictionary<Guid, Guid> HostByMember = new Dictionary<Guid, Guid>();
        private static readonly Dictionary<Guid, PendingHostVictoryEntry> PendingVictoryByHost = new Dictionary<Guid, PendingHostVictoryEntry>();
        private static GroupMembershipValidator _groupMembershipValidator;

        public static void SetGroupMembershipValidator(GroupMembershipValidator validator)
        {
            lock (LockObj)
            {
                _groupMembershipValidator = validator;
            }
        }

        public static void ClearGroupMembershipValidator(GroupMembershipValidator validator)
        {
            if (validator == null)
            {
                return;
            }

            lock (LockObj)
            {
                if (object.Equals(_groupMembershipValidator, validator))
                {
                    _groupMembershipValidator = null;
                }
            }
        }

        public static void SetPendingHostVictoryMission(Guid hostAccountId, string missionMapName)
        {
            if (hostAccountId == Guid.Empty || string.IsNullOrEmpty(missionMapName))
            {
                return;
            }

            lock (LockObj)
            {
                PendingVictoryByHost[hostAccountId] = new PendingHostVictoryEntry(missionMapName, DateTime.UtcNow);
            }
        }

        public static bool TryGetPendingHostVictoryMission(Guid hostAccountId, out string missionMapName)
        {
            missionMapName = null;
            if (hostAccountId == Guid.Empty)
            {
                return false;
            }

            lock (LockObj)
            {
                PendingHostVictoryEntry entry;
                if (!PendingVictoryByHost.TryGetValue(hostAccountId, out entry) || entry == null)
                {
                    return false;
                }

                if (DateTime.UtcNow - entry.RecordedUtc > PendingHostVictoryTtl || string.IsNullOrEmpty(entry.MissionMapName))
                {
                    PendingVictoryByHost.Remove(hostAccountId);
                    return false;
                }

                missionMapName = entry.MissionMapName;
                return true;
            }
        }

        public static void ClearPendingHostVictoryMission(Guid hostAccountId)
        {
            if (hostAccountId == Guid.Empty)
            {
                return;
            }

            lock (LockObj)
            {
                PendingVictoryByHost.Remove(hostAccountId);
            }
        }

        public static void SetHostForMember(Guid memberAccountId, Guid hostAccountId)
        {
            if (memberAccountId == Guid.Empty)
            {
                return;
            }

            lock (LockObj)
            {
                if (hostAccountId == Guid.Empty || hostAccountId == memberAccountId)
                {
                    HostByMember.Remove(memberAccountId);
                    return;
                }

                HostByMember[memberAccountId] = hostAccountId;
            }
        }

        public static bool TryGetHostForMember(Guid memberAccountId, out Guid hostAccountId)
        {
            hostAccountId = Guid.Empty;
            if (memberAccountId == Guid.Empty)
            {
                return false;
            }

            Guid candidateHost;
            GroupMembershipValidator validator;
            lock (LockObj)
            {
                if (!HostByMember.TryGetValue(memberAccountId, out candidateHost) || candidateHost == Guid.Empty)
                {
                    return false;
                }

                validator = _groupMembershipValidator;
            }

            if (validator != null)
            {
                var stillInParty = false;
                try
                {
                    stillInParty = validator(memberAccountId, candidateHost);
                }
                catch
                {
                    stillInParty = false;
                }

                if (!stillInParty)
                {
                    lock (LockObj)
                    {
                        Guid currentHost;
                        if (HostByMember.TryGetValue(memberAccountId, out currentHost) && currentHost == candidateHost)
                        {
                            HostByMember.Remove(memberAccountId);
                        }
                    }

                    return false;
                }
            }

            hostAccountId = candidateHost;
            return true;
        }

        public static void ClearForMember(Guid memberAccountId)
        {
            if (memberAccountId == Guid.Empty)
            {
                return;
            }

            lock (LockObj)
            {
                HostByMember.Remove(memberAccountId);
            }
        }

        public static void ClearMembersFollowingHost(Guid hostAccountId)
        {
            if (hostAccountId == Guid.Empty)
            {
                return;
            }

            lock (LockObj)
            {
                if (HostByMember.Count == 0)
                {
                    return;
                }

                var membersToClear = new List<Guid>();
                foreach (var kvp in HostByMember)
                {
                    if (kvp.Value == hostAccountId)
                    {
                        membersToClear.Add(kvp.Key);
                    }
                }

                for (var i = 0; i < membersToClear.Count; i++)
                {
                    HostByMember.Remove(membersToClear[i]);
                }
            }
        }

        public static void ClearForGroup(IEnumerable<Guid> memberAccountIds)
        {
            if (memberAccountIds == null)
            {
                return;
            }

            lock (LockObj)
            {
                foreach (var memberAccountId in memberAccountIds)
                {
                    if (memberAccountId != Guid.Empty)
                    {
                        HostByMember.Remove(memberAccountId);
                    }
                }
            }
        }
    }
}
