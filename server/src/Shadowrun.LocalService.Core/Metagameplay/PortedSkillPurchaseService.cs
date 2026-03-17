using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class SkillPurchaseApplicationResult
    {
        public bool Persisted;
        public bool ApplyReset;
        public int AppliedCount;
        public int KarmaBefore;
        public int KarmaAfter;
        public int KarmaRefunded;
        public int KarmaSpent;
        public SkillTreeChanges AppliedChanges;

        public bool ShouldNotifyClient
        {
            get
            {
                return AppliedChanges != null
                    && (AppliedChanges.ApplyReset || (AppliedChanges.Purchases != null && AppliedChanges.Purchases.Length > 0));
            }
        }
    }

    internal sealed class PortedSkillPurchaseService
    {
        private readonly LocalServiceOptions _options;

        public PortedSkillPurchaseService(LocalServiceOptions options)
        {
            _options = options;
        }

        public SkillPurchaseApplicationResult Apply(CareerSlot slot, SkillTreeChanges requestedChanges)
        {
            var result = new SkillPurchaseApplicationResult();
            result.AppliedChanges = new SkillPurchaseBuilder().Build();

            if (slot == null || requestedChanges == null)
            {
                return result;
            }

            if (slot.SkillTreeDefinitions == null)
            {
                slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
            }

            var index = MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null);
            var builder = new SkillPurchaseBuilder();
            var changed = false;
            result.KarmaBefore = slot.Karma;
            result.KarmaAfter = slot.Karma;

            if (requestedChanges.ApplyReset)
            {
                if (slot.SkillTreeDefinitions.Count > 0)
                {
                    slot.SkillTreeDefinitions.Clear();
                    changed = true;
                }

                var refunded = slot.SpentKarma;
                if (refunded > 0)
                {
                    try
                    {
                        checked
                        {
                            slot.Karma = slot.Karma + refunded;
                        }
                    }
                    catch
                    {
                        slot.Karma = int.MaxValue;
                    }
                }
                slot.SpentKarma = 0;
                result.KarmaRefunded = refunded > 0 ? refunded : 0;
                builder.WithReset();
                AddInitialSkills(slot, index, builder);
                changed = true;
                result.ApplyReset = true;
            }

            var purchases = requestedChanges.Purchases;
            if (purchases != null)
            {
                for (var i = 0; i < purchases.Length; i++)
                {
                    var purchase = purchases[i];
                    if (purchase == null)
                    {
                        continue;
                    }

                    MetagameplayStaticDataIndex.SkillTreeInfo skillTree;
                    MetagameplayStaticDataIndex.SkillInfo skill;
                    int levelIndex;
                    if (!index.TryGetSkill(purchase.SkillTreeTechnichalName, purchase.SkillTechnichalName, out skillTree, out levelIndex, out skill))
                    {
                        continue;
                    }

                    string[] ownedSkills;
                    if (!slot.SkillTreeDefinitions.TryGetValue(purchase.SkillTreeTechnichalName, out ownedSkills) || ownedSkills == null)
                    {
                        ownedSkills = new string[0];
                        slot.SkillTreeDefinitions[purchase.SkillTreeTechnichalName] = ownedSkills;
                    }

                    if (!AllLowerSkillsBought(ownedSkills, levelIndex))
                    {
                        continue;
                    }

                    if (!SkillLevelIsNullOrEmpty(ownedSkills, levelIndex))
                    {
                        continue;
                    }

                    if (skill == null || skill.KarmaCost < 0 || slot.Karma < skill.KarmaCost)
                    {
                        continue;
                    }

                    var updated = ApplySkillAtLevel(ownedSkills, levelIndex, purchase.SkillTechnichalName);
                    slot.SkillTreeDefinitions[purchase.SkillTreeTechnichalName] = updated;
                    slot.Karma = slot.Karma - skill.KarmaCost;
                    try
                    {
                        checked
                        {
                            slot.SpentKarma = slot.SpentKarma + skill.KarmaCost;
                        }
                    }
                    catch
                    {
                        slot.SpentKarma = int.MaxValue;
                    }

                    builder.WithSkillChange(purchase.SkillTreeTechnichalName, purchase.SkillTechnichalName, levelIndex + 1);
                    result.AppliedCount++;
                    result.KarmaSpent += skill.KarmaCost;
                    changed = true;
                }
            }

            result.Persisted = changed;
            result.KarmaAfter = slot.Karma;
            result.AppliedChanges = builder.Build();
            return result;
        }

        private static void AddInitialSkills(CareerSlot slot, MetagameplayStaticDataIndex index, SkillPurchaseBuilder builder)
        {
            if (slot == null || index == null || index.InitialSkills == null)
            {
                return;
            }

            foreach (var entry in index.InitialSkills)
            {
                if (entry.Value == null || entry.Value.Count == 0)
                {
                    continue;
                }

                var firstSkill = entry.Value[0];
                if (string.IsNullOrEmpty(firstSkill))
                {
                    continue;
                }

                slot.SkillTreeDefinitions[entry.Key] = new string[] { firstSkill };
                builder.WithSkillChange(entry.Key, firstSkill, 1);
            }
        }

        private static bool SkillLevelIsNullOrEmpty(string[] ownedSkills, int levelIndex)
        {
            if (ownedSkills == null)
            {
                return true;
            }

            if (ownedSkills.Length > levelIndex && ownedSkills[levelIndex] != null)
            {
                return ownedSkills[levelIndex] == string.Empty;
            }

            return true;
        }

        private static bool AllLowerSkillsBought(string[] ownedSkills, int levelIndex)
        {
            var count = ownedSkills != null ? ownedSkills.Length : 0;
            return count >= levelIndex;
        }

        private static string[] ApplySkillAtLevel(string[] ownedSkills, int levelIndex, string skillTechnicalName)
        {
            if (ownedSkills == null)
            {
                ownedSkills = new string[0];
            }

            if (ownedSkills.Length == levelIndex)
            {
                var appended = new string[ownedSkills.Length + 1];
                for (var i = 0; i < ownedSkills.Length; i++)
                {
                    appended[i] = ownedSkills[i];
                }
                appended[levelIndex] = skillTechnicalName;
                return appended;
            }

            var updated = new string[ownedSkills.Length];
            for (var j = 0; j < ownedSkills.Length; j++)
            {
                updated[j] = ownedSkills[j];
            }
            updated[levelIndex] = skillTechnicalName;
            return updated;
        }
    }
}
