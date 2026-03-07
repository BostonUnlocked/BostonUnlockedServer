using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Locomotion;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic
{
    internal sealed class PortedAIMovementPlanner
    {
        private const float ImprovementThreshold = 0.025f;

        private readonly IGameworldInstance _gameworld;
        private readonly Entity _agent;
        private readonly AiMovementScorer _movementScorer;

        public PortedAIMovementPlanner(IGameworldInstance gameworld, Entity agent, IValuationFactory valuationFactory)
        {
            _gameworld = gameworld;
            _agent = agent;
            _movementScorer = new AiMovementScorer(valuationFactory);
        }

        public bool TryPlanMove(AIBehaviourConfigurationComponent config, out IntVector2D targetPosition, out float score)
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

            var found = false;
            foreach (var cell in ranges.SprintRange.ReachableCells)
            {
                if (cell == currentPosition)
                {
                    continue;
                }

                var candidateScore = _movementScorer.ScorePosition(context, config.MovementAssessments, _agent, cell);
                if (candidateScore < baseline + ImprovementThreshold)
                {
                    continue;
                }

                if (!found || candidateScore > score)
                {
                    found = true;
                    score = candidateScore;
                    targetPosition = cell;
                }
            }

            return found;
        }
    }
}