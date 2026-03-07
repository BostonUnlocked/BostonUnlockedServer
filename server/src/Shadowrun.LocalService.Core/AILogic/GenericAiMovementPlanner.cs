using System;
using System.Collections.Generic;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Locomotion;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation.Serialization;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic
{
    internal sealed class GenericAiMovementPlanner
    {
        private readonly IGameworldInstance _gameworld;
        private readonly Entity _agent;

        public GenericAiMovementPlanner(IGameworldInstance gameworld, Entity agent)
        {
            _gameworld = gameworld;
            _agent = agent;
        }

        public bool TryPlanAdvance(out IntVector2D targetPosition, out AiPlanningDiagnostics diagnostics)
        {
            targetPosition = IntVector2D.Zero;
            diagnostics = new AiPlanningDiagnostics
            {
                DecisionNote = "ported-advance",
                DebugStage = "ported-advance",
            };

            if (_gameworld == null || _gameworld.EntitySystem == null || _gameworld.ReachableRangesCalculator == null || _agent == null)
            {
                return false;
            }

            IntVector2D currentPosition = AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, _agent);

            ReachableRanges ranges;
            try
            {
                ranges = _gameworld.ReachableRangesCalculator.GetReachableRanges(_agent);
            }
            catch
            {
                return false;
            }

            if (ranges == null || ranges.SprintRange == null || ranges.SprintRange.ReachableCells == null)
            {
                return false;
            }

            Entity preferredEnemy;
            IntVector2D objectivePosition;
            IPointOfInterestDefinition pointOfInterest;
            if (!TryResolveObjective(currentPosition, out preferredEnemy, out objectivePosition, out pointOfInterest, diagnostics))
            {
                return false;
            }

            var currentDistance = IntVector2DExtensions.CalculateCustomDistance(currentPosition, objectivePosition);
            var bestScore = float.MinValue;
            var found = false;
            var reachableCount = 0;
            var reducingCount = 0;
            foreach (IntVector2D cell in ranges.SprintRange.ReachableCells)
            {
                reachableCount++;
                if (cell == currentPosition)
                {
                    continue;
                }

                if (pointOfInterest != null && pointOfInterest.TriggerArea != null && pointOfInterest.TriggerArea.Contains(cell))
                {
                    diagnostics.DebugReachableCellCount = reachableCount;
                    diagnostics.DebugReducingCellCount = reducingCount + 1;
                    diagnostics.DebugChosenMoveWithinWalkRange = ranges.WalkRange != null && ranges.WalkRange.Contains(cell);
                    diagnostics.DebugChosenMoveDistToEnemy = preferredEnemy != null ? (int?)CalculateDistance(cell, objectivePosition) : null;
                    diagnostics.DebugChosenMoveScore = 1000f;
                    diagnostics.DecisionNote = "ported-advance-poi";
                    diagnostics.DebugStage = "ported-advance-poi";
                    targetPosition = cell;
                    return true;
                }

                var candidateDistance = IntVector2DExtensions.CalculateCustomDistance(cell, objectivePosition);
                if (candidateDistance >= currentDistance)
                {
                    continue;
                }

                reducingCount++;
                var improvement = currentDistance - candidateDistance;
                var score = improvement * 10f;

                if (preferredEnemy != null && _gameworld.CoverSystem != null)
                {
                    try
                    {
                        score += _gameworld.CoverSystem.DetermineCover(cell, preferredEnemy).CTHPenalty;
                    }
                    catch
                    {
                    }
                }

                if (ranges.WalkRange != null && ranges.WalkRange.Contains(cell))
                {
                    score += 0.05f;
                }

                if (!found || score > bestScore)
                {
                    found = true;
                    bestScore = score;
                    targetPosition = cell;
                    diagnostics.DebugChosenMoveScore = score;
                    diagnostics.DebugChosenMoveWithinWalkRange = ranges.WalkRange != null && ranges.WalkRange.Contains(cell);
                    diagnostics.DebugChosenMoveDistToEnemy = preferredEnemy != null ? (int?)CalculateDistance(cell, objectivePosition) : null;
                }
            }

            diagnostics.DebugReachableCellCount = reachableCount;
            diagnostics.DebugReducingCellCount = reducingCount;
            return found;
        }

        private bool TryResolveObjective(IntVector2D currentPosition, out Entity preferredEnemy, out IntVector2D objectivePosition, out IPointOfInterestDefinition pointOfInterest, AiPlanningDiagnostics diagnostics)
        {
            preferredEnemy = null;
            objectivePosition = IntVector2D.Zero;
            pointOfInterest = null;

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

            if (_gameworld.PointOfInterestController != null)
            {
                try
                {
                    pointOfInterest = _gameworld.PointOfInterestController.GetActivePointOfInterest(myTeam.TeamID, currentPosition);
                }
                catch
                {
                    pointOfInterest = null;
                }
            }

            preferredEnemy = FindPreferredEnemy(currentPosition, myTeam.TeamID, teamInfo, diagnostics);
            if (pointOfInterest != null && pointOfInterest.Priority == PointOfInterestPriority.High)
            {
                objectivePosition = pointOfInterest.Position;
                diagnostics.DebugEnemyReason = "high-priority-poi";
                return true;
            }

            if (preferredEnemy != null)
            {
                objectivePosition = ResolveObjectivePosition(preferredEnemy, currentPosition);
                diagnostics.DebugEnemyReason = "hostile";
                return true;
            }

            if (pointOfInterest != null)
            {
                objectivePosition = pointOfInterest.Position;
                diagnostics.DebugEnemyReason = "poi";
                return true;
            }

            return false;
        }

        private Entity FindPreferredEnemy(IntVector2D currentPosition, int myTeamId, TeamInfoComponent teamInfo, AiPlanningDiagnostics diagnostics)
        {
            var visibleIds = new HashSet<int>();
            DetectionComponent myDetection;
            if (_gameworld.EntitySystem.TryGetComponent<DetectionComponent>(_agent, out myDetection) && myDetection != null)
            {
                foreach (Entity visible in myDetection.VisibleAgents)
                {
                    if (visible != null)
                    {
                        visibleIds.Add(visible.Id);
                    }
                }
            }

            Entity bestVisible = null;
            Entity bestAny = null;
            float visibleDistance = float.MaxValue;
            float anyDistance = float.MaxValue;
            var candidates = 0;

            foreach (Entity entity in _gameworld.EntitySystem.GetAllEntities())
            {
                if (entity == null || entity == _agent)
                {
                    continue;
                }

                TeamComponent team;
                if (!_gameworld.EntitySystem.TryGetComponent<TeamComponent>(entity, out team) || team == null)
                {
                    continue;
                }

                if (!teamInfo.GetRelationship(myTeamId, team.TeamID).Hostile)
                {
                    continue;
                }

                GameplayPropertiesComponent gameplayProperties;
                if (_gameworld.EntitySystem.TryGetComponent<GameplayPropertiesComponent>(entity, out gameplayProperties) && gameplayProperties != null && gameplayProperties.InteractiveObject)
                {
                    continue;
                }

                var enemyPosition = AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, entity);
                var distance = IntVector2DExtensions.CalculateCustomDistance(currentPosition, enemyPosition);
                candidates++;
                if (distance < anyDistance)
                {
                    anyDistance = distance;
                    bestAny = entity;
                }

                if (visibleIds.Contains(entity.Id) && distance < visibleDistance)
                {
                    visibleDistance = distance;
                    bestVisible = entity;
                }
            }

            diagnostics.DebugEnemyCandidateCount = candidates;
            var selected = bestVisible ?? bestAny;
            if (selected != null)
            {
                var selectedPos = AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, selected);
                diagnostics.DebugChosenEnemyId = selected.Id;
                diagnostics.DebugEnemyX = selectedPos.X;
                diagnostics.DebugEnemyY = selectedPos.Y;
                diagnostics.DebugCurrentDistToEnemy = CalculateDistance(currentPosition, selectedPos);
                diagnostics.DebugEnemyPick = bestVisible != null ? "visible-nearest" : "nearest";
            }

            return selected;
        }

        private IntVector2D ResolveObjectivePosition(Entity entity, IntVector2D currentPosition)
        {
            IPositionComponent position;
            if (_gameworld.EntitySystem.TryGetComponent<IPositionComponent>(entity, out position) && position != null && position.BlockedGridPositions != null)
            {
                IntVector2D best = position.GridPosition;
                float bestDistance = float.MaxValue;
                foreach (IntVector2D blocked in position.BlockedGridPositions)
                {
                    var distance = IntVector2DExtensions.CalculateCustomDistance(currentPosition, blocked);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = blocked;
                    }
                }

                return best;
            }

            return AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, entity);
        }

        private static int CalculateDistance(IntVector2D a, IntVector2D b)
        {
            return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        }
    }
}