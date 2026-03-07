using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation.Serialization;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic.Valuations
{
    internal sealed class CloseToPointOfInterest : AValuation
    {
        protected override float Valuate(IValuationContext context, Entity target, IntVector2D position)
        {
            if (context == null || context.ProtagonistSnapshot == null)
            {
                return 0f;
            }

            if (context.PerceivedEnemies.Any() && context.ProtagonistSnapshot.MainPointOfInterest != null && context.ProtagonistSnapshot.MainPointOfInterest.Priority == PointOfInterestPriority.Low)
            {
                return 0f;
            }

            IPointOfInterestDefinition mainPointOfInterest = context.ProtagonistSnapshot.MainPointOfInterest;
            if (mainPointOfInterest == null)
            {
                return 0f;
            }

            if (mainPointOfInterest.TriggerArea.Contains(position))
            {
                return 1f;
            }

            if (mainPointOfInterest.TriggerArea.Contains(context.ProtagonistSnapshot.Position) && context.ProtagonistSnapshot.MainPointOfInterest.Priority == PointOfInterestPriority.High)
            {
                return 0f;
            }

            float distanceToPoi = IntVector2DExtensions.CalculateCustomDistance(position, mainPointOfInterest.Position);
            float protagonistToPointOfInterest = IntVector2DExtensions.CalculateCustomDistance(context.ProtagonistSnapshot.Position, mainPointOfInterest.Position);
            float result = 0.5f;
            if (distanceToPoi < protagonistToPointOfInterest)
            {
                result = 1f - 0.5f / (1f + (protagonistToPointOfInterest - distanceToPoi));
            }
            else if (distanceToPoi > protagonistToPointOfInterest)
            {
                result = 0.5f / (1f - (protagonistToPointOfInterest - distanceToPoi));
            }

            return result;
        }
    }
}