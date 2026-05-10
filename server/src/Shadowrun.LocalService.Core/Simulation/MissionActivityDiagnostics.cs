using System;
using System.Collections.Generic;
using System.Linq;
using Cliffhanger.GameLogic.Framework;
using Cliffhanger.SRO.ServerClientCommons.GameLogic;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Visualization;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Skills;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Turnbased;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation.Serialization;
using SRO.Core.Compatibility.Gameplay.Trigger;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.Simulation
{
    internal sealed class MissionActivityDiagnostics : IActivitySystem, IActivitySystemDryRunner, IActivitySystemTooltipInformationRunner, IActivityListener, ISpawnGroupDeathListener
    {
        private const ulong ModifyStatusValueActivityId = 393243UL;

        private readonly RequestLogger _logger;
        private readonly string _peer;
        private readonly GameworldInstance _gameworld;
        private readonly IActivitySystem _innerActivitySystem;
        private readonly IActivitySystemDryRunner _innerDryRunner;
        private readonly IActivitySystemTooltipInformationRunner _innerTooltipRunner;
        private Dictionary<ulong, float> _environmentStatusValues;

        private MissionActivityDiagnostics(RequestLogger logger, string peer, GameworldInstance gameworld)
        {
            _logger = logger;
            _peer = peer;
            _gameworld = gameworld;
            _innerActivitySystem = gameworld.ActivitySystem;
            _innerDryRunner = gameworld.ActivityDryRunner;
            _innerTooltipRunner = gameworld.ActivityTooltipInformationRunner;
            _environmentStatusValues = SnapshotEnvironmentStatusValues();
        }

        public static void Install(RequestLogger logger, string peer, GameworldInstance gameworld)
        {
            if (gameworld == null || gameworld.ActivitySystem == null)
            {
                return;
            }

            var diagnostics = new MissionActivityDiagnostics(logger, peer, gameworld);

            var activitySystem = gameworld.ActivitySystem as ActivitySystem;
            if (activitySystem != null)
            {
                activitySystem.AddActivityListener(diagnostics);
            }

            var spawnGroupTracker = gameworld.SpawnGroupTracker as SpawnGroupStateChangeTracker;
            if (spawnGroupTracker != null)
            {
                spawnGroupTracker.AddSpawnGroupDeathListener(diagnostics);
            }

            gameworld.ActivitySystem = diagnostics;
            if (gameworld.ActivityDryRunner != null)
            {
                gameworld.ActivityDryRunner = diagnostics;
            }
            if (gameworld.ActivityTooltipInformationRunner != null)
            {
                gameworld.ActivityTooltipInformationRunner = diagnostics;
            }
        }

        public void RunActivity(ulong activityId, ITriggerActivityContext activityTriggerContext, Entity sourceEntity, IntVector2D targetPosition, int weaponIndex, int skillIndex)
        {
            var isModifyStatusValue = activityId == ModifyStatusValueActivityId;
            if (isModifyStatusValue)
            {
                LogModifyStatusValueActivity("before", activityId, activityTriggerContext, sourceEntity, targetPosition, weaponIndex, skillIndex, null);
            }

            try
            {
                _innerActivitySystem.RunActivity(activityId, activityTriggerContext, sourceEntity, targetPosition, weaponIndex, skillIndex);
            }
            catch (Exception ex)
            {
                if (isModifyStatusValue)
                {
                    LogModifyStatusValueActivity("exception", activityId, activityTriggerContext, sourceEntity, targetPosition, weaponIndex, skillIndex, ex);
                }
                throw;
            }

            if (isModifyStatusValue)
            {
                LogModifyStatusValueActivity("after", activityId, activityTriggerContext, sourceEntity, targetPosition, weaponIndex, skillIndex, null);
            }
        }

        public void RunTriggerActivity(ulong activityId, ITriggerActivityContext activityTriggerContext, Entity source, Entity target)
        {
            _innerActivitySystem.RunTriggerActivity(activityId, activityTriggerContext, source, target);
        }

        public void RunTriggerActivity(ulong activityId, ITriggerActivityContext activityTriggerContext, Entity source, Entity[] targets)
        {
            _innerActivitySystem.RunTriggerActivity(activityId, activityTriggerContext, source, targets);
        }

        public IVisualizationItem RunTriggerActivityGroups(TriggerActivityGroupDefinition[] activityGroupDefinition, Entity source)
        {
            return _innerActivitySystem.RunTriggerActivityGroups(activityGroupDefinition, source);
        }

        public IVisualizationItem RunTriggerActivityClientSide(ulong activityId, ITriggerActivityContext activityTriggerContext, Entity source, Entity[] targets)
        {
            return _innerActivitySystem.RunTriggerActivityClientSide(activityId, activityTriggerContext, source, targets);
        }

        public Activity GetActivityFromId(ulong id)
        {
            return _innerActivitySystem.GetActivityFromId(id);
        }

        public ActivityEvaluationResult DryRunActivity(int weaponIndex, int skillIndex, ulong activityId, Entity sourceEntity, IntVector2D targetPosition)
        {
            return _innerDryRunner.DryRunActivity(weaponIndex, skillIndex, activityId, sourceEntity, targetPosition);
        }

        public BaseCostsAndConditionsEvaluationResult EvaluateBaseCostsAndConditions(IActivityCost<ActivityParameters> costs, ICondition<ActivityParameters> conditions, Entity entity, ulong activityId, int weaponIndex, int skillIndex)
        {
            return _innerDryRunner.EvaluateBaseCostsAndConditions(costs, conditions, entity, activityId, weaponIndex, skillIndex);
        }

        public void DryRunActivityForTooltipInformationCollection(ulong activityId, Entity sourceEntity, int weaponIndex, IDynamicTooltipInformationCollector collector)
        {
            _innerTooltipRunner.DryRunActivityForTooltipInformationCollection(activityId, sourceEntity, weaponIndex, collector);
        }

        public void SkillWasExecuted(Entity source)
        {
            LogEnvironmentStatusValueChanges(source);
        }

        public void OnSpawnGroupDied(SpawnGroup spawnGroup)
        {
            if (_logger == null || spawnGroup == null)
            {
                return;
            }

            try
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-spawn-group-death",
                    peer = _peer,
                    spawnGroupId = spawnGroup.SpawnGroupId.ToString(),
                    deathActivities = DescribeActivityRefs(spawnGroup.DeathActivities),
                    spawnInfos = DescribeSpawnInfos(spawnGroup.SpawnInfos),
                });
            }
            catch
            {
            }
        }

        private void LogModifyStatusValueActivity(string phase, ulong activityId, ITriggerActivityContext activityTriggerContext, Entity sourceEntity, IntVector2D targetPosition, int weaponIndex, int skillIndex, Exception exception)
        {
            if (_logger == null)
            {
                return;
            }

            try
            {
                ulong? statusValueId = TryGetNumberAsUlong(activityTriggerContext, "StatusValueId");
                float? delta = TryGetNumberAsFloat(activityTriggerContext, "Delta");
                float? value = TryGetNumberAsFloat(activityTriggerContext, "Value");
                float? currentValue = statusValueId.HasValue ? TryReadEnvironmentStatusValue(statusValueId.Value) : null;

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-modify-status-value",
                    peer = _peer,
                    phase = phase,
                    activityId = activityId,
                    statusValueId = statusValueId,
                    delta = delta,
                    value = value,
                    currentValue = currentValue,
                    source = DescribeEntity(sourceEntity),
                    targetX = targetPosition.X,
                    targetY = targetPosition.Y,
                    weaponIndex = weaponIndex,
                    skillIndex = skillIndex,
                    context = DescribeContext(activityTriggerContext),
                    exceptionType = exception != null ? exception.GetType().FullName : null,
                    exceptionMessage = exception != null ? exception.Message : null,
                });
            }
            catch
            {
            }
        }

        private void LogEnvironmentStatusValueChanges(Entity source)
        {
            if (_logger == null)
            {
                return;
            }

            Dictionary<ulong, float> current;
            try
            {
                current = SnapshotEnvironmentStatusValues();
            }
            catch
            {
                return;
            }

            if (_environmentStatusValues == null)
            {
                _environmentStatusValues = current;
                return;
            }

            foreach (var entry in current)
            {
                float previous;
                if (!_environmentStatusValues.TryGetValue(entry.Key, out previous) || previous != entry.Value)
                {
                    try
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "mission-status-value-change",
                            peer = _peer,
                            statusValueId = entry.Key,
                            previousValue = _environmentStatusValues.ContainsKey(entry.Key) ? (float?)previous : null,
                            currentValue = entry.Value,
                            source = DescribeEntity(source),
                        });
                    }
                    catch
                    {
                    }
                }
            }

            foreach (var entry in _environmentStatusValues)
            {
                if (!current.ContainsKey(entry.Key))
                {
                    try
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "mission-status-value-change",
                            peer = _peer,
                            statusValueId = entry.Key,
                            previousValue = (float?)entry.Value,
                            currentValue = (float?)null,
                            source = DescribeEntity(source),
                        });
                    }
                    catch
                    {
                    }
                }
            }

            _environmentStatusValues = current;
        }

        private Dictionary<ulong, float> SnapshotEnvironmentStatusValues()
        {
            var values = new Dictionary<ulong, float>();
            if (_gameworld == null || _gameworld.EntitySystem == null)
            {
                return values;
            }

            AttributeBackedStatusValueContainer container;
            if (!_gameworld.EntitySystem.TryGetComponent<AttributeBackedStatusValueContainer>(EnvironmentEntity.Instance, out container) || container == null)
            {
                return values;
            }

            foreach (var statusValue in container)
            {
                if (statusValue != null)
                {
                    values[statusValue.Id] = statusValue.Value;
                }
            }

            return values;
        }

        private float? TryReadEnvironmentStatusValue(ulong statusValueId)
        {
            if (_gameworld == null || _gameworld.EntitySystem == null)
            {
                return null;
            }

            try
            {
                AttributeBackedStatusValueContainer container;
                if (_gameworld.EntitySystem.TryGetComponent<AttributeBackedStatusValueContainer>(EnvironmentEntity.Instance, out container) && container != null && container.DoesContain(statusValueId))
                {
                    return container.GetByIdOrReturnDefault(statusValueId);
                }
            }
            catch
            {
            }

            return null;
        }

        private object DescribeEntity(Entity entity)
        {
            if (entity == null)
            {
                return null;
            }

            try
            {
                CharacterSpawnInfoComponent spawnInfo;
                var hasSpawnInfo = _gameworld.EntitySystem.TryGetComponent<CharacterSpawnInfoComponent>(entity, out spawnInfo) && spawnInfo != null;

                SpawnGroupComponent spawnGroup;
                var hasSpawnGroup = _gameworld.EntitySystem.TryGetComponent<SpawnGroupComponent>(entity, out spawnGroup) && spawnGroup != null;

                TeamComponent team;
                var hasTeam = _gameworld.EntitySystem.TryGetComponent<TeamComponent>(entity, out team) && team != null;

                IPositionComponent position;
                var hasPosition = _gameworld.EntitySystem.TryGetComponent<IPositionComponent>(entity, out position) && position != null;

                return new
                {
                    entityId = entity.Id,
                    spawnManagerTag = hasSpawnInfo ? spawnInfo.SpawnManagerTag : null,
                    spawnGroupId = hasSpawnGroup ? spawnGroup.Group.SpawnGroupId.ToString() : null,
                    spawnGroupRuntimeId = hasSpawnGroup ? (Guid?)spawnGroup.Id : null,
                    teamId = hasTeam ? (int?)team.TeamID : null,
                    x = hasPosition ? (int?)position.GridPosition.X : null,
                    y = hasPosition ? (int?)position.GridPosition.Y : null,
                };
            }
            catch
            {
                return new { entityId = entity.Id };
            }
        }

        private static ulong? TryGetNumberAsUlong(ITriggerActivityContext context, string key)
        {
            if (context == null || !context.HasNumber(key))
            {
                return null;
            }

            return (ulong)Math.Max(0, context.GetNumberAsInt(key));
        }

        private static float? TryGetNumberAsFloat(ITriggerActivityContext context, string key)
        {
            if (context == null || !context.HasNumber(key))
            {
                return null;
            }

            return context.GetNumberAsFloat(key);
        }

        private static object DescribeContext(ITriggerActivityContext context)
        {
            if (context == null)
            {
                return null;
            }

            return new
            {
                statusValueId = TryGetNumberAsUlong(context, "StatusValueId"),
                agentStatusValueId = TryGetNumberAsUlong(context, "AgentStatusValueId"),
                delta = TryGetNumberAsFloat(context, "Delta"),
                value = TryGetNumberAsFloat(context, "Value"),
            };
        }

        private static object[] DescribeActivityRefs(IEnumerable<ActivityRef> activities)
        {
            if (activities == null)
            {
                return new object[0];
            }

            return activities.Select(activity => new
            {
                activityId = activity.ActivityId,
                context = activity.Context == null ? new object[0] : activity.Context.Select(entry => new { id = entry.Id, value = entry.Value }).Cast<object>().ToArray(),
                stringContext = activity.StringContext == null ? new object[0] : activity.StringContext.Select(entry => new { id = entry.Id, value = entry.Value }).Cast<object>().ToArray(),
            }).Cast<object>().ToArray();
        }

        private static object[] DescribeSpawnInfos(IEnumerable<CharacterSpawnInfo> spawnInfos)
        {
            if (spawnInfos == null)
            {
                return new object[0];
            }

            return spawnInfos.Select(spawnInfo => new
            {
                spawnPointId = spawnInfo.SpawnPointId.ToString(),
                characterId = spawnInfo.CharacterId,
                teamId = spawnInfo.TeamId,
                templateId = spawnInfo.TemplateId,
                roleName = spawnInfo.RoleName,
                isPlayerSpawn = spawnInfo.IsPlayerSpawn,
                isHenchman = spawnInfo.IsHenchman,
                isRoleSpawn = spawnInfo.IsRoleSpawn,
                isLieutenant = spawnInfo.IsLieutenant,
                x = spawnInfo.SpawnPosition.SpawnPoint.X,
                y = spawnInfo.SpawnPosition.SpawnPoint.Y,
                deathActivities = DescribeActivityRefs(spawnInfo.DeathActivities),
            }).Cast<object>().ToArray();
        }
    }
}