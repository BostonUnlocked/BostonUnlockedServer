using System;
using System.Collections.Generic;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Hub;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class PortedHubTransitionResult
    {
        public PortedHubInstance PreviousHubInstance { get; set; }

        public PortedHubInstance TargetHubInstance { get; set; }

        public HubStateUpdate LeaveUpdate { get; set; }

        public HubStateUpdate JoinUpdate { get; set; }

        public bool ReusedExistingHub { get; set; }

        public bool IsSameHub
        {
            get
            {
                return PreviousHubInstance != null
                    && TargetHubInstance != null
                    && string.Equals(PreviousHubInstance.HubId, TargetHubInstance.HubId, StringComparison.Ordinal);
            }
        }
    }

    internal sealed class PortedHubStateReport
    {
        public string Id { get; set; }

        public string HubName { get; set; }

        public int CurrentPlayers { get; set; }

        public int SoftCap { get; set; }

        public int HardCap { get; set; }

        public TimeSpan TimeAlive { get; set; }
    }

    internal sealed class PortedHubInstanceManager
    {
        private readonly PortedHubRepository _hubRepository;
        private readonly object _sync = new object();
        private readonly Dictionary<string, List<PortedHubInstance>> _hubPool;
        private IPortedHubLoadBalancing _balancer;

        public PortedHubInstanceManager(PortedHubRepository hubRepository, bool massTestingEnabled)
        {
            _hubRepository = hubRepository;
            _hubPool = new Dictionary<string, List<PortedHubInstance>>(StringComparer.OrdinalIgnoreCase);
            _balancer = massTestingEnabled
                ? (IPortedHubLoadBalancing)new PortedMassTestingHubLoadBalancing()
                : new PortedHubLoadBalancing();
        }

        public void SwitchLoadBalancing(IPortedHubLoadBalancing loadBalancing)
        {
            if (loadBalancing == null)
            {
                return;
            }

            lock (_sync)
            {
                _balancer = loadBalancing;
            }
        }

        public PortedHubTransitionResult ExecuteRequestHubInstance(string hubName, IPlayerCharacterSnapshot playerCharacterSnapshot)
        {
            return ExecuteRequestHubInstance(hubName, playerCharacterSnapshot, null, new GroupStatus());
        }

        public PortedHubTransitionResult ExecuteRequestHubInstance(PortedHubInstance targetHubInstance, IPlayerCharacterSnapshot playerCharacterSnapshot, PortedHubInstance currentHubInstance)
        {
            if (targetHubInstance == null || playerCharacterSnapshot == null)
            {
                return null;
            }

            if (HubInstancesExistingAndTheSame(currentHubInstance, targetHubInstance))
            {
                return new PortedHubTransitionResult
                {
                    PreviousHubInstance = currentHubInstance,
                    TargetHubInstance = currentHubInstance,
                    ReusedExistingHub = true,
                };
            }

            lock (_sync)
            {
                _balancer.AccountEnteredHub(playerCharacterSnapshot.AccountId, targetHubInstance);
            }

            var leaveUpdate = RemoveCharacterFromHubInstanceWithoutLock(currentHubInstance, playerCharacterSnapshot.CharacterIdentifier);
            var joinUpdate = targetHubInstance.AddCharacter(playerCharacterSnapshot);

            return new PortedHubTransitionResult
            {
                PreviousHubInstance = currentHubInstance,
                TargetHubInstance = targetHubInstance,
                LeaveUpdate = leaveUpdate,
                JoinUpdate = joinUpdate,
                ReusedExistingHub = true,
            };
        }

        public PortedHubTransitionResult ExecuteRequestExactHubInstance(string hubId, IPlayerCharacterSnapshot playerCharacterSnapshot, PortedHubInstance currentHubInstance)
        {
            if (string.IsNullOrEmpty(hubId) || playerCharacterSnapshot == null)
            {
                return null;
            }

            PortedHubInstance targetHubInstance;
            lock (_sync)
            {
                targetHubInstance = _hubPool
                    .SelectMany(kvp => kvp.Value)
                    .FirstOrDefault(h => h != null && string.Equals(h.HubId, hubId, StringComparison.OrdinalIgnoreCase));

                if (targetHubInstance == null)
                {
                    targetHubInstance = CreateNewHubInstanceWithId(hubId);
                }

                _balancer.AccountEnteredHub(playerCharacterSnapshot.AccountId, targetHubInstance);
            }

            if (HubInstancesExistingAndTheSame(currentHubInstance, targetHubInstance))
            {
                return new PortedHubTransitionResult
                {
                    PreviousHubInstance = currentHubInstance,
                    TargetHubInstance = currentHubInstance,
                    ReusedExistingHub = true,
                };
            }

            var leaveUpdate = RemoveCharacterFromHubInstanceWithoutLock(currentHubInstance, playerCharacterSnapshot.CharacterIdentifier);
            var joinUpdate = targetHubInstance.AddCharacter(playerCharacterSnapshot);

            return new PortedHubTransitionResult
            {
                PreviousHubInstance = currentHubInstance,
                TargetHubInstance = targetHubInstance,
                LeaveUpdate = leaveUpdate,
                JoinUpdate = joinUpdate,
                ReusedExistingHub = false,
            };
        }

        public PortedHubTransitionResult ExecuteRequestHubInstance(string hubName, IPlayerCharacterSnapshot playerCharacterSnapshot, PortedHubInstance currentHubInstance, GroupStatus groupStatus)
        {
            if (string.IsNullOrEmpty(hubName) || playerCharacterSnapshot == null)
            {
                return null;
            }

            PortedHubInstance selectedHubInstance;
            lock (_sync)
            {
                selectedHubInstance = _balancer.GetHub(hubName, groupStatus ?? new GroupStatus(), _hubPool);
            }

            if (HubInstancesExistingAndTheSame(currentHubInstance, selectedHubInstance))
            {
                return new PortedHubTransitionResult
                {
                    PreviousHubInstance = currentHubInstance,
                    TargetHubInstance = currentHubInstance,
                    ReusedExistingHub = true,
                };
            }

            PortedHubInstance targetHubInstance;
            lock (_sync)
            {
                targetHubInstance = CreateNewHubInstanceIfNecessary(hubName, selectedHubInstance);
                _balancer.AccountEnteredHub(playerCharacterSnapshot.AccountId, targetHubInstance);
            }

            var leaveUpdate = RemoveCharacterFromHubInstanceWithoutLock(currentHubInstance, playerCharacterSnapshot.CharacterIdentifier);
            var joinUpdate = targetHubInstance.AddCharacter(playerCharacterSnapshot);

            return new PortedHubTransitionResult
            {
                PreviousHubInstance = currentHubInstance,
                TargetHubInstance = targetHubInstance,
                LeaveUpdate = leaveUpdate,
                JoinUpdate = joinUpdate,
                ReusedExistingHub = selectedHubInstance != null,
            };
        }

        public HubStateUpdate RemoveCharacterFromHub(PortedHubInstance currentHubInstance, string characterIdentifier)
        {
            if (string.IsNullOrEmpty(characterIdentifier))
            {
                return null;
            }

            return RemoveCharacterFromHubInstanceWithoutLock(currentHubInstance, characterIdentifier);
        }

        public PortedHubInstance RequestHubInstance(string characterId)
        {
            if (string.IsNullOrEmpty(characterId))
            {
                return null;
            }

            lock (_sync)
            {
                return _hubPool
                    .SelectMany(kvp => kvp.Value)
                    .FirstOrDefault(h => h != null && h.PlayerIsInHub(characterId));
            }
        }

        public PortedHubInstance RequestHubInstanceByHubId(string hubId)
        {
            if (string.IsNullOrEmpty(hubId))
            {
                return null;
            }

            lock (_sync)
            {
                return _hubPool
                    .SelectMany(kvp => kvp.Value)
                    .FirstOrDefault(h => h != null && string.Equals(h.HubId, hubId, StringComparison.OrdinalIgnoreCase));
            }
        }

        public void QueueMoveRequest(string characterId, Vector2D position)
        {
            var hubInstance = RequestHubInstance(characterId);
            if (hubInstance != null)
            {
                hubInstance.QueueMoveRequest(characterId, position);
            }
        }

        public void UpdatePlayerCharacterSnapshot(IPlayerCharacterSnapshot playerCharacterSnapshot)
        {
            if (playerCharacterSnapshot == null || string.IsNullOrEmpty(playerCharacterSnapshot.CharacterIdentifier))
            {
                return;
            }

            var hubInstance = RequestHubInstance(playerCharacterSnapshot.CharacterIdentifier);
            if (hubInstance != null)
            {
                hubInstance.UpdatePlayerCharacterSnapshot(playerCharacterSnapshot);
            }
        }

        public IEnumerable<KeyValuePair<PortedHubInstance, string>> FlushMoveRequests()
        {
            List<PortedHubInstance> hubInstances;
            lock (_sync)
            {
                hubInstances = _hubPool.SelectMany(kvp => kvp.Value).Where(h => h != null).ToList();
            }

            foreach (var hubInstance in hubInstances)
            {
                var serializedMoves = hubInstance.FlushMoveRequests();
                if (!string.IsNullOrEmpty(serializedMoves))
                {
                    yield return new KeyValuePair<PortedHubInstance, string>(hubInstance, serializedMoves);
                }
            }
        }

        public PortedHubStateReport[] GetHubInstanceState()
        {
            lock (_sync)
            {
                return _hubPool
                    .SelectMany(kvp => kvp.Value)
                    .Where(instance => instance != null)
                    .Select(instance => CreateReport(instance))
                    .ToArray();
            }
        }

        private static bool HubInstancesExistingAndTheSame(PortedHubInstance currentHubInstance, PortedHubInstance hubInstance)
        {
            return currentHubInstance != null
                && hubInstance != null
                && string.Equals(currentHubInstance.HubId, hubInstance.HubId, StringComparison.Ordinal);
        }

        private PortedHubInstance CreateNewHubInstanceIfNecessary(string hubName, PortedHubInstance hubInstance)
        {
            List<PortedHubInstance> instances;
            if (!_hubPool.TryGetValue(hubName, out instances) || instances == null)
            {
                instances = new List<PortedHubInstance>();
                _hubPool[hubName] = instances;
            }

            if (hubInstance != null)
            {
                return hubInstance;
            }

            return CreateNewHubInstanceWithId(CreateHubInstanceId(hubName));
        }

        private PortedHubInstance CreateNewHubInstanceWithId(string hubId)
        {
            var hubName = GetHubNameFromInstanceId(hubId);
            List<PortedHubInstance> instances;
            if (!_hubPool.TryGetValue(hubName, out instances) || instances == null)
            {
                instances = new List<PortedHubInstance>();
                _hubPool[hubName] = instances;
            }

            var hubData = _hubRepository != null ? _hubRepository.GetHub(hubName) : null;
            var softCap = hubData != null && hubData.PlayerLimit != null ? hubData.PlayerLimit.SoftCap : 20;
            var hardCap = hubData != null && hubData.PlayerLimit != null ? hubData.PlayerLimit.HardCap : 25;
            var playerStart = hubData != null ? hubData.PlayerCharacterStart : null;
            var createdHub = new PortedHubInstance(hubName, softCap, hardCap, playerStart, hubId, DateTime.UtcNow);
            instances.Add(createdHub);
            return createdHub;
        }

        private static HubStateUpdate RemoveCharacterFromHubInstanceWithoutLock(PortedHubInstance currentHubInstance, string characterIdentifier)
        {
            return currentHubInstance != null ? currentHubInstance.RemoveCharacter(characterIdentifier) : null;
        }

        private static string CreateHubInstanceId(string hubName)
        {
            return string.Format("{0}#{1}", hubName ?? string.Empty, Guid.NewGuid().ToString("N"));
        }

        private static string GetHubNameFromInstanceId(string hubId)
        {
            if (string.IsNullOrEmpty(hubId))
            {
                return string.Empty;
            }

            var separatorIndex = hubId.IndexOf('#');
            return separatorIndex > 0 ? hubId.Substring(0, separatorIndex) : hubId;
        }

        private static PortedHubStateReport CreateReport(PortedHubInstance instance)
        {
            return new PortedHubStateReport
            {
                Id = instance.HubId,
                HubName = instance.HubState != null ? instance.HubState.Name : string.Empty,
                CurrentPlayers = instance.NumberOfContainedPlayerCharacters(),
                SoftCap = instance.SoftCap,
                HardCap = instance.HardCap,
                TimeAlive = DateTime.UtcNow - instance.CreationTime,
            };
        }
    }
}
