using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Hub;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class PortedHubStateController
    {
        private readonly HubState _hubState;

        public PortedHubStateController(string hubName, string hubId)
        {
            _hubState = new HubState
            {
                Name = hubName,
                HubId = hubId,
            };
        }

        public HubState HubState
        {
            get { return _hubState; }
        }

        public void UpdateCharacterSnapshot(IPlayerCharacterSnapshot playerCharacterSnapshot)
        {
            if (playerCharacterSnapshot == null || string.IsNullOrEmpty(playerCharacterSnapshot.CharacterIdentifier))
            {
                return;
            }

            HubPlayerCharacter playerCharacter;
            if (_hubState.PlayerCharacters.TryGetValue(playerCharacterSnapshot.CharacterIdentifier, out playerCharacter) && playerCharacter != null)
            {
                playerCharacter.Snapshot = playerCharacterSnapshot;
            }
        }

        public HubStateUpdate AddCharacter(IPlayerCharacterSnapshot playerCharacterSnapshot, Vector2D playerCharacterStart)
        {
            if (playerCharacterSnapshot == null || string.IsNullOrEmpty(playerCharacterSnapshot.CharacterIdentifier) || InInstance(playerCharacterSnapshot.CharacterIdentifier))
            {
                return null;
            }

            return HubStateUpdate.CreateForCharacterAddtion(_hubState.Add(playerCharacterSnapshot, playerCharacterStart), _hubState.HubId);
        }

        public HubStateUpdate RemoveCharacter(string identifier)
        {
            if (!InInstance(identifier))
            {
                return null;
            }

            _hubState.Remove(identifier);
            return HubStateUpdate.CreateForRemoveCharacter(identifier, _hubState.HubId);
        }

        public bool InInstance(string identifier)
        {
            return !string.IsNullOrEmpty(identifier) && _hubState.PlayerCharacters.ContainsKey(identifier);
        }

        public void UpdateCharacterPosition(string identifier, Vector2D position)
        {
            if (!InInstance(identifier))
            {
                return;
            }

            _hubState.PlayerCharacters[identifier].CurrentPosition = position;
        }

        public Dictionary<string, Vector2D> GetLastKnownInstancePositions()
        {
            var positions = new Dictionary<string, Vector2D>();
            foreach (var kvp in _hubState.PlayerCharacters)
            {
                if (kvp.Value != null)
                {
                    positions[kvp.Key] = kvp.Value.CurrentPosition;
                }
            }

            return positions;
        }

        public void Clear()
        {
            _hubState.PlayerCharacters.Clear();
        }
    }
}
