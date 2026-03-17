using System.Collections.Generic;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.GameLogic;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Locomotion;

namespace Shadowrun.LocalService.Core.Simulation
{
    public sealed partial class ServerSimulationSession
    {
        internal sealed class MissionEntityReplicationSnapshot
        {
            public int EntityId { get; set; }
            public bool Exists { get; set; }
            public bool HasSpawnInfo { get; set; }
            public string SpawnManagerTag { get; set; }
            public int? TeamId { get; set; }
            public bool? AiControlled { get; set; }
            public int? X { get; set; }
            public int? Y { get; set; }
            public int CurrentHealth { get; set; }
            public int? MaxHealth { get; set; }
            public bool IsDead { get; set; }
            public bool? IsDespawned { get; set; }
            public bool? IsActive { get; set; }
        }

        internal MissionEntityReplicationSnapshot[] DescribeEntitiesForReplication(IEnumerable<int> entityIds)
        {
            if (_gameworld == null || _gameworld.EntitySystem == null || entityIds == null)
            {
                return new MissionEntityReplicationSnapshot[0];
            }

            var distinctIds = entityIds
                .Where(id => id > 0)
                .Distinct()
                .ToArray();

            if (distinctIds.Length == 0)
            {
                return new MissionEntityReplicationSnapshot[0];
            }

            var entitiesById = _gameworld.EntitySystem
                .GetAllEntities()
                .Where(entity => entity != null)
                .GroupBy(entity => entity.Id)
                .ToDictionary(group => group.Key, group => group.First());

            var snapshots = new List<MissionEntityReplicationSnapshot>(distinctIds.Length);
            for (var i = 0; i < distinctIds.Length; i++)
            {
                var entityId = distinctIds[i];
                Entity entity;
                if (!entitiesById.TryGetValue(entityId, out entity) || entity == null)
                {
                    snapshots.Add(new MissionEntityReplicationSnapshot
                    {
                        EntityId = entityId,
                        Exists = false,
                    });
                    continue;
                }

                CharacterSpawnInfoComponent spawnInfo;
                var hasSpawnInfo = _gameworld.EntitySystem.TryGetComponent<CharacterSpawnInfoComponent>(entity, out spawnInfo) && spawnInfo != null;

                TeamComponent team;
                var hasTeam = _gameworld.EntitySystem.TryGetComponent<TeamComponent>(entity, out team) && team != null;

                ControlComponent control;
                var hasControl = _gameworld.EntitySystem.TryGetComponent<ControlComponent>(entity, out control) && control != null;

                IPositionComponent position;
                var hasPosition = _gameworld.EntitySystem.TryGetComponent<IPositionComponent>(entity, out position) && position != null;

                CharacterStateComponent characterState;
                var hasCharacterState = _gameworld.EntitySystem.TryGetComponent<CharacterStateComponent>(entity, out characterState) && characterState != null;

                var currentHealth = (int)_gameworld.EntitySystem.GetStatusValueOrDefault(entity, 458753UL);
                int? maxHealth = null;
                AttributeBackedStatusValueContainer statusValues;
                if (_gameworld.EntitySystem.TryGetComponent<AttributeBackedStatusValueContainer>(entity, out statusValues)
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

                snapshots.Add(new MissionEntityReplicationSnapshot
                {
                    EntityId = entity.Id,
                    Exists = true,
                    HasSpawnInfo = hasSpawnInfo,
                    SpawnManagerTag = hasSpawnInfo ? spawnInfo.SpawnManagerTag : null,
                    TeamId = hasTeam ? (int?)team.TeamID : null,
                    AiControlled = hasControl ? (bool?)control.IsAIControlled : null,
                    X = hasPosition ? (int?)position.GridPosition.X : null,
                    Y = hasPosition ? (int?)position.GridPosition.Y : null,
                    CurrentHealth = currentHealth,
                    MaxHealth = maxHealth,
                    IsDead = currentHealth <= 0,
                    IsDespawned = hasCharacterState ? (bool?)characterState.Despawned : null,
                    IsActive = hasCharacterState ? (bool?)characterState.Active : null,
                });
            }

            return snapshots.ToArray();
        }
    }
}