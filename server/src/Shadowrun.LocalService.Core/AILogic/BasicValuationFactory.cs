using System;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Placeholder valuation factory.
    /// The real game likely provides concrete valuations with access to LOS, cover, flanking forecasts, etc.
    /// </summary>
    public sealed class BasicValuationFactory : IValuationFactory
    {
        public IValuation CreateFlankedValuation(float weight)
        {
            return new ConstantValuation(weight);
        }

        public IValuation CreateOpportunityValuation(float weight, float inSprintRangeMalus)
        {
            return new ConstantValuation(weight);
        }

        public IValuation CreateThreatValuation(float weight, float forecastMultiplier)
        {
            return new ConstantValuation(weight);
        }

        public IValuation CreateWalkDistanceToEnemiesValuation(float weight)
        {
            return new ConstantValuation(weight);
        }

        public IValuation CreateOptimalDistanceToEnemiesValuation(float weight, int desiredDistance)
        {
            return new ConstantValuation(weight);
        }

        public IValuation CreateCloseToPointOfInterestValuation(float weight)
        {
            return new ConstantValuation(weight);
        }

        private sealed class ConstantValuation : IValuation
        {
            public ConstantValuation(float weight)
            {
                Weight = weight;
            }

            public float Weight { get; set; }

            public float Weighted(IValuationContext context, Entity target, IntVector2D position)
            {
                // Placeholder: just return the configured weight.
                return Weight;
            }
        }
    }
}
