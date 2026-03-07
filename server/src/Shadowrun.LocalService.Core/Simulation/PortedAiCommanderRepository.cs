using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Map;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.Simulation
{
    internal sealed class PortedAiCommanderRepository
    {
        private readonly EntitySystem _entitySystem;
        private readonly ILineOfSightEvaluator _lineOfSightEvaluator;
        private readonly Dictionary<SpawnGroupId, PortedAiCommander> _commanders = new Dictionary<SpawnGroupId, PortedAiCommander>();

        public PortedAiCommanderRepository(EntitySystem entitySystem, ILineOfSightEvaluator lineOfSightEvaluator)
        {
            _entitySystem = entitySystem;
            _lineOfSightEvaluator = lineOfSightEvaluator;
        }

        public void RegisterAiControlledEntity(Entity entity)
        {
            if (_entitySystem == null || entity == null || !IsAiControlled(entity))
            {
                return;
            }

            CharacterSpawnInfoComponent spawnInfo;
            TeamComponent team;
            if (!_entitySystem.TryGetComponent<CharacterSpawnInfoComponent>(entity, out spawnInfo) || spawnInfo == null)
            {
                return;
            }

            if (!_entitySystem.TryGetComponent<TeamComponent>(entity, out team) || team == null)
            {
                return;
            }

            PortedAiCommander commander;
            if (!_commanders.TryGetValue(spawnInfo.SpawnGroupId, out commander) || commander == null)
            {
                commander = new PortedAiCommander(team.TeamID, _entitySystem, _lineOfSightEvaluator, spawnInfo.SpawnManagerTag);
                _commanders[spawnInfo.SpawnGroupId] = commander;
            }

            commander.Add(entity);
        }

        public void UnregisterAiControlledEntity(Entity entity)
        {
            if (_entitySystem == null || entity == null)
            {
                return;
            }

            CharacterSpawnInfoComponent spawnInfo;
            if (!_entitySystem.TryGetComponent<CharacterSpawnInfoComponent>(entity, out spawnInfo) || spawnInfo == null)
            {
                return;
            }

            PortedAiCommander commander;
            if (_commanders.TryGetValue(spawnInfo.SpawnGroupId, out commander) && commander != null)
            {
                commander.Remove(entity);
            }
        }

        public void EnsureTrackedAiEntities()
        {
            if (_entitySystem == null)
            {
                return;
            }

            foreach (var entity in _entitySystem.GetAllEntities())
            {
                RegisterAiControlledEntity(entity);
            }
        }

        public bool TryProcessCombatStateForSpawnTag(string spawnManagerTag)
        {
            if (string.IsNullOrEmpty(spawnManagerTag))
            {
                return false;
            }

            EnsureTrackedAiEntities();

            var matched = false;
            foreach (var entry in _commanders)
            {
                var commander = entry.Value;
                if (commander == null || commander.SpawnManagerTag != spawnManagerTag)
                {
                    continue;
                }

                matched = true;
                commander.Act();
                if (commander.InCombat)
                {
                    return true;
                }
            }

            return matched && IsGroupInCombat(spawnManagerTag);
        }

        public bool IsGroupInCombat(string spawnManagerTag)
        {
            if (string.IsNullOrEmpty(spawnManagerTag))
            {
                return true;
            }

            EnsureTrackedAiEntities();

            foreach (var entry in _commanders)
            {
                var commander = entry.Value;
                if (commander != null && commander.SpawnManagerTag == spawnManagerTag && commander.InCombat)
                {
                    return true;
                }
            }

            return false;
        }

        public string[] GetEngagedTagSnapshot()
        {
            EnsureTrackedAiEntities();

            var tags = new HashSet<string>();
            foreach (var entry in _commanders)
            {
                var commander = entry.Value;
                if (commander != null && commander.InCombat && !string.IsNullOrEmpty(commander.SpawnManagerTag))
                {
                    tags.Add(commander.SpawnManagerTag);
                }
            }

            var result = new string[tags.Count];
            tags.CopyTo(result);
            return result;
        }

        public void Move(Entity entity, IntVector2D fromPosition, IntVector2D targetPosition)
        {
            EnsureTrackedAiEntities();

            foreach (var entry in _commanders)
            {
                var commander = entry.Value;
                if (commander != null)
                {
                    commander.MovementEvent(entity, fromPosition, targetPosition);
                }
            }
        }

        public void Skill(Entity entity, Entity[] targets)
        {
            EnsureTrackedAiEntities();

            foreach (var entry in _commanders)
            {
                var commander = entry.Value;
                if (commander != null)
                {
                    commander.SkillEvent(entity, targets);
                }
            }
        }

        public void ChangeTeam(Entity entity, Team targetTeam)
        {
            EnsureTrackedAiEntities();

            foreach (var entry in _commanders)
            {
                var commander = entry.Value;
                if (commander != null)
                {
                    commander.ChangeTeamEvent(entity, targetTeam);
                }
            }
        }

        public void MoveToCombatState(string spawnManagerTag)
        {
            EnsureTrackedAiEntities();

            foreach (var entry in _commanders)
            {
                var commander = entry.Value;
                if (commander != null)
                {
                    commander.SetInCombatEvent(spawnManagerTag);
                }
            }
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
