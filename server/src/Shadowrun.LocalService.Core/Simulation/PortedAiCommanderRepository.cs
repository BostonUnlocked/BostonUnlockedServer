using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
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
        private readonly Dictionary<int, PortedAiAgent> _agents = new Dictionary<int, PortedAiAgent>();

        public PortedAiCommanderRepository(EntitySystem entitySystem, ILineOfSightEvaluator lineOfSightEvaluator)
        {
            _entitySystem = entitySystem;
            _lineOfSightEvaluator = lineOfSightEvaluator;
        }

        public void RegisterAiControlledEntity(Entity entity)
        {
            if (_entitySystem == null || entity == null || !IsAiTrackable(entity))
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

            var agent = GetOrCreateAgent(entity);
            if (agent == null)
            {
                return;
            }

            PortedAiCommander commander;
            if (!_commanders.TryGetValue(spawnInfo.SpawnGroupId, out commander) || commander == null)
            {
                commander = new PortedAiCommander(team.TeamID, _entitySystem, _lineOfSightEvaluator, spawnInfo.SpawnManagerTag);
                _commanders[spawnInfo.SpawnGroupId] = commander;
            }

            commander.Add(agent);
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
                PortedAiAgent agent;
                if (_agents.TryGetValue(entity.Id, out agent) && agent != null)
                {
                    commander.Remove(agent);
                }
            }

            _agents.Remove(entity.Id);
        }

        public void OnAiControlledAgentsTurn(Entity entity)
        {
            PortedAiCommander commander;
            CharacterSpawnInfoComponent spawnInfo;
            if (!TryGetCommander(entity, out commander, out spawnInfo) || commander == null)
            {
                return;
            }

            commander.Act();
        }

        public bool TryGetAiAgent(Entity entity, out PortedAiAgent agent, out string groupKey)
        {
            agent = null;
            groupKey = null;

            PortedAiCommander commander;
            CharacterSpawnInfoComponent spawnInfo;
            if (!TryGetCommander(entity, out commander, out spawnInfo) || spawnInfo == null)
            {
                return false;
            }

            groupKey = BuildGroupKey(spawnInfo);
            if (!_agents.TryGetValue(entity.Id, out agent) || agent == null)
            {
                RegisterAiControlledEntity(entity);
                _agents.TryGetValue(entity.Id, out agent);
            }

            return agent != null;
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

        public bool TryProcessCombatStateForEntity(Entity entity, out string groupKey)
        {
            groupKey = null;

            PortedAiCommander commander;
            CharacterSpawnInfoComponent spawnInfo;
            if (!TryGetCommander(entity, out commander, out spawnInfo) || commander == null || spawnInfo == null)
            {
                return false;
            }

            groupKey = BuildGroupKey(spawnInfo);
            commander.Act();
            return commander.InCombat;
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

        public bool IsGroupInCombat(Entity entity, out string groupKey)
        {
            groupKey = null;

            PortedAiCommander commander;
            CharacterSpawnInfoComponent spawnInfo;
            if (!TryGetCommander(entity, out commander, out spawnInfo) || commander == null || spawnInfo == null)
            {
                return true;
            }

            groupKey = BuildGroupKey(spawnInfo);
            return commander.InCombat;
        }

        public string[] GetEngagedTagSnapshot()
        {
            EnsureTrackedAiEntities();

            var tags = new HashSet<string>();
            foreach (var entry in _commanders)
            {
                var commander = entry.Value;
                if (commander == null || !commander.InCombat)
                {
                    continue;
                }

                var groupKey = !string.IsNullOrEmpty(commander.SpawnManagerTag)
                    ? commander.SpawnManagerTag
                    : entry.Key.ToString();

                if (!string.IsNullOrEmpty(groupKey))
                {
                    tags.Add(groupKey);
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

        private bool IsAiTrackable(Entity entity)
        {
            if (_entitySystem == null || entity == null)
            {
                return false;
            }

            if (_entitySystem.HasComponent<AIBehaviourConfigurationComponent>(entity))
            {
                return true;
            }

            ControlComponent control;
            if (!_entitySystem.TryGetComponent<ControlComponent>(entity, out control) || control == null)
            {
                return false;
            }

            return control.IsAIControlled;
        }

        private PortedAiAgent GetOrCreateAgent(Entity entity)
        {
            if (entity == null)
            {
                return null;
            }

            PortedAiAgent agent;
            if (_agents.TryGetValue(entity.Id, out agent) && agent != null)
            {
                return agent;
            }

            agent = new PortedAiAgent(entity);
            _agents[entity.Id] = agent;
            return agent;
        }

        private bool TryGetCommander(Entity entity, out PortedAiCommander commander, out CharacterSpawnInfoComponent spawnInfo)
        {
            commander = null;
            spawnInfo = null;

            if (_entitySystem == null || entity == null)
            {
                return false;
            }

            EnsureTrackedAiEntities();

            if (!_entitySystem.TryGetComponent<CharacterSpawnInfoComponent>(entity, out spawnInfo) || spawnInfo == null)
            {
                return false;
            }

            if (!_commanders.TryGetValue(spawnInfo.SpawnGroupId, out commander) || commander == null)
            {
                RegisterAiControlledEntity(entity);
                _commanders.TryGetValue(spawnInfo.SpawnGroupId, out commander);
            }

            return commander != null;
        }

        private static string BuildGroupKey(CharacterSpawnInfoComponent spawnInfo)
        {
            if (spawnInfo == null)
            {
                return null;
            }

            return !string.IsNullOrEmpty(spawnInfo.SpawnManagerTag)
                ? spawnInfo.SpawnManagerTag
                : spawnInfo.SpawnGroupId.ToString();
        }
    }
}
