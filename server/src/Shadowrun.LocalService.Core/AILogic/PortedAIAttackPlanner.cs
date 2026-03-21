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
        private const int MaxAttackTargetDetails = 64;

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
            var detailCount = 0;
            string firstFailure = null;
            string firstEvaluatedFailure = null;
            var rejectionCounts = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var targetDetails = new System.Collections.Generic.List<AiAttackTargetScanEntry>();

            foreach (var entity in _gameworld.EntitySystem.GetAllEntities())
            {
                AiAttackTargetScanEntry targetDetail = CreateTargetDetail(entity, attackerPosition);
                if (targetDetail != null)
                {
                    detailCount++;
                }

                IRelationship relationship;
                string rejectionReason;
                if (!IsValidTarget(entity, attackerPosition, myTeam.TeamID, teamInfo, out relationship, out rejectionReason))
                {
                    if (targetDetail != null)
                    {
                        targetDetail.Relationship = DescribeRelationship(entity, relationship);
                        targetDetail.RejectionReason = rejectionReason;
                        AppendTargetDetail(targetDetails, targetDetail);
                    }
                    IncrementReason(rejectionCounts, rejectionReason);
                    if (firstFailure == null && !string.IsNullOrEmpty(rejectionReason))
                    {
                        firstFailure = rejectionReason;
                    }
                    continue;
                }

                candidateCount++;
                if (targetDetail != null)
                {
                    targetDetail.Relationship = DescribeRelationship(entity, relationship);
                }

                IntVector2D candidatePosition;
                if (!TryResolveBestBlockedTargetPosition(entity, attackerPosition, out candidatePosition))
                {
                    if (targetDetail != null)
                    {
                        targetDetail.RejectionReason = "no-blocked-position";
                        AppendTargetDetail(targetDetails, targetDetail);
                    }
                    IncrementReason(rejectionCounts, "no-blocked-position");
                    if (firstFailure == null)
                    {
                        firstFailure = "no-blocked-position";
                    }
                    continue;
                }

                if (targetDetail != null)
                {
                    targetDetail.CandidateX = candidatePosition.X;
                    targetDetail.CandidateY = candidatePosition.Y;
                }

                evaluatedCount++;

                ActivityEvaluationResult evaluation;
                try
                {
                    evaluation = dryRunner.DryRunActivity(0, 0, skillId, _agent, candidatePosition);
                }
                catch
                {
                    if (targetDetail != null)
                    {
                        targetDetail.RejectionReason = "dry-run-exception";
                        targetDetail.DryRunSkillSuccessful = false;
                        targetDetail.DryRunTargetWorkspaceCount = 0;
                        AppendTargetDetail(targetDetails, targetDetail);
                    }
                    IncrementReason(rejectionCounts, "dry-run-exception");
                    if (firstFailure == null)
                    {
                        firstFailure = "dry-run-exception";
                    }
                    continue;
                }

                if (evaluation == null || !evaluation.SkillWasSuccessful || evaluation.TargetWorkspaces == null || !evaluation.TargetWorkspaces.Any())
                {
                    var dryRunFailure = DescribeDryRunFailure(evaluation);
                    if (firstEvaluatedFailure == null)
                    {
                        firstEvaluatedFailure = dryRunFailure;
                    }
                    if (targetDetail != null)
                    {
                        targetDetail.RejectionReason = dryRunFailure;
                        targetDetail.DryRunSkillSuccessful = evaluation != null ? (bool?)evaluation.SkillWasSuccessful : null;
                        targetDetail.DryRunTargetWorkspaceCount = evaluation != null && evaluation.TargetWorkspaces != null ? (int?)evaluation.TargetWorkspaces.Count() : null;
                        AppendTargetDetail(targetDetails, targetDetail);
                    }
                    IncrementReason(rejectionCounts, dryRunFailure);
                    if (firstFailure == null)
                    {
                        firstFailure = dryRunFailure;
                    }
                    continue;
                }

                var candidateScore = EffectiveChanceToHitTargets(evaluation);
                if (targetDetail != null)
                {
                    targetDetail.RejectionReason = "viable";
                    targetDetail.DryRunSkillSuccessful = true;
                    targetDetail.DryRunTargetWorkspaceCount = evaluation.TargetWorkspaces.Count();
                }
                IncrementReason(rejectionCounts, "viable");
                if (!found || candidateScore > score)
                {
                    found = true;
                    score = candidateScore;
                    targetPosition = candidatePosition;
                    PopulateChosenTargetDiagnostics(diagnostics, entity, relationship, candidatePosition);
                    if (targetDetail != null)
                    {
                        targetDetail.ChosenTarget = true;
                    }
                }

                if (targetDetail != null)
                {
                    AppendTargetDetail(targetDetails, targetDetail);
                }
            }

            if (diagnostics != null)
            {
                diagnostics.DebugEnemyCandidateCount = detailCount;
                diagnostics.DebugAttackCandidateCount = candidateCount;
                diagnostics.DebugAttackEvaluatedTargetCount = evaluatedCount;
                diagnostics.DebugAttackDetailCount = detailCount;
                diagnostics.DebugAttackRejectionCounts = rejectionCounts
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new AiReasonCount { Reason = pair.Key, Count = pair.Value })
                    .ToArray();
                diagnostics.DebugAttackTargetDetails = targetDetails.ToArray();
            }

            if (!found)
            {
                SetAttackFailure(diagnostics, ResolveTerminalFailureReason(evaluatedCount, rejectionCounts, firstEvaluatedFailure, firstFailure, candidateCount));
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
            if (IntVector2DExtensions.CalculateCustomDistance(attackerPosition, targetPosition) > detection.Range)
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
            diagnostics.DebugAttackDetailCount = null;
            diagnostics.DebugAttackRejectionCounts = null;
            diagnostics.DebugAttackTargetDetails = null;
        }

        private static void SetAttackFailure(AiPlanningDiagnostics diagnostics, string reason)
        {
            if (diagnostics == null || string.IsNullOrEmpty(reason))
            {
                return;
            }

            diagnostics.DebugAttackFailureReason = reason;
        }

        private bool TryResolveBestBlockedTargetPosition(Entity entity, IntVector2D attackerPosition, out IntVector2D bestPosition)
        {
            bestPosition = IntVector2D.Zero;

            IPositionComponent position;
            if (!_gameworld.EntitySystem.TryGetComponent<IPositionComponent>(entity, out position) || position == null || position.BlockedGridPositions == null)
            {
                return false;
            }

            var found = false;
            var bestDistance = float.MaxValue;
            foreach (var blocked in position.BlockedGridPositions)
            {
                var distance = IntVector2DExtensions.CalculateCustomDistance(attackerPosition, blocked);
                if (!found || distance < bestDistance)
                {
                    found = true;
                    bestDistance = distance;
                    bestPosition = blocked;
                }
            }

            return found;
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

        private static string ResolveTerminalFailureReason(
            int evaluatedCount,
            System.Collections.Generic.IDictionary<string, int> rejectionCounts,
            string firstEvaluatedFailure,
            string firstFailure,
            int candidateCount)
        {
            if (evaluatedCount > 0)
            {
                if (HasReason(rejectionCounts, "dry-run-unsuccessful"))
                {
                    return "dry-run-unsuccessful";
                }

                if (HasReason(rejectionCounts, "dry-run-no-workspaces"))
                {
                    return "dry-run-no-workspaces";
                }

                if (HasReason(rejectionCounts, "dry-run-null"))
                {
                    return "dry-run-null";
                }

                if (HasReason(rejectionCounts, "dry-run-exception"))
                {
                    return "dry-run-exception";
                }

                if (!string.IsNullOrEmpty(firstEvaluatedFailure))
                {
                    return firstEvaluatedFailure;
                }
            }

            if (!string.IsNullOrEmpty(firstFailure))
            {
                return firstFailure;
            }

            return candidateCount == 0 ? "no-valid-targets" : "no-viable-dry-run-target";
        }

        private static bool HasReason(System.Collections.Generic.IDictionary<string, int> rejectionCounts, string reason)
        {
            if (rejectionCounts == null || string.IsNullOrEmpty(reason))
            {
                return false;
            }

            int count;
            return rejectionCounts.TryGetValue(reason, out count) && count > 0;
        }

        private static void IncrementReason(System.Collections.Generic.IDictionary<string, int> counts, string reason)
        {
            if (counts == null || string.IsNullOrEmpty(reason))
            {
                return;
            }

            int count;
            if (!counts.TryGetValue(reason, out count))
            {
                count = 0;
            }

            counts[reason] = count + 1;
        }

        private static void AppendTargetDetail(System.Collections.Generic.ICollection<AiAttackTargetScanEntry> details, AiAttackTargetScanEntry detail)
        {
            if (details == null || detail == null)
            {
                return;
            }

            if (details.Count >= MaxAttackTargetDetails)
            {
                return;
            }

            details.Add(detail);
        }

        private AiAttackTargetScanEntry CreateTargetDetail(Entity entity, IntVector2D attackerPosition)
        {
            if (entity == null || entity == _agent || _gameworld == null || _gameworld.EntitySystem == null)
            {
                return null;
            }

            var detail = new AiAttackTargetScanEntry
            {
                EntityId = entity.Id,
            };

            TeamComponent team;
            if (_gameworld.EntitySystem.TryGetComponent<TeamComponent>(entity, out team) && team != null)
            {
                detail.TeamId = team.TeamID;
            }

            var position = AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, entity);
            detail.X = position.X;
            detail.Y = position.Y;
            detail.DistanceToAttacker = IntVector2DExtensions.CalculateCustomDistance(attackerPosition, position);

            DetectionComponent detection;
            detail.HasDetection = _gameworld.EntitySystem.TryGetComponent<DetectionComponent>(entity, out detection) && detection != null;

            GameplayPropertiesComponent gameplayProperties;
            if (_gameworld.EntitySystem.TryGetComponent<GameplayPropertiesComponent>(entity, out gameplayProperties) && gameplayProperties != null)
            {
                detail.InteractiveObject = gameplayProperties.InteractiveObject;
                detail.IsPlayersPlayerCharacter = gameplayProperties.IsPlayersPlayerCharacter;
            }

            detail.HasStatus = _gameworld.EntitySystem.HasComponent<AttributeBackedStatusValueContainer>(entity);
            detail.IsDeadOrDespawned = _gameworld.EntitySystem.IsAgentDeadOrDespawned(entity);
            return detail;
        }
    }
}