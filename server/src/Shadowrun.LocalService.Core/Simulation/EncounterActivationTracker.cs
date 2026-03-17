using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.GameLogic;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Communication;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Map;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.StaticGameData;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.Simulation
{
    internal sealed class EncounterActivationTracker : IInMissionEventReceiver
    {
        private readonly RequestLogger _logger;
        private readonly string _peer;
        private readonly EntitySystem _entitySystem;
        private readonly PortedAiCommanderRepository _portedAiCommanderRepository;
        private readonly RoleFactionLookupTable _roleFactionLookupTable;
        private readonly string[] _missionFactions;
        private readonly string _missionName;

        public EncounterActivationTracker()
            : this(null, null, null, null, null, null, null)
        {
        }

        public EncounterActivationTracker(RequestLogger logger, string peer, EntitySystem entitySystem)
            : this(logger, peer, entitySystem, null, null, null, null)
        {
        }

        public EncounterActivationTracker(RequestLogger logger, string peer, EntitySystem entitySystem, ILineOfSightEvaluator lineOfSightEvaluator)
            : this(logger, peer, entitySystem, lineOfSightEvaluator, null, null, null)
        {
        }

        public EncounterActivationTracker(RequestLogger logger, string peer, EntitySystem entitySystem, ILineOfSightEvaluator lineOfSightEvaluator, IStaticData staticData, PlayingFactions factions, string missionName)
        {
            _logger = logger;
            _peer = peer;
            _entitySystem = entitySystem;
            _portedAiCommanderRepository = new PortedAiCommanderRepository(entitySystem, lineOfSightEvaluator);
            _roleFactionLookupTable = staticData != null && staticData.Globals != null && staticData.Globals.FactionData != null
                ? staticData.Globals.FactionData.RoleFactionLookupTable
                : null;
            _missionFactions = factions != null && factions.Factions != null ? factions.Factions : new string[0];
            _missionName = missionName ?? string.Empty;
        }

        public bool IsGroupEngaged(string spawnManagerTag)
        {
            if (string.IsNullOrEmpty(spawnManagerTag))
            {
                return true;
            }

            return _portedAiCommanderRepository != null && _portedAiCommanderRepository.IsGroupInCombat(spawnManagerTag);
        }

        public string[] GetEngagedTagSnapshot()
        {
            return _portedAiCommanderRepository != null
                ? _portedAiCommanderRepository.GetEngagedTagSnapshot()
                : new string[0];
        }

        public bool TryEngageSpawnTagFromCurrentPlayerVisibility(string spawnManagerTag)
        {
            if (_portedAiCommanderRepository == null || string.IsNullOrEmpty(spawnManagerTag))
            {
                return false;
            }

            return _portedAiCommanderRepository.TryProcessCombatStateForSpawnTag(spawnManagerTag);
        }

        public bool TryEngageEntityFromCurrentPlayerVisibility(Entity entity, out string groupKey)
        {
            groupKey = null;
            if (_portedAiCommanderRepository == null || entity == null)
            {
                return false;
            }

            return _portedAiCommanderRepository.TryProcessCombatStateForEntity(entity, out groupKey);
        }

        public bool IsEntityGroupEngaged(Entity entity, out string groupKey)
        {
            groupKey = null;
            if (_portedAiCommanderRepository == null || entity == null)
            {
                return true;
            }

            return _portedAiCommanderRepository.IsGroupInCombat(entity, out groupKey);
        }

        public void OnAiControlledAgentsTurn(Entity entity)
        {
            if (_portedAiCommanderRepository == null || entity == null)
            {
                return;
            }

            _portedAiCommanderRepository.OnAiControlledAgentsTurn(entity);
        }

        public bool TryGetAiAgent(Entity entity, out PortedAiAgent agent, out string groupKey)
        {
            agent = null;
            groupKey = null;
            if (_portedAiCommanderRepository == null || entity == null)
            {
                return false;
            }

            return _portedAiCommanderRepository.TryGetAiAgent(entity, out agent, out groupKey);
        }

        public void MoveToCombatState(string spawnManagerTag)
        {
            if (string.IsNullOrEmpty(spawnManagerTag) || _portedAiCommanderRepository == null)
            {
                return;
            }

            _portedAiCommanderRepository.MoveToCombatState(spawnManagerTag);
        }

        public void Despawn(Entity entity)
        {
            if (_portedAiCommanderRepository != null)
            {
                _portedAiCommanderRepository.UnregisterAiControlledEntity(entity);
            }

            if (_logger == null || _entitySystem == null)
            {
                return;
            }

            try
            {
                string spawnManagerTag;
                var hasSpawnInfo = TryGetSpawnManagerTag(entity, out spawnManagerTag);

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    action = "encounter-activation",
                    status = "despawn-event",
                    entityId = entity != null ? (int?)entity.Id : null,
                    hasSpawnInfo = hasSpawnInfo,
                    spawnManagerTag = spawnManagerTag,
                    state = BuildEntityStateSnapshot(entity),
                });
            }
            catch
            {
            }
        }

        public void Spawn(Entity entity, Team targetTeam)
        {
            if (_portedAiCommanderRepository != null)
            {
                _portedAiCommanderRepository.RegisterAiControlledEntity(entity);
            }

            if (_logger == null || _entitySystem == null)
            {
                return;
            }

            try
            {
                CharacterSpawnInfoComponent spawnInfo;
                var hasSpawnInfo = _entitySystem.TryGetComponent<CharacterSpawnInfoComponent>(entity, out spawnInfo);
                var spawnManagerTag = hasSpawnInfo && spawnInfo != null ? spawnInfo.SpawnManagerTag : null;

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    action = "encounter-activation",
                    status = "spawn-event",
                    entityId = entity != null ? (int?)entity.Id : null,
                    targetTeamId = targetTeam != null ? (int?)targetTeam.ID : null,
                    hasSpawnInfo = hasSpawnInfo,
                    spawnManagerTag = spawnManagerTag,
                    engagedAtSpawn = !string.IsNullOrEmpty(spawnManagerTag) ? (bool?)IsGroupEngaged(spawnManagerTag) : null,
                    spawnResolution = BuildSpawnResolutionSnapshot(entity, spawnInfo),
                    state = BuildEntityStateSnapshot(entity),
                });
            }
            catch
            {
            }
        }

        public void Move(Entity entity, IntVector2D fromPosition, IntVector2D targetPosition)
        {
            if (_portedAiCommanderRepository != null)
            {
                _portedAiCommanderRepository.Move(entity, fromPosition, targetPosition);
            }

            if (_logger == null || _entitySystem == null)
            {
                return;
            }

            try
            {
                var sourceAiControlled = IsAiControlled(entity);
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    action = "encounter-activation",
                    status = "move-event",
                    sourceEntityId = entity != null ? (int?)entity.Id : null,
                    sourceAiControlled = sourceAiControlled,
                    fromX = fromPosition.X,
                    fromY = fromPosition.Y,
                    toX = targetPosition.X,
                    toY = targetPosition.Y,
                    state = BuildEntityStateSnapshot(entity),
                });
            }
            catch
            {
            }
        }

        public void Action(Entity entity, Entity[] targets, IntVector2D targetPosition, ulong skillId)
        {
            if (_logger == null || _entitySystem == null)
            {
                return;
            }

            try
            {
                string sourceSpawnManagerTag;
                var sourceHasSpawnInfo = TryGetSpawnManagerTag(entity, out sourceSpawnManagerTag);
                var sourceAiControlled = IsAiControlled(entity);

                var targetDetails = new List<object>();
                if (targets != null)
                {
                    for (var i = 0; i < targets.Length; i++)
                    {
                        var target = targets[i];
                        string targetSpawnManagerTag;
                        var targetHasSpawnInfo = TryGetSpawnManagerTag(target, out targetSpawnManagerTag);
                        targetDetails.Add(new
                        {
                            entityId = target != null ? (int?)target.Id : null,
                            hasSpawnInfo = targetHasSpawnInfo,
                            spawnManagerTag = targetSpawnManagerTag,
                            state = BuildEntityStateSnapshot(target),
                        });
                    }
                }

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    action = "encounter-activation",
                    status = "action-event",
                    sourceEntityId = entity != null ? (int?)entity.Id : null,
                    sourceHasSpawnInfo = sourceHasSpawnInfo,
                    sourceSpawnManagerTag = sourceSpawnManagerTag,
                    sourceAiControlled = sourceAiControlled,
                    targetX = targetPosition.X,
                    targetY = targetPosition.Y,
                    skillId = skillId,
                    sourceState = BuildEntityStateSnapshot(entity),
                    targetCount = targets != null ? targets.Length : 0,
                    targets = targetDetails.ToArray(),
                });
            }
            catch
            {
            }
        }

        public void ChangeTeam(Entity entity, Team targetTeam)
        {
            if (_portedAiCommanderRepository != null)
            {
                if (targetTeam != null && targetTeam.AIControlled)
                {
                    _portedAiCommanderRepository.RegisterAiControlledEntity(entity);
                }
                else
                {
                    _portedAiCommanderRepository.UnregisterAiControlledEntity(entity);
                }

                _portedAiCommanderRepository.ChangeTeam(entity, targetTeam);
            }

            if (_logger == null || _entitySystem == null)
            {
                return;
            }

            try
            {
                string spawnManagerTag;
                var hasSpawnInfo = TryGetSpawnManagerTag(entity, out spawnManagerTag);

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    action = "encounter-activation",
                    status = "change-team-event",
                    entityId = entity != null ? (int?)entity.Id : null,
                    hasSpawnInfo = hasSpawnInfo,
                    spawnManagerTag = spawnManagerTag,
                    targetTeamId = targetTeam != null ? (int?)targetTeam.ID : null,
                    targetTeamAi = targetTeam != null ? (bool?)targetTeam.AIControlled : null,
                    state = BuildEntityStateSnapshot(entity),
                });
            }
            catch
            {
            }
        }

        private object BuildEntityStateSnapshot(Entity entity)
        {
            if (_entitySystem == null || entity == null)
            {
                return null;
            }

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

            CharacterStateComponent characterState;
            var hasCharacterState = _entitySystem.TryGetComponent<CharacterStateComponent>(entity, out characterState) && characterState != null;

            TeamComponent team;
            var hasTeam = _entitySystem.TryGetComponent<TeamComponent>(entity, out team) && team != null;

            ControlComponent control;
            var hasControl = _entitySystem.TryGetComponent<ControlComponent>(entity, out control) && control != null;

            IPositionComponent position;
            var hasPosition = _entitySystem.TryGetComponent<IPositionComponent>(entity, out position) && position != null;

            GameplayPropertiesComponent gameplayProperties;
            var hasGameplayProperties = _entitySystem.TryGetComponent<GameplayPropertiesComponent>(entity, out gameplayProperties) && gameplayProperties != null;

            return new
            {
                currentHealth = currentHealth,
                maxHealth = maxHealth,
                isDead = currentHealth <= 0,
                isDespawned = hasCharacterState ? (bool?)characterState.Despawned : null,
                isActive = hasCharacterState ? (bool?)characterState.Active : null,
                teamId = hasTeam ? (int?)team.TeamID : null,
                aiControlled = hasControl ? (bool?)control.IsAIControlled : null,
                characterId = hasGameplayProperties ? gameplayProperties.CharacterId : null,
                characterDifficultyType = hasGameplayProperties ? (int?)gameplayProperties.CharacterDifficultyType : null,
                x = hasPosition ? (int?)position.GridPosition.X : null,
                y = hasPosition ? (int?)position.GridPosition.Y : null,
            };
        }

        private object BuildSpawnResolutionSnapshot(Entity entity, CharacterSpawnInfoComponent spawnInfo)
        {
            if (_entitySystem == null || entity == null || spawnInfo == null)
            {
                return null;
            }

            TeamComponent team;
            var hasTeam = _entitySystem.TryGetComponent<TeamComponent>(entity, out team) && team != null;
            var teamId = hasTeam ? team.TeamID : -1;
            var resolvedFaction = ResolveFactionName(teamId);

            GameplayPropertiesComponent gameplayProperties;
            var hasGameplayProperties = _entitySystem.TryGetComponent<GameplayPropertiesComponent>(entity, out gameplayProperties) && gameplayProperties != null;

            return new
            {
                missionName = _missionName,
                teamId = hasTeam ? (int?)teamId : null,
                spawnGroupId = spawnInfo.SpawnGroupId.ToString(),
                resolvedFaction = resolvedFaction,
                roleTemplateLookupAvailable = _roleFactionLookupTable != null,
                gameplayCharacterId = hasGameplayProperties ? gameplayProperties.CharacterId : null,
                characterDifficultyType = hasGameplayProperties ? (int?)gameplayProperties.CharacterDifficultyType : null,
            };
        }

        private string ResolveFactionName(int teamId)
        {
            if (_missionFactions == null || teamId < 0 || teamId >= _missionFactions.Length)
            {
                return null;
            }

            return _missionFactions[teamId];
        }

        private bool TryGetSpawnManagerTag(Entity entity, out string spawnManagerTag)
        {
            spawnManagerTag = null;
            if (_entitySystem == null || entity == null)
            {
                return false;
            }

            CharacterSpawnInfoComponent spawnInfo;
            if (!_entitySystem.TryGetComponent<CharacterSpawnInfoComponent>(entity, out spawnInfo))
            {
                return false;
            }

            spawnManagerTag = spawnInfo != null ? spawnInfo.SpawnManagerTag : null;
            return spawnInfo != null;
        }

        private bool IsAiControlled(Entity entity)
        {
            if (_entitySystem == null || entity == null)
            {
                return false;
            }

            ControlComponent control;
            if (!_entitySystem.TryGetComponent<ControlComponent>(entity, out control) || control == null)
            {
                return false;
            }

            return control.IsAIControlled;
        }
    }
}
