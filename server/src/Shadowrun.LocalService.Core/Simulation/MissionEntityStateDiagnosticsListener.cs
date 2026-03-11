using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.GameLogic;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.EntityStateChange;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Locomotion;

namespace Shadowrun.LocalService.Core.Simulation
{
    internal sealed class MissionEntityStateDiagnosticsListener : IEntityStateChangeListener
    {
        private readonly RequestLogger _logger;
        private readonly string _peer;
        private readonly EntitySystem _entitySystem;

        public MissionEntityStateDiagnosticsListener(RequestLogger logger, string peer, EntitySystem entitySystem)
        {
            _logger = logger;
            _peer = peer;
            _entitySystem = entitySystem;
        }

        public void OnEntityDied(Entity entity)
        {
            LogStateChange("entity-died", entity);
        }

        public void OnEntityActivated(Entity entity)
        {
            LogStateChange("entity-activated", entity);
        }

        public void OnEntityDeactivated(Entity entity)
        {
            LogStateChange("entity-deactivated", entity);
        }

        public void OnEntityDespawned(Entity entity)
        {
            LogStateChange("entity-despawned", entity);
        }

        private void LogStateChange(string status, Entity entity)
        {
            if (_logger == null || _entitySystem == null || entity == null)
            {
                return;
            }

            try
            {
                CharacterSpawnInfoComponent spawnInfo;
                var hasSpawnInfo = _entitySystem.TryGetComponent<CharacterSpawnInfoComponent>(entity, out spawnInfo) && spawnInfo != null;

                CharacterStateComponent characterState;
                var hasCharacterState = _entitySystem.TryGetComponent<CharacterStateComponent>(entity, out characterState) && characterState != null;

                TeamComponent team;
                var hasTeam = _entitySystem.TryGetComponent<TeamComponent>(entity, out team) && team != null;

                ControlComponent control;
                var hasControl = _entitySystem.TryGetComponent<ControlComponent>(entity, out control) && control != null;

                IPositionComponent position;
                var hasPosition = _entitySystem.TryGetComponent<IPositionComponent>(entity, out position) && position != null;

                var currentHealth = (int)_entitySystem.GetStatusValueOrDefault(entity, 458753UL);
                int? maxHealth = null;
                AttributeBackedStatusValueContainer statusValues;
                if (_entitySystem.TryGetComponent<AttributeBackedStatusValueContainer>(entity, out statusValues)
                    && statusValues != null)
                {
                    try
                    {
                        maxHealth = (int)statusValues[458753UL].MaxValue;
                    }
                    catch
                    {
                        maxHealth = null;
                    }
                }

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-entity-state",
                    peer = _peer,
                    status = status,
                    entityId = entity.Id,
                    hasSpawnInfo = hasSpawnInfo,
                    spawnManagerTag = hasSpawnInfo ? spawnInfo.SpawnManagerTag : null,
                    teamId = hasTeam ? (int?)team.TeamID : null,
                    aiControlled = hasControl ? (bool?)control.IsAIControlled : null,
                    x = hasPosition ? (int?)position.GridPosition.X : null,
                    y = hasPosition ? (int?)position.GridPosition.Y : null,
                    currentHealth = currentHealth,
                    maxHealth = maxHealth,
                    isDead = currentHealth <= 0,
                    isDespawned = hasCharacterState ? (bool?)characterState.Despawned : null,
                    isActive = hasCharacterState ? (bool?)characterState.Active : null,
                });
            }
            catch
            {
            }
        }
    }
}