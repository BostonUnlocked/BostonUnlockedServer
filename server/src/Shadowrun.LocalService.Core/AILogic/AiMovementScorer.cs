using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence.Serialization;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Placeholder movement scorer: converts assessment definitions into valuations and sums them for candidate positions.
    /// </summary>
    public sealed class AiMovementScorer
    {
        private readonly IValuationFactory _valuationFactory;

        public AiMovementScorer(IValuationFactory valuationFactory)
        {
            _valuationFactory = valuationFactory;
        }

        public float ScorePosition(IValuationContext context, IEnumerable<AWeightedAssessmentDefinition> assessments, Entity target, IntVector2D position)
        {
            if (assessments == null)
            {
                return 0f;
            }

            float score = 0f;
            foreach (var def in assessments)
            {
                if (def == null)
                {
                    continue;
                }

                var valuation = def.CreateValuation(_valuationFactory);
                if (valuation == null)
                {
                    continue;
                }

                score += valuation.Weighted(context, target, position);
            }

            return score;
        }
    }
}
