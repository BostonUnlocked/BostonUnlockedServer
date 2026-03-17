using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic.Valuations
{
    internal sealed class OptimalDistanceEnemies : OptimalDistance
    {
        protected override float Valuate(IValuationContext context, Entity target, IntVector2D position)
        {
            if (context == null || context.ProtagonistSnapshot == null || !context.EnemySnapshots.Any() || (context.ProtagonistSnapshot.MainPointOfInterest != null && context.ProtagonistSnapshot.MainPointOfInterest.Priority == PointOfInterestPriority.High))
            {
                return 0f;
            }

            return Valuate(context, context.EnemySnapshots, position);
        }
    }
}