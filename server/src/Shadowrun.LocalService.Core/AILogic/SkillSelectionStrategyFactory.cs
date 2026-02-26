using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.RandomNumbers;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Creates runtime skill-selection strategies from serialized rotation config objects.
    /// Placeholder: selections currently return DefaultSkill (or 0).
    /// </summary>
    public sealed class SkillSelectionStrategyFactory : ISkillSelectionStrategyFactory
    {
        public IAISkillSelection CreateWeightedSelection(Entity entity, WeightedSkillSelection rotation, IRandomNumberGenerator rng)
        {
            return new WeightedSelection(entity, rotation, rng);
        }

        public IAISkillSelection CreateSimpleRotationSelection(Entity entity, RotationSkillSelection rotation)
        {
            return new SimpleRotationSelection(entity, rotation);
        }

        public IAISkillSelection CreateConditionalSkillRotation(Entity entity, ConditionalRotationSkillSelection conditionalRotationSkillSelection)
        {
            return new ConditionalRotationSelection(entity, conditionalRotationSkillSelection);
        }

        public IAISkillSelection CreateNotOnCooldownSelection(Entity entity, NextSkillNotOnCooldownSelection nextSkillNotOnCooldownSelection)
        {
            return new NotOnCooldownSelection(entity, nextSkillNotOnCooldownSelection);
        }

        private abstract class ASimpleSelection : IAISkillSelection
        {
            protected readonly Entity Entity;

            protected ASimpleSelection(Entity entity)
            {
                Entity = entity;
            }

            public virtual ulong DefaultSkill
            {
                get { return 0UL; }
            }

            public abstract ulong SelectSkill();
        }

        private sealed class SimpleRotationSelection : ASimpleSelection
        {
            private readonly RotationSkillSelection _rotation;
            private int _index;

            public SimpleRotationSelection(Entity entity, RotationSkillSelection rotation)
                : base(entity)
            {
                _rotation = rotation;
                _index = 0;
            }

            public override ulong SelectSkill()
            {
                if (_rotation == null || _rotation.Rotation == null || _rotation.Rotation.Length == 0)
                {
                    return DefaultSkill;
                }

                var skillId = (ulong)_rotation.Rotation[_index % _rotation.Rotation.Length];
                _index++;
                return skillId;
            }
        }

        private sealed class WeightedSelection : ASimpleSelection
        {
            private readonly WeightedSkillSelection _rotation;
            private readonly IRandomNumberGenerator _rng;

            public WeightedSelection(Entity entity, WeightedSkillSelection rotation, IRandomNumberGenerator rng)
                : base(entity)
            {
                _rotation = rotation;
                _rng = rng;
            }

            public override ulong SelectSkill()
            {
                if (_rotation == null || _rotation.Rotation == null || _rotation.Rotation.Length == 0)
                {
                    return DefaultSkill;
                }

                // Placeholder: simple weighted pick without allocations.
                float total = 0f;
                for (var i = 0; i < _rotation.Rotation.Length; i++)
                {
                    var w = _rotation.Rotation[i] != null ? _rotation.Rotation[i].Weight : 0f;
                    if (w > 0f)
                    {
                        total += w;
                    }
                }

                if (total <= 0.0001f)
                {
                    return (ulong)_rotation.Rotation[0].Item;
                }

                // IRandomNumberGenerator in Cliffhanger has multiple methods; we avoid depending on a specific one here.
                // Until we wire RNG, fall back to deterministic first-positive.
                for (var i = 0; i < _rotation.Rotation.Length; i++)
                {
                    var entry = _rotation.Rotation[i];
                    if (entry != null && entry.Weight > 0f)
                    {
                        return (ulong)entry.Item;
                    }
                }

                return (ulong)_rotation.Rotation[0].Item;
            }
        }

        private sealed class ConditionalRotationSelection : ASimpleSelection
        {
            private readonly ConditionalRotationSkillSelection _rotation;
            private int _index;

            public ConditionalRotationSelection(Entity entity, ConditionalRotationSkillSelection rotation)
                : base(entity)
            {
                _rotation = rotation;
                _index = 0;
            }

            public override ulong SelectSkill()
            {
                if (_rotation == null || _rotation.Rotation == null || _rotation.Rotation.Length == 0)
                {
                    return DefaultSkill;
                }

                // Placeholder: ignore conditions and return next.
                var entry = _rotation.Rotation[_index % _rotation.Rotation.Length];
                _index++;
                if (entry == null)
                {
                    return DefaultSkill;
                }

                return (ulong)entry.SkillId;
            }
        }

        private sealed class NotOnCooldownSelection : ASimpleSelection
        {
            private readonly NextSkillNotOnCooldownSelection _rotation;
            private int _index;

            public NotOnCooldownSelection(Entity entity, NextSkillNotOnCooldownSelection rotation)
                : base(entity)
            {
                _rotation = rotation;
                _index = 0;
            }

            public override ulong SelectSkill()
            {
                if (_rotation == null || _rotation.Rotation == null || _rotation.Rotation.Length == 0)
                {
                    return DefaultSkill;
                }

                // Placeholder: ignore cooldowns and iterate.
                var skill = _rotation.Rotation[_index % _rotation.Rotation.Length];
                _index++;
                return skill;
            }
        }
    }
}
