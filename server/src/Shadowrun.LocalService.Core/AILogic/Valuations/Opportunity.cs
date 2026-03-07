using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation.Serialization;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic.Valuations
{
    internal sealed class Opportunity : AValuation
    {
        public float InSprintRangeMalus { get; set; }

        protected override float Valuate(IValuationContext context, Entity target, IntVector2D position)
        {
            if (context == null || context.ProtagonistSnapshot == null)
            {
                return 0f;
            }

            IPointOfInterestDefinition mainPointOfInterest = context.ProtagonistSnapshot.MainPointOfInterest;
            if (!context.EnemySnapshots.Any() || (mainPointOfInterest != null && mainPointOfInterest.Priority == PointOfInterestPriority.High))
            {
                return 0f;
            }

            float bestChanceToHit = context.EnemySnapshots.Max(delegate(IAgentSnapshot enemy)
            {
                return context.ChanceToHit(position, context.ProtagonistSnapshot.Range, context.ProtagonistSnapshot.Accuracy, enemy.ToHitModifier, enemy.Entity);
            });

            if (!context.IsWithinWalkRange(position) && !position.Equals(context.ProtagonistSnapshot.Position))
            {
                bestChanceToHit *= InSprintRangeMalus;
            }

            return bestChanceToHit;
        }
    }
}