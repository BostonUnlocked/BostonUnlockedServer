using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.RandomNumbers;

namespace Shadowrun.LocalService.Core.AILogic
{
    internal static class AiSkillCatalog
    {
        public static ulong[] CollectCandidateSkills(
            Entity agent,
            IGameworldInstance gameworld,
            ISkillSelectionStrategyFactory factory,
            IRandomNumberGenerator random,
            AIBehaviourConfigurationComponent config,
            AiPlanningDiagnostics diagnostics)
        {
            var results = new List<ulong>();
            var seen = new HashSet<ulong>();

            ISkillLoadoutComponent loadout = null;
            if (gameworld != null && gameworld.EntitySystem != null && agent != null)
            {
                gameworld.EntitySystem.TryGetComponent<ISkillLoadoutComponent>(agent, out loadout);
            }

            if (diagnostics != null)
            {
                diagnostics.DebugHasLoadout = loadout != null;
                diagnostics.DebugSelectedWeaponIndex = loadout != null ? (int?)loadout.SelectedWeaponIndex : null;
                diagnostics.DebugSelectedWeaponSkillCount = loadout != null && loadout.SelectedWeapon != null && loadout.SelectedWeapon.Skills != null
                    ? (int?)loadout.SelectedWeapon.Skills.Length
                    : null;
            }

            if (config != null && config.SkillRotation != null && factory != null)
            {
                if (diagnostics != null)
                {
                    diagnostics.DebugRotationType = config.SkillRotation.GetType().FullName;
                    diagnostics.DebugRotationCount = TryGetRotationCount(config.SkillRotation);
                }

                IAISkillSelection selector = null;
                try
                {
                    selector = config.SkillRotation.CreateFor(factory, agent, random);
                }
                catch
                {
                    selector = null;
                }

                if (selector != null)
                {
                    var attempts = diagnostics != null && diagnostics.DebugRotationCount.HasValue
                        ? Math.Max(1, Math.Min(8, diagnostics.DebugRotationCount.Value))
                        : 4;

                    for (var i = 0; i < attempts; i++)
                    {
                        ulong skillId;
                        try
                        {
                            skillId = selector.SelectSkill();
                        }
                        catch
                        {
                            skillId = selector.DefaultSkill;
                        }

                        if (i == 0 && diagnostics != null)
                        {
                            diagnostics.DebugRawSelection = skillId;
                        }

                        Add(results, seen, skillId);
                    }

                    Add(results, seen, selector.DefaultSkill);
                }
            }

            AddLoadoutSkills(results, seen, loadout, true);
            AddLoadoutSkills(results, seen, loadout, false);

            return results.ToArray();
        }

        private static void AddLoadoutSkills(List<ulong> results, HashSet<ulong> seen, ISkillLoadoutComponent loadout, bool selectedWeaponOnly)
        {
            if (loadout == null || loadout.Weapons == null || loadout.Weapons.Length == 0)
            {
                return;
            }

            for (var weaponIndex = 0; weaponIndex < loadout.Weapons.Length; weaponIndex++)
            {
                if (selectedWeaponOnly && weaponIndex != loadout.SelectedWeaponIndex)
                {
                    continue;
                }

                if (!selectedWeaponOnly && weaponIndex == loadout.SelectedWeaponIndex)
                {
                    continue;
                }

                var weapon = loadout.Weapons[weaponIndex];
                if (weapon == null || weapon.Skills == null)
                {
                    continue;
                }

                for (var skillIndex = 0; skillIndex < weapon.Skills.Length; skillIndex++)
                {
                    var activity = weapon.Skills[skillIndex];
                    Add(results, seen, activity != null ? activity.Id : 0UL);
                }
            }
        }

        private static void Add(List<ulong> results, HashSet<ulong> seen, ulong skillId)
        {
            if (skillId == 0UL || seen.Contains(skillId))
            {
                return;
            }

            seen.Add(skillId);
            results.Add(skillId);
        }

        private static int? TryGetRotationCount(ISkillSelectionConfiguration rotation)
        {
            var simple = rotation as RotationSkillSelection;
            if (simple != null)
            {
                return simple.Rotation != null ? (int?)simple.Rotation.Length : null;
            }

            var weighted = rotation as WeightedSkillSelection;
            if (weighted != null)
            {
                return weighted.Rotation != null ? (int?)weighted.Rotation.Length : null;
            }

            var conditional = rotation as ConditionalRotationSkillSelection;
            if (conditional != null)
            {
                return conditional.Rotation != null ? (int?)conditional.Rotation.Length : null;
            }

            var notOnCooldown = rotation as NextSkillNotOnCooldownSelection;
            if (notOnCooldown != null)
            {
                return notOnCooldown.Rotation != null ? (int?)notOnCooldown.Rotation.Length : null;
            }

            return null;
        }
    }
}