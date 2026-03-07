using System;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.GameLogic;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Skills;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic
{
    internal sealed class PortedAIAttackPlanner
    {
        private readonly IGameworldInstance _gameworld;
        private readonly Entity _agent;

        public PortedAIAttackPlanner(IGameworldInstance gameworld, Entity agent)
        {
            _gameworld = gameworld;
            _agent = agent;
        }

        public bool TryPlanAttack(ulong skillId, out int weaponIndex, out int skillIndex, out IntVector2D targetPosition, out float score)
        {
            weaponIndex = 0;
            skillIndex = 0;
            targetPosition = IntVector2D.Zero;
            score = 0f;

            if (skillId == 0UL || _gameworld == null || _gameworld.EntitySystem == null || _agent == null)
            {
                return false;
            }

            if (!TryFindSkillIndicesForActivity(skillId, out weaponIndex, out skillIndex))
            {
                return false;
            }

            var dryRunner = _gameworld.ActivitySystem as IActivitySystemDryRunner;
            if (dryRunner == null)
            {
                return false;
            }

            TeamComponent myTeam;
            TeamInfoComponent teamInfo;
            if (!_gameworld.EntitySystem.TryGetComponent<TeamComponent>(_agent, out myTeam) || myTeam == null)
            {
                return false;
            }
            if (!_gameworld.EntitySystem.TryGetComponent<TeamInfoComponent>(EnvironmentEntity.Instance, out teamInfo) || teamInfo == null)
            {
                return false;
            }

            var attackerPosition = AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, _agent);
            var found = false;

            foreach (var entity in _gameworld.EntitySystem.GetAllEntities())
            {
                if (!IsValidTarget(entity, attackerPosition, myTeam.TeamID, teamInfo))
                {
                    continue;
                }

                var candidatePosition = ResolveBestTargetPosition(entity, attackerPosition);
                if (candidatePosition.Equals(IntVector2D.Zero) && AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, entity).Equals(IntVector2D.Zero))
                {
                    continue;
                }

                ActivityEvaluationResult evaluation;
                try
                {
                    evaluation = dryRunner.DryRunActivity(weaponIndex, skillIndex, skillId, _agent, candidatePosition);
                }
                catch
                {
                    continue;
                }

                if (evaluation == null || !evaluation.SkillWasSuccessful || evaluation.TargetWorkspaces == null || !evaluation.TargetWorkspaces.Any())
                {
                    continue;
                }

                var candidateScore = EffectiveChanceToHitTargets(evaluation);
                if (!found || candidateScore > score)
                {
                    found = true;
                    score = candidateScore;
                    targetPosition = candidatePosition;
                }
            }

            return found;
        }

        private bool TryFindSkillIndicesForActivity(ulong activityId, out int weaponIndex, out int skillIndex)
        {
            weaponIndex = 0;
            skillIndex = 0;

            ISkillLoadoutComponent loadout;
            if (!_gameworld.EntitySystem.TryGetComponent<ISkillLoadoutComponent>(_agent, out loadout) || loadout == null || loadout.Weapons == null)
            {
                return false;
            }

            for (var wi = 0; wi < loadout.Weapons.Length; wi++)
            {
                var weapon = loadout.Weapons[wi];
                if (weapon == null || weapon.Skills == null)
                {
                    continue;
                }

                for (var si = 0; si < weapon.Skills.Length; si++)
                {
                    var activity = weapon.Skills[si];
                    if (activity != null && activity.Id == activityId)
                    {
                        weaponIndex = wi;
                        skillIndex = si;
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsValidTarget(Entity entity, IntVector2D attackerPosition, int myTeamId, TeamInfoComponent teamInfo)
        {
            if (entity == null || entity == _agent)
            {
                return false;
            }

            TeamComponent team;
            if (!_gameworld.EntitySystem.TryGetComponent<TeamComponent>(entity, out team) || team == null)
            {
                return false;
            }

            if (!teamInfo.GetRelationship(myTeamId, team.TeamID).Hostile)
            {
                return false;
            }

            GameplayPropertiesComponent gameplayProperties;
            if (_gameworld.EntitySystem.TryGetComponent<GameplayPropertiesComponent>(entity, out gameplayProperties) && gameplayProperties != null && gameplayProperties.InteractiveObject)
            {
                return false;
            }

            if (!_gameworld.EntitySystem.HasComponent<AttributeBackedStatusValueContainer>(entity))
            {
                return false;
            }

            DetectionComponent detection;
            if (_gameworld.EntitySystem.TryGetComponent<DetectionComponent>(entity, out detection) && detection != null && detection.Range > 0)
            {
                var distance = CalculateDistance(attackerPosition, AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, entity));
                if (distance > detection.Range)
                {
                    return false;
                }
            }

            return true;
        }

        private IntVector2D ResolveBestTargetPosition(Entity entity, IntVector2D attackerPosition)
        {
            IPositionComponent position;
            if (_gameworld.EntitySystem.TryGetComponent<IPositionComponent>(entity, out position) && position != null)
            {
                if (position.BlockedGridPositions != null)
                {
                    var bestDistance = int.MaxValue;
                    var bestPosition = IntVector2D.Zero;
                    var found = false;
                    foreach (var blocked in position.BlockedGridPositions)
                    {
                        var distance = CalculateDistance(attackerPosition, blocked);
                        if (!found || distance < bestDistance)
                        {
                            found = true;
                            bestDistance = distance;
                            bestPosition = blocked;
                        }
                    }

                    if (found)
                    {
                        return bestPosition;
                    }
                }

                return position.GridPosition;
            }

            return AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, entity);
        }

        private static float EffectiveChanceToHitTargets(ActivityEvaluationResult evaluation)
        {
            var bestChance = 0f;
            foreach (var workspace in evaluation.TargetWorkspaces)
            {
                if (workspace.Value != null && workspace.Value.ChanceToHitWithoutTargetModifier > bestChance)
                {
                    bestChance = workspace.Value.ChanceToHitWithoutTargetModifier;
                }
            }

            var aoeBonus = Math.Max(0, evaluation.TargetWorkspaces.Count() - 1) * 0.05f;
            return bestChance + aoeBonus;
        }

        private static int CalculateDistance(IntVector2D a, IntVector2D b)
        {
            return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        }
    }
}