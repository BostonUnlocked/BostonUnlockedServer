using System;
using System.Collections.Generic;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic.Valuations
{
    internal abstract class OptimalDistance : AValuation
    {
        public int DesiredDistance { private get; set; }

        protected float Valuate(IValuationContext context, ICollection<IAgentSnapshot> agentSnapshots, IntVector2D position)
        {
            if (context == null || !context.PerceivedEnemies.Any())
            {
                return 0f;
            }

            double worstValuation = 1.0;
            foreach (Entity agent in context.PerceivedEnemies)
            {
                IntVector2D agentPosition = context.Gameworld.EntitySystem.GetAgentGridPosition(agent);
                float distance = IntVector2DExtensions.CalculateCustomDistance(position, agentPosition);
                double weight;
                if (distance <= (float)DesiredDistance)
                {
                    weight = 1.0 / (1.0 + (double)DesiredDistance - (double)distance);
                }
                else
                {
                    float overshoot = (float)DesiredDistance - distance;
                    weight = Math.Max(0.5f, 1f - overshoot * -0.05f);
                }

                worstValuation = Math.Min(weight, worstValuation);
            }

            return (float)worstValuation;
        }
    }
}