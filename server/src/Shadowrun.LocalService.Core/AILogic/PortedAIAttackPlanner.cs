using System;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons;
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

        public bool TryPlanAttack(ulong skillId, AiPlanningDiagnostics diagnostics, out int weaponIndex, out int skillIndex, out IntVector2D targetPosition, out float score)
        {
            weaponIndex = 0;
            skillIndex = 0;
            targetPosition = IntVector2D.Zero;
            score = 0f;

            ResetAttackDiagnostics(diagnostics);

            if (skillId == 0UL || _gameworld == null || _gameworld.EntitySystem == null || _agent == null)
            {
                SetAttackFailure(diagnostics, "invalid-attack-context");
                return false;
            }

            if (!TryFindSkillIndicesForActivity(skillId, out weaponIndex, out skillIndex))
            {
                SetAttackFailure(diagnostics, "skill-not-in-loadout");
                return false;
            }

            var dryRunner = _gameworld.ActivitySystem as IActivitySystemDryRunner;
            if (dryRunner == null)
            {
                SetAttackFailure(diagnostics, "no-dry-runner");
                return false;
            }

            TeamComponent myTeam;
            TeamInfoComponent teamInfo;
            if (!_gameworld.EntitySystem.TryGetComponent<TeamComponent>(_agent, out myTeam) || myTeam == null)
            {
                SetAttackFailure(diagnostics, "no-source-team");
                return false;
            }
            if (!_gameworld.EntitySystem.TryGetComponent<TeamInfoComponent>(EnvironmentEntity.Instance, out teamInfo) || teamInfo == null)
            {
                SetAttackFailure(diagnostics, "no-team-info");
                return false;
            }

            var attackerPosition = AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, _agent);
            var found = false;
            var candidateCount = 0;
            var evaluatedCount = 0;
            string firstFailure = null;

            foreach (var entity in _gameworld.EntitySystem.GetAllEntities())
            {
                IRelationship relationship;
                string rejectionReason;
                if (!IsValidTarget(entity, attackerPosition, myTeam.TeamID, teamInfo, out relationship, out rejectionReason))
                {
                    if (firstFailure == null && !string.IsNullOrEmpty(rejectionReason))
                    {
                        firstFailure = rejectionReason;
                    }
                    continue;
                }

                candidateCount++;

                var candidatePosition = ResolveBestTargetPosition(entity, attackerPosition);
                if (candidatePosition.Equals(IntVector2D.Zero) && AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, entity).Equals(IntVector2D.Zero))
                {
                    if (firstFailure == null)
                    {
                        firstFailure = "target-position-unresolved";
                    }
                    continue;
                }

                evaluatedCount++;

                ActivityEvaluationResult evaluation;
                try
                {
                    evaluation = dryRunner.DryRunActivity(weaponIndex, skillIndex, skillId, _agent, candidatePosition);
                }
                catch
                {
                    if (firstFailure == null)
                    {
                        firstFailure = "dry-run-exception";
                    }
                    continue;
                }

                if (evaluation == null || !evaluation.SkillWasSuccessful || evaluation.TargetWorkspaces == null || !evaluation.TargetWorkspaces.Any())
                {
                    if (firstFailure == null)
                    {
                        firstFailure = DescribeDryRunFailure(evaluation);
                    }
                    continue;
                }

                var candidateScore = EffectiveChanceToHitTargets(evaluation);
                if (!found || candidateScore > score)
                {
                    found = true;
                    score = candidateScore;
                    targetPosition = candidatePosition;
                    PopulateChosenTargetDiagnostics(diagnostics, entity, relationship, candidatePosition);
                }
            }

            if (diagnostics != null)
            {
                diagnostics.DebugAttackCandidateCount = candidateCount;
                diagnostics.DebugAttackEvaluatedTargetCount = evaluatedCount;
            }

            if (!found)
            {
                SetAttackFailure(diagnostics, firstFailure ?? (candidateCount == 0 ? "no-valid-targets" : "no-viable-dry-run-target"));
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

        private bool IsValidTarget(Entity entity, IntVector2D attackerPosition, int myTeamId, TeamInfoComponent teamInfo, out IRelationship relationship, out string rejectionReason)
        {
            relationship = null;
            rejectionReason = null;

            if (entity == null)
            {
                rejectionReason = "null-target";
                return false;
            }

            DetectionComponent detection;
            if (!_gameworld.EntitySystem.TryGetComponent<DetectionComponent>(entity, out detection) || detection == null)
            {
                rejectionReason = "no-detection";
                return false;
            }

            var targetPosition = AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, entity);
            if (CalculateDistance(attackerPosition, targetPosition) > detection.Range)
            {
                rejectionReason = "out-of-detection-range";
                return false;
            }

            if (!_gameworld.EntitySystem.HasComponent<AttributeBackedStatusValueContainer>(entity) || _gameworld.EntitySystem.IsAgentDeadOrDespawned(entity))
            {
                rejectionReason = "dead-or-no-status";
                return false;
            }

            TeamComponent team;
            if (!_gameworld.EntitySystem.TryGetComponent<TeamComponent>(entity, out team) || team == null)
            {
                rejectionReason = "no-team";
                return false;
            }

            relationship = teamInfo.GetRelationship(myTeamId, team.TeamID);
            if (relationship == null || relationship.Id == Relationship.Ignored.Id)
            {
                rejectionReason = "ignored-relationship";
                return false;
            }

            GameplayPropertiesComponent gameplayProperties;
            if (_gameworld.EntitySystem.TryGetComponent<GameplayPropertiesComponent>(entity, out gameplayProperties) && gameplayProperties != null && gameplayProperties.InteractiveObject)
            {
                rejectionReason = "interactive-object";
                return false;
            }

            return true;
        }

        private void PopulateChosenTargetDiagnostics(AiPlanningDiagnostics diagnostics, Entity entity, IRelationship relationship, IntVector2D candidatePosition)
        {
            if (diagnostics == null || entity == null)
            {
                return;
            }

            diagnostics.DebugChosenEnemyId = entity.Id;
            diagnostics.DebugEnemyX = candidatePosition.X;
            diagnostics.DebugEnemyY = candidatePosition.Y;
            diagnostics.DebugAttackUsedSelfTarget = entity == _agent;
            diagnostics.DebugChosenTargetRelationship = DescribeRelationship(entity, relationship);
            diagnostics.DebugEnemyReason = diagnostics.DebugChosenTargetRelationship;

            TeamComponent team;
            if (_gameworld.EntitySystem.TryGetComponent<TeamComponent>(entity, out team) && team != null)
            {
                diagnostics.DebugChosenEnemyTeamId = team.TeamID;
            }

            GameplayPropertiesComponent gameplayProperties;
            if (_gameworld.EntitySystem.TryGetComponent<GameplayPropertiesComponent>(entity, out gameplayProperties) && gameplayProperties != null)
            {
                diagnostics.DebugChosenEnemyInteractiveObject = gameplayProperties.InteractiveObject;
                diagnostics.DebugChosenEnemyIsPlayersPlayerCharacter = gameplayProperties.IsPlayersPlayerCharacter;
            }

            ControlComponent control;
            if (_gameworld.EntitySystem.TryGetComponent<ControlComponent>(entity, out control) && control != null)
            {
                diagnostics.DebugChosenEnemyControlPlayerId = control.PlayerId;
                diagnostics.DebugChosenEnemyControlAi = control.IsAIControlled;
            }
        }

        private static string DescribeRelationship(Entity entity, IRelationship relationship)
        {
            if (entity == null)
            {
                return null;
            }

            if (relationship == null)
            {
                return "unknown";
            }

            if (entity != null && relationship.Id == Relationship.Ally.Id)
            {
                return "ally";
            }

            if (relationship.Id == Relationship.Enemy.Id)
            {
                return "hostile";
            }

            if (relationship.Id == Relationship.Neutral.Id)
            {
                return "neutral";
            }

            return "unknown";
        }

        private static string DescribeDryRunFailure(ActivityEvaluationResult evaluation)
        {
            if (evaluation == null)
            {
                return "dry-run-null";
            }

            if (!evaluation.SkillWasSuccessful)
            {
                return "dry-run-unsuccessful";
            }

            if (evaluation.TargetWorkspaces == null || !evaluation.TargetWorkspaces.Any())
            {
                return "dry-run-no-workspaces";
            }

            return "dry-run-rejected";
        }

        private static void ResetAttackDiagnostics(AiPlanningDiagnostics diagnostics)
        {
            if (diagnostics == null)
            {
                return;
            }

            diagnostics.DebugAttackFailureReason = null;
            diagnostics.DebugAttackCandidateCount = null;
            diagnostics.DebugAttackEvaluatedTargetCount = null;
            diagnostics.DebugAttackUsedSelfTarget = null;
            diagnostics.DebugChosenTargetRelationship = null;
        }

        private static void SetAttackFailure(AiPlanningDiagnostics diagnostics, string reason)
        {
            if (diagnostics == null || string.IsNullOrEmpty(reason))
            {
                return;
            }

            diagnostics.DebugAttackFailureReason = reason;
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