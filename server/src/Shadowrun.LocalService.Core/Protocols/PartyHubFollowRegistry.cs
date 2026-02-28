using System;
using System.Collections.Generic;

namespace Shadowrun.LocalService.Core.Protocols
{
    internal static class PartyHubFollowRegistry
    {
        private static readonly object LockObj = new object();
        private static readonly Dictionary<Guid, Guid> HostByMember = new Dictionary<Guid, Guid>();

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

            lock (LockObj)
            {
                return HostByMember.TryGetValue(memberAccountId, out hostAccountId) && hostAccountId != Guid.Empty;
            }
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
