using System;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic.Valuations
{
    internal abstract class AValuation : IValuation
    {
        public float Weight { get; set; }

        public float Weighted(IValuationContext context, Entity target, IntVector2D position)
        {
            float valuation = Valuate(context, target, position);
            if (valuation < 0f || valuation > 1f)
            {
                valuation = 0f;
            }

            return Weight * valuation;
        }

        protected abstract float Valuate(IValuationContext context, Entity target, IntVector2D position);
    }
}