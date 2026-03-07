using System;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Pathfinding;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic.Valuations
{
    internal sealed class WalkDistanceToEnemies : AValuation
    {
        protected override float Valuate(IValuationContext context, Entity target, IntVector2D position)
        {
            if (context == null || context.ProtagonistSnapshot == null || !context.EnemySnapshots.Any() || (context.ProtagonistSnapshot.MainPointOfInterest != null && context.ProtagonistSnapshot.MainPointOfInterest.Priority == PointOfInterestPriority.High))
            {
                return 0f;
            }

            int sprintRange = context.ProtagonistSnapshot.SprintRange;
            float closest = float.MaxValue;
            foreach (IAgentSnapshot enemy in context.EnemySnapshots)
            {
                int distance = int.MaxValue;
                int targetRange = enemy.Range + enemy.SprintRange;
                if ((enemy.Position - position).SquaredAbs() > targetRange * targetRange)
                {
                    continue;
                }

                if (enemy.ReachablePositions != null)
                {
                    foreach (Node reachable in enemy.ReachablePositions.Values)
                    {
                        if (reachable.Position.IsAdjacent(position))
                        {
                            distance = Math.Min(distance, reachable.MovementSteps);
                        }
                    }
                }

                if (distance == int.MaxValue)
                {
                    distance = 20 + (int)IntVector2DExtensions.CalculateCustomDistance(position, enemy.Position);
                }

                if ((float)distance < closest)
                {
                    closest = distance;
                }
            }

            return (float)sprintRange / ((float)sprintRange + closest);
        }
    }
}