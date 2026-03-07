using System;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic.Valuations
{
    internal sealed class FlankingAvoidance : AValuation
    {
        public float ForecastMultiplier { private get; set; }

        protected override float Valuate(IValuationContext context, Entity target, IntVector2D position)
        {
            if (context == null || context.ProtagonistSnapshot == null || !context.EnemySnapshots.Any())
            {
                return 0f;
            }

            float cumulativeImpact = 0f;
            foreach (IAgentSnapshot enemy in context.EnemySnapshots)
            {
                IntVector2D enemyPosition = enemy.Position;
                int enemyRange = enemy.Range + enemy.WalkRange;
                float chanceToHit = context.ChanceToHit(enemyPosition, enemyRange, context.ProtagonistSnapshot.Accuracy, enemy.ToHitModifier, target);

                if (position.Y != enemyPosition.Y && Math.Abs(position.X - enemyPosition.X) <= enemy.WalkRange)
                {
                    IntVector2D xFlank = new IntVector2D(position.X, enemyPosition.Y);
                    float chanceToHitX = context.ChanceToHit(xFlank, enemy.Range, context.ProtagonistSnapshot.Accuracy, enemy.ToHitModifier, target) * ForecastMultiplier;
                    chanceToHit = Math.Max(chanceToHit, chanceToHitX);
                }

                if (position.X != enemyPosition.X && Math.Abs(position.Y - enemyPosition.Y) <= enemy.WalkRange)
                {
                    IntVector2D yFlank = new IntVector2D(enemyPosition.X, position.Y);
                    float chanceToHitY = context.ChanceToHit(yFlank, enemy.Range, context.ProtagonistSnapshot.Accuracy, enemy.ToHitModifier, target) * ForecastMultiplier;
                    chanceToHit = Math.Max(chanceToHit, chanceToHitY);
                }

                cumulativeImpact += Intensify(chanceToHit);
                if (cumulativeImpact >= 2f)
                {
                    break;
                }
            }

            return (float)(1.0 - Math.Pow(0.5, cumulativeImpact));
        }

        public static float Intensify(float chanceToHit)
        {
            if (Math.Abs(chanceToHit - 0.5f) < 0.001f)
            {
                return 0.5f;
            }

            return (float)((double)chanceToHit - Math.Sin(Math.PI * 2.0 * (double)chanceToHit) * 0.15);
        }
    }
}