using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
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
        private readonly IGameworldInstance _gameworld;

        public SkillSelectionStrategyFactory(IGameworldInstance gameworld)
        {
            _gameworld = gameworld;
        }

        public IAISkillSelection CreateWeightedSelection(Entity entity, WeightedSkillSelection rotation, IRandomNumberGenerator rng)
        {
            return new WeightedSelection(entity, _gameworld, rotation, rng);
        }

        public IAISkillSelection CreateSimpleRotationSelection(Entity entity, RotationSkillSelection rotation)
        {
            return new SimpleRotationSelection(entity, _gameworld, rotation);
        }

        public IAISkillSelection CreateConditionalSkillRotation(Entity entity, ConditionalRotationSkillSelection conditionalRotationSkillSelection)
        {
            return new ConditionalRotationSelection(entity, _gameworld, conditionalRotationSkillSelection);
        }

        public IAISkillSelection CreateNotOnCooldownSelection(Entity entity, NextSkillNotOnCooldownSelection nextSkillNotOnCooldownSelection)
        {
            return new NotOnCooldownSelection(entity, _gameworld, nextSkillNotOnCooldownSelection);
        }

        private abstract class ASimpleSelection : IAISkillSelection
        {
            protected readonly Entity Entity;
            protected readonly IGameworldInstance Gameworld;

            protected ASimpleSelection(Entity entity, IGameworldInstance gameworld)
            {
                Entity = entity;
                Gameworld = gameworld;
            }

            public virtual ulong DefaultSkill
            {
                get
                {
                    ISkillLoadoutComponent loadout;
                    if (!TryGetLoadout(out loadout) || loadout.SelectedWeapon == null || loadout.SelectedWeapon.Skills == null || loadout.SelectedWeapon.Skills.Length == 0)
                    {
                        return 0UL;
                    }

                    var activity = loadout.SelectedWeapon.Skills[0];
                    return activity != null ? activity.Id : 0UL;
                }
            }

            public abstract ulong SelectSkill();

            protected ulong SelectSkillByAvailabilityOrId(int selection)
            {
                ISkillLoadoutComponent loadout;
                if (!TryGetLoadout(out loadout) || loadout.SelectedWeapon == null || loadout.SelectedWeapon.Skills == null || loadout.SelectedWeapon.Skills.Length == 0)
                {
                    return DefaultSkill;
                }

                CooldownComponent cooldown;
                TryGetCooldown(out cooldown);

                if (selection >= 0 && selection < loadout.SelectedWeapon.Skills.Length)
                {
                    var indexedActivity = loadout.SelectedWeapon.Skills[selection];
                    if (indexedActivity != null && !IsOnCooldown(cooldown, indexedActivity.Id))
                    {
                        return indexedActivity.Id;
                    }
                    return DefaultSkill;
                }

                var candidateId = selection >= 0 ? (ulong)selection : 0UL;
                if (candidateId != 0UL)
                {
                    for (var i = 0; i < loadout.SelectedWeapon.Skills.Length; i++)
                    {
                        var activity = loadout.SelectedWeapon.Skills[i];
                        if (activity != null && activity.Id == candidateId && !IsOnCooldown(cooldown, candidateId))
                        {
                            return candidateId;
                        }
                    }
                }

                return DefaultSkill;
            }

            protected bool TryGetLoadout(out ISkillLoadoutComponent loadout)
            {
                loadout = null;
                return Gameworld != null
                    && Gameworld.EntitySystem != null
                    && Entity != null
                    && Gameworld.EntitySystem.TryGetComponent<ISkillLoadoutComponent>(Entity, out loadout)
                    && loadout != null;
            }

            protected bool TryGetCooldown(out CooldownComponent cooldown)
            {
                cooldown = null;
                return Gameworld != null
                    && Gameworld.EntitySystem != null
                    && Entity != null
                    && Gameworld.EntitySystem.TryGetComponent<CooldownComponent>(Entity, out cooldown)
                    && cooldown != null;
            }

            private static bool IsOnCooldown(CooldownComponent cooldown, ulong skillId)
            {
                return cooldown != null && skillId != 0UL && cooldown.IsSkillOnCooldown(skillId);
            }
        }

        private sealed class SimpleRotationSelection : ASimpleSelection
        {
            private readonly RotationSkillSelection _rotation;
            private int _index;

            public SimpleRotationSelection(Entity entity, IGameworldInstance gameworld, RotationSkillSelection rotation)
                : base(entity, gameworld)
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

                var skillId = SelectSkillByAvailabilityOrId(_rotation.Rotation[_index % _rotation.Rotation.Length]);
                _index++;
                return skillId;
            }
        }

        private sealed class WeightedSelection : ASimpleSelection
        {
            private readonly WeightedSkillSelection _rotation;
            private readonly IRandomNumberGenerator _rng;

            public WeightedSelection(Entity entity, IGameworldInstance gameworld, WeightedSkillSelection rotation, IRandomNumberGenerator rng)
                : base(entity, gameworld)
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
                    return SelectSkillByAvailabilityOrId(_rotation.Rotation[0].Item);
                }

                var bestWeight = float.MinValue;
                var bestItem = _rotation.Rotation[0].Item;
                for (var i = 0; i < _rotation.Rotation.Length; i++)
                {
                    var entry = _rotation.Rotation[i];
                    if (entry != null && entry.Weight > bestWeight)
                    {
                        bestWeight = entry.Weight;
                        bestItem = entry.Item;
                    }
                }

                return SelectSkillByAvailabilityOrId(bestItem);
            }
        }

        private sealed class ConditionalRotationSelection : ASimpleSelection
        {
            private readonly ConditionalRotationSkillSelection _rotation;
            private int _index;

            public ConditionalRotationSelection(Entity entity, IGameworldInstance gameworld, ConditionalRotationSkillSelection rotation)
                : base(entity, gameworld)
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

                return SelectSkillByAvailabilityOrId(entry.SkillId);
            }
        }

        private sealed class NotOnCooldownSelection : ASimpleSelection
        {
            private readonly NextSkillNotOnCooldownSelection _rotation;
            private int _index;

            public NotOnCooldownSelection(Entity entity, IGameworldInstance gameworld, NextSkillNotOnCooldownSelection rotation)
                : base(entity, gameworld)
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
                var skill = SelectSkillByAvailabilityOrId((int)_rotation.Rotation[_index % _rotation.Rotation.Length]);
                _index++;
                return skill;
            }
        }

    }
}
