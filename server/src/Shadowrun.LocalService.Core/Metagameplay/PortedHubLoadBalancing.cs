using System;
using System.Collections.Generic;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal interface IPortedHubLoadBalancing
    {
        PortedHubInstance GetHub(string hubName, GroupStatus groupStatus, Dictionary<string, List<PortedHubInstance>> hubPool);

        void AccountEnteredHub(Guid accountId, PortedHubInstance targetHubInstance);
    }

    internal sealed class PortedHubLoadBalancing : IPortedHubLoadBalancing
    {
        public PortedHubInstance GetHub(string hubName, GroupStatus groupStatus, Dictionary<string, List<PortedHubInstance>> hubPool)
        {
            if (string.IsNullOrEmpty(hubName) || hubPool == null || !hubPool.ContainsKey(hubName))
            {
                return null;
            }

            var normalizedGroupStatus = groupStatus ?? new GroupStatus();
            if (normalizedGroupStatus.IsCoop)
            {
                var existingHub = GetHubContainingAGroupMember(hubPool[hubName], normalizedGroupStatus);
                if (existingHub != null && existingHub.HardCap > existingHub.NumberOfContainedPlayerCharacters())
                {
                    return existingHub;
                }
            }

            var hubInstances = GetNonFullHubs(hubPool[hubName], normalizedGroupStatus);
            if (hubInstances.Count > 0)
            {
                var nonEmpty = GetNonEmptyHubs(hubInstances);
                return nonEmpty.Count > 0 ? GetEmptiestHub(nonEmpty) : hubInstances[0];
            }

            return null;
        }

        public void AccountEnteredHub(Guid accountId, PortedHubInstance targetHubInstance)
        {
        }

        private static PortedHubInstance GetHubContainingAGroupMember(List<PortedHubInstance> hubInstances, GroupStatus groupStatus)
        {
            if (hubInstances == null || groupStatus == null)
            {
                return null;
            }

            var hostHub = hubInstances.FirstOrDefault(h => h != null && h.PlayerIsInHub(groupStatus.GroupHostCharacterId));
            if (hostHub != null)
            {
                return hostHub;
            }

            return hubInstances.FirstOrDefault(h => h != null && h.GroupMemberIsInHub(groupStatus));
        }

        private static PortedHubInstance GetEmptiestHub(List<PortedHubInstance> hubInstances)
        {
            if (hubInstances == null || hubInstances.Count == 0)
            {
                return null;
            }

            var minPlayers = hubInstances.Min(h => h.NumberOfContainedPlayerCharacters());
            return hubInstances.FirstOrDefault(h => h.NumberOfContainedPlayerCharacters() == minPlayers);
        }

        private static List<PortedHubInstance> GetNonEmptyHubs(IEnumerable<PortedHubInstance> hubInstances)
        {
            return hubInstances.Where(h => h != null && h.NumberOfContainedPlayerCharacters() > 0).ToList();
        }

        private static List<PortedHubInstance> GetNonFullHubs(IEnumerable<PortedHubInstance> hubInstances, GroupStatus groupStatus)
        {
            return hubInstances.Where(h => h != null && (RequiredSpaceInHub(groupStatus, h) <= h.SoftCap || (groupStatus.IsCoop && HubIsEmptyAndHasEnoughSpace(groupStatus, h)))).ToList();
        }

        private static bool HubIsEmptyAndHasEnoughSpace(GroupStatus groupStatus, PortedHubInstance hubInstance)
        {
            return hubInstance != null
                && RequiredSpaceInHub(groupStatus, hubInstance) <= hubInstance.HardCap
                && hubInstance.NumberOfContainedPlayerCharacters() == 0;
        }

        private static int RequiredSpaceInHub(GroupStatus groupStatus, PortedHubInstance hubInstance)
        {
            return hubInstance.NumberOfContainedPlayerCharacters() + groupStatus.RequiredSpace();
        }
    }

    internal sealed class PortedBasicHubLoadBalancing : IPortedHubLoadBalancing
    {
        private readonly object _sync = new object();

        public PortedHubInstance GetHub(string hubName, GroupStatus groupStatus, Dictionary<string, List<PortedHubInstance>> hubPool)
        {
            lock (_sync)
            {
                if (string.IsNullOrEmpty(hubName) || hubPool == null || !hubPool.ContainsKey(hubName))
                {
                    return null;
                }

                return hubPool[hubName].FirstOrDefault();
            }
        }

        public void AccountEnteredHub(Guid accountId, PortedHubInstance targetHubInstance)
        {
        }
    }

    internal sealed class PortedMassTestingHubLoadBalancing : IPortedHubLoadBalancing
    {
        private readonly Dictionary<Guid, HashSet<PortedHubInstance>> _alreadyUsedHubs = new Dictionary<Guid, HashSet<PortedHubInstance>>();

        public PortedHubInstance GetHub(string hubName, GroupStatus groupStatus, Dictionary<string, List<PortedHubInstance>> hubPool)
        {
            if (string.IsNullOrEmpty(hubName) || groupStatus == null || hubPool == null)
            {
                return null;
            }

            List<PortedHubInstance> instances;
            if (!hubPool.TryGetValue(hubName, out instances) || instances == null)
            {
                return null;
            }

            var availableInstances = instances.Where(i => i != null && i.NumberOfContainedPlayerCharacters() < i.SoftCap).ToArray();
            Guid hostAccountId;
            if (!TryParseGuidPrefix(groupStatus.GroupHostCharacterId, out hostAccountId))
            {
                return availableInstances.FirstOrDefault();
            }

            HashSet<PortedHubInstance> usedHubs;
            if (!_alreadyUsedHubs.TryGetValue(hostAccountId, out usedHubs))
            {
                return availableInstances.FirstOrDefault();
            }

            return availableInstances.Except(usedHubs).FirstOrDefault() ?? availableInstances.FirstOrDefault();
        }

        public void AccountEnteredHub(Guid accountId, PortedHubInstance targetHubInstance)
        {
            if (accountId == Guid.Empty || targetHubInstance == null)
            {
                return;
            }

            HashSet<PortedHubInstance> instances;
            if (!_alreadyUsedHubs.TryGetValue(accountId, out instances) || instances == null)
            {
                instances = new HashSet<PortedHubInstance>();
                _alreadyUsedHubs[accountId] = instances;
            }

            instances.Add(targetHubInstance);
        }

        private static bool TryParseGuidPrefix(string characterIdentifier, out Guid accountId)
        {
            accountId = Guid.Empty;
            if (string.IsNullOrEmpty(characterIdentifier))
            {
                return false;
            }

            var separator = characterIdentifier.IndexOf(':');
            var guidPart = separator > 0 ? characterIdentifier.Substring(0, separator) : characterIdentifier;
            try
            {
                accountId = new Guid(guidPart);
                return accountId != Guid.Empty;
            }
            catch
            {
                return false;
            }
        }
    }
}
