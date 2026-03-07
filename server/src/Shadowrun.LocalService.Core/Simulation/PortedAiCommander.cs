using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Map;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.Simulation
{
    internal sealed class PortedAiCommander
    {
        private readonly int _teamId;
        private readonly EntitySystem _entitySystem;
        private readonly ILineOfSightEvaluator _lineOfSightEvaluator;
        private readonly string _spawnManagerTag;
        private readonly HashSet<Entity> _controlledAgents = new HashSet<Entity>();
        private readonly List<IntVector2D> _suspiciousPositions = new List<IntVector2D>();
        private readonly TeamInfoComponent _teamInfoComponent;

        public PortedAiCommander(int teamId, EntitySystem entitySystem, ILineOfSightEvaluator lineOfSightEvaluator, string spawnManagerTag)
        {
            _teamId = teamId;
            _entitySystem = entitySystem;
            _lineOfSightEvaluator = lineOfSightEvaluator;
            _spawnManagerTag = spawnManagerTag;
            _teamInfoComponent = _entitySystem != null
                ? _entitySystem.GetComponent<TeamInfoComponent>(EnvironmentEntity.Instance)
                : null;
        }

        public string SpawnManagerTag
        {
            get { return _spawnManagerTag; }
        }

        public bool InCombat { get; private set; }

        public void Add(Entity newAgent)
        {
            if (newAgent == null)
            {
                return;
            }

            _controlledAgents.Add(newAgent);
        }

        public void Remove(Entity agent)
        {
            if (agent == null)
            {
                return;
            }

            _controlledAgents.Remove(agent);
        }

        public void Act()
        {
            if (!InCombat)
            {
                CheckDetection();
                ProcessEvents();
            }
        }

        private void CheckDetection()
        {
            if (_entitySystem == null || _teamInfoComponent == null)
            {
                return;
            }

            foreach (var controlledAgent in _controlledAgents)
            {
                if (controlledAgent == null)
                {
                    continue;
                }

                DetectionComponent detection;
                if (!_entitySystem.TryGetComponent<DetectionComponent>(controlledAgent, out detection) || detection == null || detection.VisibleAgents == null)
                {
                    continue;
                }

                foreach (var entity in detection.VisibleAgents)
                {
                    if (entity == null)
                    {
                        continue;
                    }

                    TeamComponent teamComponent;
                    if (!_entitySystem.TryGetComponent<TeamComponent>(entity, out teamComponent) || teamComponent == null)
                    {
                        continue;
                    }

                    if (_teamInfoComponent.GetRelationship(teamComponent.TeamID, _teamId).Hostile)
                    {
                        SetAllInCombat();
                        return;
                    }
                }
            }
        }

        private void ProcessEvents()
        {
            if (_suspiciousPositions.Count == 0 || _entitySystem == null)
            {
                return;
            }

            foreach (var position in _suspiciousPositions)
            {
                if (InCombat)
                {
                    break;
                }

                foreach (var agent in _controlledAgents)
                {
                    if (agent == null)
                    {
                        continue;
                    }

                    IPositionComponent positionComponent;
                    if (!_entitySystem.TryGetComponent<IPositionComponent>(agent, out positionComponent) || positionComponent == null)
                    {
                        continue;
                    }

                    var aggroDistance = 0;
                    DetectionComponent detectionComponent;
                    if (_entitySystem.TryGetComponent<DetectionComponent>(agent, out detectionComponent) && detectionComponent != null)
                    {
                        aggroDistance = detectionComponent.Range;
                    }

                    if (aggroDistance > 0
                        && _lineOfSightEvaluator != null
                        && _lineOfSightEvaluator.Evaluate(positionComponent.GridPosition, position, aggroDistance))
                    {
                        SetAllInCombat();
                        break;
                    }
                }
            }

            _suspiciousPositions.Clear();
        }

        private void SetAllInCombat()
        {
            InCombat = true;
        }

        public void MovementEvent(Entity agent, IntVector2D startPosition, IntVector2D targetPosition)
        {
            if (!InCombat && IsHostile(agent))
            {
                _suspiciousPositions.Add(startPosition);
                _suspiciousPositions.Add(targetPosition);
            }
        }

        public void SkillEvent(Entity agent, Entity[] targetedEntities)
        {
            if (InCombat || !IsHostile(agent) || targetedEntities == null)
            {
                return;
            }

            foreach (var entity in targetedEntities)
            {
                if (entity == null)
                {
                    continue;
                }

                foreach (var controlledAgent in _controlledAgents)
                {
                    if (controlledAgent != null && controlledAgent.Id == entity.Id)
                    {
                        SetAllInCombat();
                        return;
                    }
                }
            }
        }

        public void SetInCombatEvent(string spawnManagerTag)
        {
            if (!InCombat && _spawnManagerTag == spawnManagerTag)
            {
                SetAllInCombat();
            }
        }

        public void ChangeTeamEvent(Entity entity, Team targetTeam)
        {
            if (InCombat || !IsHostile(entity) || _entitySystem == null)
            {
                return;
            }

            PositionComponent positionComponent;
            if (_entitySystem.TryGetComponent<PositionComponent>(entity, out positionComponent) && positionComponent != null)
            {
                _suspiciousPositions.Add(positionComponent.GridPosition);
            }
        }

        private bool IsHostile(Entity agent)
        {
            if (_entitySystem == null || _teamInfoComponent == null || agent == null)
            {
                return false;
            }

            TeamComponent team;
            if (!_entitySystem.TryGetComponent<TeamComponent>(agent, out team) || team == null)
            {
                return false;
            }

            return _teamInfoComponent.GetRelationship(team.TeamID, _teamId) == Relationship.Enemy;
        }
    }
}
