using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.GameLogic;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Pathfinding;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation.Serialization;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic
{
    internal static class AiAgentSnapshotFactory
    {
        public static IAgentSnapshot Create(IGameworldInstance gameworld, Entity agent)
        {
            try
            {
                if (gameworld == null || gameworld.EntitySystem == null || agent == null)
                {
                    return null;
                }

                APositionComponent positionComponent;
                if (!gameworld.EntitySystem.TryGetComponent<APositionComponent>(agent, out positionComponent) || positionComponent == null)
                {
                    return null;
                }

                var range = 0;
                var walkRange = 0;
                var sprintRange = 0;
                var accuracy = 0.5f;
                var toHitModifier = 0f;

                AttributeBackedStatusValueContainer statusValues;
                if (gameworld.EntitySystem.TryGetComponent<AttributeBackedStatusValueContainer>(agent, out statusValues) && statusValues != null)
                {
                    range = (int)statusValues.GetByIdOrReturnDefault(458766UL);
                    walkRange = statusValues.GetWalkRange();
                    sprintRange = statusValues.GetSprintRange();
                    accuracy = statusValues.GetByIdOrReturnDefault(458757UL);
                    toHitModifier = statusValues.GetByIdOrReturnDefault(458768UL);
                }

                NodeList reachablePositions = null;
                try
                {
                    if (gameworld.ReachableRangesCalculator != null)
                    {
                        reachablePositions = gameworld.ReachableRangesCalculator.GetReachabilty(agent);
                    }
                }
                catch
                {
                    reachablePositions = null;
                }

                return new EngineAgentSnapshot(agent, positionComponent.GridPosition, range, walkRange, sprintRange, accuracy, toHitModifier, reachablePositions);
            }
            catch
            {
                return null;
            }
        }

        public static IntVector2D TryGetGridPositionOrDefault(IGameworldInstance gameworld, Entity agent)
        {
            if (gameworld == null || gameworld.EntitySystem == null || agent == null)
            {
                return IntVector2D.Zero;
            }

            try
            {
                APositionComponent positionComponent;
                if (gameworld.EntitySystem.TryGetComponent<APositionComponent>(agent, out positionComponent) && positionComponent != null)
                {
                    return positionComponent.GridPosition;
                }
            }
            catch
            {
            }

            return IntVector2D.Zero;
        }

        private sealed class EngineAgentSnapshot : IAgentSnapshot
        {
            public EngineAgentSnapshot(Entity entity, IntVector2D position, int range, int walkRange, int sprintRange, float accuracy, float toHitModifier, NodeList reachablePositions)
            {
                Entity = entity;
                Position = position;
                Range = range;
                WalkRange = walkRange;
                SprintRange = sprintRange;
                Accuracy = accuracy;
                ToHitModifier = toHitModifier;
                ReachablePositions = reachablePositions;
            }

            public int Range { get; private set; }
            public IntVector2D Position { get; private set; }
            public int WalkRange { get; private set; }
            public int SprintRange { get; private set; }
            public float Accuracy { get; private set; }
            public float ToHitModifier { get; private set; }
            public Entity Entity { get; private set; }
            public NodeList ReachablePositions { get; set; }
            public IPointOfInterestDefinition MainPointOfInterest { get; set; }
        }
    }
}