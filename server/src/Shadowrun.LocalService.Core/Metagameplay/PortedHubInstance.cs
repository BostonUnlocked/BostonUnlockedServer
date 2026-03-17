using System;
using System.Collections.Generic;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Hub;
using SRO.Core.Compatibility.Math;
using SRO.Core.Compatibility.Serialization;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class PortedHubInstance
    {
        private readonly PortedHubStateController _hubStateController;
        private readonly string _hubName;
        private readonly Vector2D _playerCharacterStart;
        private readonly DateTime _creationTime;
        private readonly object _sync = new object();
        private Dictionary<string, Vector2D> _moveRequests = new Dictionary<string, Vector2D>();

        public PortedHubInstance(string name, int softCap, int hardCap, SerializableVector3 playerCharacterStart, string hubId, DateTime creationTime)
        {
            HubId = hubId ?? string.Empty;
            SoftCap = softCap;
            HardCap = hardCap;
            _hubName = name ?? string.Empty;
            _hubStateController = new PortedHubStateController(_hubName, HubId);
            _creationTime = creationTime;
            _playerCharacterStart = playerCharacterStart != null ? new Vector2D(playerCharacterStart.X, playerCharacterStart.Z) : new Vector2D(0f, 0f);
        }

        public string HubId { get; private set; }

        public DateTime CreationTime
        {
            get { return _creationTime; }
        }

        public int SoftCap { get; private set; }

        public int HardCap { get; private set; }

        public HubState HubState
        {
            get { return _hubStateController.HubState; }
        }

        public string SerializedHubState()
        {
            lock (_sync)
            {
                return HubSerializer.SerializeHubState(_hubStateController.HubState);
            }
        }

        public string EmptySerializedHubState()
        {
            return HubSerializer.SerializeHubState(new HubState
            {
                HubId = HubId,
                Name = _hubName,
                PlayerCharacters = new Dictionary<string, HubPlayerCharacter>(),
            });
        }

        public int NumberOfContainedPlayerCharacters()
        {
            lock (_sync)
            {
                return _hubStateController.HubState.PlayerCharacters.Count;
            }
        }

        public bool PlayerIsInHub(string characterId)
        {
            lock (_sync)
            {
                return _hubStateController.HubState.PlayerCharacters.Any(p => characterId == p.Key);
            }
        }

        public bool GroupMemberIsInHub(GroupStatus groupStatus)
        {
            if (groupStatus == null || groupStatus.MemberCharacterIds == null)
            {
                return false;
            }

            lock (_sync)
            {
                return _hubStateController.HubState.PlayerCharacters.Any(p => groupStatus.MemberCharacterIds.Any(g => g == p.Key));
            }
        }

        public HubStateUpdate RemoveCharacter(string characterIdentifier)
        {
            lock (_sync)
            {
                return _hubStateController.RemoveCharacter(characterIdentifier);
            }
        }

        public HubStateUpdate AddCharacter(IPlayerCharacterSnapshot playerCharacterSnapshot)
        {
            lock (_sync)
            {
                return _hubStateController.AddCharacter(playerCharacterSnapshot, _playerCharacterStart);
            }
        }

        public void UpdateCharacterPosition(string characterIdentifier, Vector2D position)
        {
            lock (_sync)
            {
                _hubStateController.UpdateCharacterPosition(characterIdentifier, position);
            }
        }

        public void UpdatePlayerCharacterSnapshot(IPlayerCharacterSnapshot playerCharacterSnapshot)
        {
            lock (_sync)
            {
                if (playerCharacterSnapshot != null && PlayerIsInHub(playerCharacterSnapshot.CharacterIdentifier))
                {
                    _hubStateController.UpdateCharacterSnapshot(playerCharacterSnapshot);
                }
            }
        }

        public string SerializeHubStateChange(HubStateUpdate result)
        {
            return result != null ? HubSerializer.SerializeHubStateUpdate(result) : null;
        }

        public void QueueMoveRequest(string characterId, Vector2D position)
        {
            if (string.IsNullOrEmpty(characterId))
            {
                return;
            }

            lock (_sync)
            {
                _hubStateController.UpdateCharacterPosition(characterId, position);
                _moveRequests[characterId] = position;
            }
        }

        public string FlushMoveRequests()
        {
            Dictionary<string, Vector2D> entries;
            lock (_sync)
            {
                if (_moveRequests.Count <= 0)
                {
                    return null;
                }

                entries = _moveRequests;
                _moveRequests = new Dictionary<string, Vector2D>();
            }

            return HubMovementSerializer.Serialize(entries);
        }

        public override string ToString()
        {
            return string.Format("PortedHubInstance(id: {0}, hubName: {1})", HubId, _hubName);
        }
    }
}
