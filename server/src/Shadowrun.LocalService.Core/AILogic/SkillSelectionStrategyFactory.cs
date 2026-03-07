using System;
using System.Collections.Generic;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.GameLogic;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.RandomNumbers;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Creates runtime skill-selection strategies from serialized rotation config objects.
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
            private readonly List<IWeightedItem<int>> _weightedItems;

            public WeightedSelection(Entity entity, IGameworldInstance gameworld, WeightedSkillSelection rotation, IRandomNumberGenerator rng)
                : base(entity, gameworld)
            {
                _rotation = rotation;
                _rng = rng;
                _weightedItems = BuildWeightedItems(rotation);
            }

            public override ulong SelectSkill()
            {
                if (_weightedItems == null || _weightedItems.Count == 0 || _rng == null)
                {
                    return DefaultSkill;
                }

                return SelectSkillByAvailabilityOrId(_rng.SelectRandomItemFromWeightedList(_weightedItems));
            }

            private static List<IWeightedItem<int>> BuildWeightedItems(WeightedSkillSelection rotation)
            {
                var weightedItems = new List<IWeightedItem<int>>();
                if (rotation == null || rotation.Rotation == null)
                {
                    return weightedItems;
                }

                for (var i = 0; i < rotation.Rotation.Length; i++)
                {
                    var entry = rotation.Rotation[i];
                    if (entry == null || entry.Weight <= 0f)
                    {
                        continue;
                    }

                    weightedItems.Add(new WeightedSkillItem(entry.Item, entry.Weight));
                }

                return weightedItems;
            }
        }

        private sealed class ConditionalRotationSelection : ASimpleSelection
        {
            private readonly ConditionalRotationSkillSelection _rotation;
            private readonly HashSet<int> _selectedRunOnceSkills;
            private ConditionalSkill _lastSkill;

            public ConditionalRotationSelection(Entity entity, IGameworldInstance gameworld, ConditionalRotationSkillSelection rotation)
                : base(entity, gameworld)
            {
                _rotation = rotation;
                _selectedRunOnceSkills = new HashSet<int>();
            }

            public override ulong SelectSkill()
            {
                if (_rotation == null || _rotation.Rotation == null || _rotation.Rotation.Length == 0)
                {
                    return DefaultSkill;
                }

                if (Gameworld == null || Gameworld.EntitySystem == null || Entity == null)
                {
                    return DefaultSkill;
                }

                var activityParameters = new ActivityParametersBuilder()
                    .WithEntitySystem(Gameworld.EntitySystem)
                    .WithGameworldInstance(Gameworld)
                    .WithSource(Entity)
                    .Build();

                var filteredSkills = _rotation.Rotation.Where(skill => ApplyFilter(skill, activityParameters)).ToList();
                if (filteredSkills.Count == 0)
                {
                    return DefaultSkill;
                }

                var nextIndex = filteredSkills.IndexOf(_lastSkill) + 1;
                if (nextIndex >= filteredSkills.Count)
                {
                    nextIndex = 0;
                }

                var selectedSkill = filteredSkills[nextIndex];
                if (selectedSkill == null)
                {
                    return DefaultSkill;
                }

                if (selectedSkill.RunOnce)
                {
                    _selectedRunOnceSkills.Add(selectedSkill.SkillId);
                }

                _lastSkill = selectedSkill;
                return SelectSkillByAvailabilityOrId(selectedSkill.SkillId);
            }

            private bool ApplyFilter(ConditionalSkill skill, ActivityParameters activityParameters)
            {
                if (skill == null || skill.Condition == null)
                {
                    return false;
                }

                bool fulfilled;
                try
                {
                    fulfilled = skill.Condition.IsFulfilledBy(activityParameters);
                }
                catch
                {
                    return false;
                }

                if (!skill.RunOnce)
                {
                    return fulfilled;
                }

                return fulfilled && !_selectedRunOnceSkills.Contains(skill.SkillId);
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
                _index = -1;
            }

            public override ulong SelectSkill()
            {
                if (_rotation == null || _rotation.Rotation == null || _rotation.Rotation.Length == 0)
                {
                    return DefaultSkill;
                }

                CooldownComponent cooldown;
                TryGetCooldown(out cooldown);

                var firstIndex = _index;
                do
                {
                    _index = (_index + 1) % _rotation.Rotation.Length;
                    var skillId = _rotation.Rotation[_index];
                    if (cooldown == null || !cooldown.IsSkillOnCooldown(skillId))
                    {
                        return skillId;
                    }
                }
                while (_index != firstIndex);

                return DefaultSkill;
            }
        }

        private sealed class WeightedSkillItem : IWeightedItem<int>
        {
            public WeightedSkillItem(int item, float weight)
            {
                Item = item;
                Weight = weight;
            }

            public int Item { get; private set; }
            public float Weight { get; private set; }
        }

    }
}
