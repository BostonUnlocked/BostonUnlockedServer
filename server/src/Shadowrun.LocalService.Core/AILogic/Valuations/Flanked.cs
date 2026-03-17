using System;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Map;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic.Valuations
{
    internal sealed class Flanked : AValuation
    {
        protected override float Valuate(IValuationContext context, Entity target, IntVector2D position)
        {
            if (context == null || context.Gameworld == null || context.Gameworld.CoverSystem == null || !context.EnemySnapshots.Any())
            {
                return 0f;
            }

            ICoverSystem coverSystem = context.Gameworld.CoverSystem;
            int flankingEnemies = context.EnemySnapshots.Count(delegate(IAgentSnapshot enemy)
            {
                return coverSystem.DetermineCover(enemy.Position, position) == NoCover.Instance;
            });

            return 1f - (float)Math.Pow(0.5, flankingEnemies);
        }
    }
}