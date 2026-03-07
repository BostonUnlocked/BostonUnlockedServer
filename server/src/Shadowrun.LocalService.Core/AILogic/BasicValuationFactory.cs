using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Shadowrun.LocalService.Core.AILogic.Valuations;

namespace Shadowrun.LocalService.Core.AILogic
{
    public sealed class BasicValuationFactory : IValuationFactory
    {
        public IValuation CreateFlankedValuation(float weight)
        {
            return new Flanked
            {
                Weight = weight,
            };
        }

        public IValuation CreateOpportunityValuation(float weight, float inSprintRangeMalus)
        {
            return new Opportunity
            {
                Weight = weight,
                InSprintRangeMalus = inSprintRangeMalus,
            };
        }

        public IValuation CreateThreatValuation(float weight, float forecastMultiplier)
        {
            return new FlankingAvoidance
            {
                Weight = weight,
                ForecastMultiplier = forecastMultiplier,
            };
        }

        public IValuation CreateWalkDistanceToEnemiesValuation(float weight)
        {
            return new WalkDistanceToEnemies
            {
                Weight = weight,
            };
        }

        public IValuation CreateOptimalDistanceToEnemiesValuation(float weight, int desiredDistance)
        {
            return new OptimalDistanceEnemies
            {
                Weight = weight,
                DesiredDistance = desiredDistance,
            };
        }

        public IValuation CreateCloseToPointOfInterestValuation(float weight)
        {
            return new CloseToPointOfInterest
            {
                Weight = weight,
            };
        }
    }
}
