using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence.Serialization;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Locomotion;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic
{
    internal sealed class PortedAIMovementPlanner
    {
        private const float ImprovementFraction = 0.025f;

        private readonly IGameworldInstance _gameworld;
        private readonly Entity _agent;
        private readonly AiMovementScorer _movementScorer;

        public PortedAIMovementPlanner(IGameworldInstance gameworld, Entity agent, IValuationFactory valuationFactory)
        {
            _gameworld = gameworld;
            _agent = agent;
            _movementScorer = new AiMovementScorer(valuationFactory);
        }

        public bool TryPlanMove(AIBehaviourConfigurationComponent config, AiPlanningDiagnostics diagnostics, out IntVector2D targetPosition, out float score)
        {
            targetPosition = IntVector2D.Zero;
            score = 0f;

            if (_gameworld == null || _gameworld.ReachableRangesCalculator == null || _agent == null || config == null || config.MovementAssessments == null || !config.MovementAssessments.Any())
            {
                return false;
            }

            var snapshot = AiAgentSnapshotFactory.Create(_gameworld, _agent);
            if (snapshot == null)
            {
                return false;
            }

            var context = new BasicValuationContext(_gameworld, snapshot);
            context.Refresh();

            var currentPosition = AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, _agent);
            var baseline = _movementScorer.ScorePosition(context, config.MovementAssessments, _agent, currentPosition);
            PopulateMovementDiagnostics(diagnostics, context, currentPosition, baseline);

            var ranges = default(ReachableRanges);
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

            if (diagnostics != null)
            {
                diagnostics.DebugReachableCellCount = ranges.SprintRange.ReachableCells.Count();
            }

            var found = false;
            var bestScore = float.MinValue;
            var maxPositiveWeight = SumPositiveWeights(config.MovementAssessments);
            var minNegativeWeight = SumNegativeWeights(config.MovementAssessments);
            var improvement = ImprovementFraction * (maxPositiveWeight - minNegativeWeight);
            foreach (var cell in ranges.SprintRange.ReachableCells)
            {
                if (cell == currentPosition)
                {
                    continue;
                }

                var candidateScore = _movementScorer.ScorePosition(context, config.MovementAssessments, _agent, cell);
                if (candidateScore > bestScore)
                {
                    bestScore = candidateScore;
                }

                if (candidateScore <= baseline + improvement)
                {
                    continue;
                }

                if (!found || candidateScore > score)
                {
                    found = true;
                    score = candidateScore;
                    targetPosition = cell;
                    PopulateChosenMoveDiagnostics(diagnostics, context, currentPosition, cell);
                }
            }

            if (!found)
            {
                return false;
            }

            var pruneLimit = System.Math.Max(bestScore - improvement, baseline + improvement);
            if (score <= pruneLimit)
            {
                targetPosition = IntVector2D.Zero;
                score = 0f;
                return false;
            }

            return true;
        }

        public bool TryPlanMoveTowardPreferredHostile(AIBehaviourConfigurationComponent config, AiPlanningDiagnostics diagnostics, out IntVector2D targetPosition, out float score)
        {
            targetPosition = IntVector2D.Zero;
            score = 0f;

            if (_gameworld == null || _gameworld.ReachableRangesCalculator == null || _agent == null)
            {
                return false;
            }

            var snapshot = AiAgentSnapshotFactory.Create(_gameworld, _agent);
            if (snapshot == null)
            {
                return false;
            }

            var context = new BasicValuationContext(_gameworld, snapshot);
            context.Refresh();

            var currentPosition = AiAgentSnapshotFactory.TryGetGridPositionOrDefault(_gameworld, _agent);
            var preferredEnemy = SelectPreferredEnemy(context, currentPosition);
            if (preferredEnemy == null)
            {
                return false;
            }

            var assessments = config != null ? config.MovementAssessments : null;
            var baseline = _movementScorer.ScorePosition(context, assessments, _agent, currentPosition);
            PopulateMovementDiagnostics(diagnostics, context, currentPosition, baseline);

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

            var reachableCells = ranges.SprintRange.ReachableCells.ToArray();
            if (diagnostics != null)
            {
                diagnostics.DebugReachableCellCount = reachableCells.Length;
            }

            var currentDistance = (int)IntVector2DExtensions.CalculateCustomDistance(currentPosition, preferredEnemy.Position);
            var reducingCellCount = 0;
            var found = false;
            var bestDistance = int.MaxValue;
            var bestScore = float.MinValue;

            foreach (var cell in reachableCells)
            {
                if (cell == currentPosition)
                {
                    continue;
                }

                var candidateDistance = (int)IntVector2DExtensions.CalculateCustomDistance(cell, preferredEnemy.Position);
                if (candidateDistance >= currentDistance)
                {
                    continue;
                }

                reducingCellCount++;

                var candidateScore = _movementScorer.ScorePosition(context, assessments, _agent, cell);
                if (!found || candidateDistance < bestDistance || (candidateDistance == bestDistance && candidateScore > bestScore))
                {
                    found = true;
                    bestDistance = candidateDistance;
                    bestScore = candidateScore;
                    targetPosition = cell;
                    score = candidateScore;
                }
            }

            if (diagnostics != null)
            {
                diagnostics.DebugReducingCellCount = reducingCellCount;
            }

            if (!found)
            {
                return false;
            }

            PopulateChosenMoveDiagnostics(diagnostics, context, currentPosition, targetPosition);
            return true;
        }

        private static void PopulateMovementDiagnostics(AiPlanningDiagnostics diagnostics, BasicValuationContext context, IntVector2D currentPosition, float baseline)
        {
            if (diagnostics == null)
            {
                return;
            }

            diagnostics.DebugMoveSourceX = currentPosition.X;
            diagnostics.DebugMoveSourceY = currentPosition.Y;
            diagnostics.DebugMoveBaselineScore = baseline;

            IAgentSnapshot preferredEnemy = SelectPreferredEnemy(context, currentPosition);
            if (preferredEnemy == null || preferredEnemy.Entity == null)
            {
                return;
            }

            diagnostics.DebugPreferredEnemyId = preferredEnemy.Entity.Id;
            diagnostics.DebugEnemyX = preferredEnemy.Position.X;
            diagnostics.DebugEnemyY = preferredEnemy.Position.Y;
            diagnostics.DebugCurrentDistToEnemy = (int)IntVector2DExtensions.CalculateCustomDistance(currentPosition, preferredEnemy.Position);
            diagnostics.DebugEnemyReason = "movement-preferred-hostile";
        }

        private static void PopulateChosenMoveDiagnostics(AiPlanningDiagnostics diagnostics, BasicValuationContext context, IntVector2D currentPosition, IntVector2D targetPosition)
        {
            if (diagnostics == null)
            {
                return;
            }

            IAgentSnapshot preferredEnemy = SelectPreferredEnemy(context, currentPosition);
            if (preferredEnemy == null)
            {
                return;
            }

            diagnostics.DebugChosenMoveDistToEnemy = (int)IntVector2DExtensions.CalculateCustomDistance(targetPosition, preferredEnemy.Position);
        }

        private static IAgentSnapshot SelectPreferredEnemy(BasicValuationContext context, IntVector2D currentPosition)
        {
            if (context == null || context.EnemySnapshots == null)
            {
                return null;
            }

            IAgentSnapshot best = null;
            float bestDistance = float.MaxValue;
            foreach (var enemy in context.EnemySnapshots)
            {
                if (enemy == null || enemy.Entity == null)
                {
                    continue;
                }

                var distance = IntVector2DExtensions.CalculateCustomDistance(currentPosition, enemy.Position);
                if (best == null || distance < bestDistance)
                {
                    best = enemy;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private static float SumPositiveWeights(System.Collections.Generic.IEnumerable<AWeightedAssessmentDefinition> assessments)
        {
            if (assessments == null)
            {
                return 0f;
            }

            float sum = 0f;
            foreach (var assessment in assessments)
            {
                if (assessment != null && assessment.Weight > 0f)
                {
                    sum += assessment.Weight;
                }
            }

            return sum;
        }

        private static float SumNegativeWeights(System.Collections.Generic.IEnumerable<AWeightedAssessmentDefinition> assessments)
        {
            if (assessments == null)
            {
                return 0f;
            }

            float sum = 0f;
            foreach (var assessment in assessments)
            {
                if (assessment != null && assessment.Weight < 0f)
                {
                    sum += assessment.Weight;
                }
            }

            return sum;
        }
    }
}