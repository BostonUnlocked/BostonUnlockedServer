using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Communication;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.Simulation
{
    internal sealed class EncounterActivationTracker : IInMissionEventReceiver
    {
        private readonly HashSet<string> _engagedSpawnManagerTags = new HashSet<string>();
        private readonly RequestLogger _logger;
        private readonly string _peer;
        private readonly EntitySystem _entitySystem;
        private readonly object _sync = new object();

        public EncounterActivationTracker()
            : this(null, null, null)
        {
        }

        public EncounterActivationTracker(RequestLogger logger, string peer, EntitySystem entitySystem)
        {
            _logger = logger;
            _peer = peer;
            _entitySystem = entitySystem;
        }

        public bool IsGroupEngaged(string spawnManagerTag)
        {
            if (string.IsNullOrEmpty(spawnManagerTag))
            {
                return true;
            }

            lock (_sync)
            {
                return _engagedSpawnManagerTags.Contains(spawnManagerTag);
            }
        }

        public string[] GetEngagedTagSnapshot()
        {
            lock (_sync)
            {
                if (_engagedSpawnManagerTags.Count == 0)
                {
                    return new string[0];
                }

                var result = new string[_engagedSpawnManagerTags.Count];
                _engagedSpawnManagerTags.CopyTo(result);
                return result;
            }
        }

        public bool TryEngageSpawnTagFromCurrentPlayerVisibility(string spawnManagerTag)
        {
            if (_entitySystem == null || string.IsNullOrEmpty(spawnManagerTag))
            {
                return false;
            }

            if (IsGroupEngaged(spawnManagerTag))
            {
                return true;
            }

            foreach (var candidate in _entitySystem.GetAllEntities())
            {
                if (candidate == null || IsAiControlled(candidate))
                {
                    continue;
                }

                DetectionComponent detection;
                if (!_entitySystem.TryGetComponent<DetectionComponent>(candidate, out detection) || detection == null)
                {
                    continue;
                }

                foreach (var visibleEntity in detection.VisibleAgents)
                {
                    string visibleSpawnManagerTag;
                    var visibleHasSpawnInfo = TryGetSpawnManagerTag(visibleEntity, out visibleSpawnManagerTag);
                    if (!visibleHasSpawnInfo || !string.Equals(visibleSpawnManagerTag, spawnManagerTag))
                    {
                        continue;
                    }

                    AddEngagedTag(spawnManagerTag, "player-current-visibility", candidate, visibleEntity);
                    return true;
                }
            }

            return false;
        }

        public void MoveToCombatState(string spawnManagerTag)
        {
            if (string.IsNullOrEmpty(spawnManagerTag))
            {
                return;
            }

            AddEngagedTag(spawnManagerTag, "move-to-combat-state", null, null);
        }

        public void Despawn(Entity entity)
        {
        }

        public void Spawn(Entity entity, Team targetTeam)
        {
            if (_logger == null || _entitySystem == null)
            {
                return;
            }

            try
            {
                CharacterSpawnInfoComponent spawnInfo;
                var hasSpawnInfo = _entitySystem.TryGetComponent<CharacterSpawnInfoComponent>(entity, out spawnInfo);
                var spawnManagerTag = hasSpawnInfo && spawnInfo != null ? spawnInfo.SpawnManagerTag : null;
                var engagedBySpawnVisibility = false;
                var spawnVisibleToPlayerIds = new List<int>();

                if (!string.IsNullOrEmpty(spawnManagerTag) && !IsGroupEngaged(spawnManagerTag))
                {
                    engagedBySpawnVisibility = TryEngageOnSpawnVisibility(entity, spawnManagerTag, spawnVisibleToPlayerIds);
                }

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
                    engagedBySpawnVisibility = engagedBySpawnVisibility,
                    spawnVisibleToPlayerIds = spawnVisibleToPlayerIds.ToArray(),
                });
            }
            catch
            {
            }
        }

        public void Move(Entity entity, IntVector2D fromPosition, IntVector2D targetPosition)
        {
            if (_logger == null || _entitySystem == null)
            {
                return;
            }

            try
            {
                var sourceAiControlled = IsAiControlled(entity);
                var newlyEngaged = new List<string>();
                var visibleTargets = new List<object>();

                DetectionComponent detection = null;
                var hasDetection = entity != null && _entitySystem.TryGetComponent<DetectionComponent>(entity, out detection) && detection != null;
                if (!sourceAiControlled && hasDetection)
                {
                    foreach (var visibleEntity in detection.VisibleAgents)
                    {
                        string visibleSpawnManagerTag;
                        var visibleHasSpawnInfo = TryGetSpawnManagerTag(visibleEntity, out visibleSpawnManagerTag);
                        visibleTargets.Add(new
                        {
                            entityId = visibleEntity != null ? (int?)visibleEntity.Id : null,
                            hasSpawnInfo = visibleHasSpawnInfo,
                            spawnManagerTag = visibleSpawnManagerTag,
                        });

                        if (!string.IsNullOrEmpty(visibleSpawnManagerTag))
                        {
                            if (AddEngagedTag(visibleSpawnManagerTag, "player-move-visibility", entity, visibleEntity))
                            {
                                newlyEngaged.Add(visibleSpawnManagerTag);
                            }
                        }
                    }
                }

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
                    hasDetection = hasDetection,
                    visibleCount = visibleTargets.Count,
                    visibleTargets = visibleTargets.ToArray(),
                    engagedByMoveCount = newlyEngaged.Count,
                    engagedByMoveTags = newlyEngaged.ToArray(),
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
                        });

                        if (!sourceAiControlled && !string.IsNullOrEmpty(targetSpawnManagerTag))
                        {
                            AddEngagedTag(targetSpawnManagerTag, "player-action-target", entity, target);
                        }
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
                });
            }
            catch
            {
            }
        }

        private bool AddEngagedTag(string spawnManagerTag, string reason, Entity sourceEntity, Entity targetEntity)
        {
            if (string.IsNullOrEmpty(spawnManagerTag))
            {
                return false;
            }

            var isNew = false;
            lock (_sync)
            {
                if (_engagedSpawnManagerTags.Add(spawnManagerTag))
                {
                    isNew = true;
                }
            }

            try
            {
                var engagedTags = GetEngagedTagSnapshot();
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    action = "encounter-activation",
                    status = isNew ? "engaged" : "already-engaged",
                    reason = reason,
                    spawnManagerTag = spawnManagerTag,
                    sourceEntityId = sourceEntity != null ? (int?)sourceEntity.Id : null,
                    targetEntityId = targetEntity != null ? (int?)targetEntity.Id : null,
                    engagedCount = engagedTags.Length,
                    engagedTags = engagedTags,
                });
            }
            catch
            {
            }

            return isNew;
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

        private bool TryEngageOnSpawnVisibility(Entity spawnedEntity, string spawnManagerTag, List<int> visibleToPlayerIds)
        {
            if (_entitySystem == null || spawnedEntity == null || string.IsNullOrEmpty(spawnManagerTag))
            {
                return false;
            }

            var spawnedEntityId = spawnedEntity.Id;
            foreach (var candidate in _entitySystem.GetAllEntities())
            {
                if (candidate == null || IsAiControlled(candidate))
                {
                    continue;
                }

                DetectionComponent detection;
                if (!_entitySystem.TryGetComponent<DetectionComponent>(candidate, out detection) || detection == null)
                {
                    continue;
                }

                foreach (var visibleEntity in detection.VisibleAgents)
                {
                    if (visibleEntity == null || visibleEntity.Id != spawnedEntityId)
                    {
                        continue;
                    }

                    visibleToPlayerIds.Add((int)candidate.Id);
                    if (AddEngagedTag(spawnManagerTag, "player-visibility-at-spawn", candidate, spawnedEntity))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
