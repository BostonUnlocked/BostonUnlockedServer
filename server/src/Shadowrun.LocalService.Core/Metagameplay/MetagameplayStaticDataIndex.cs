using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal sealed class MetagameplayStaticDataIndex
    {
        internal sealed class SkillInfo
        {
            public string TechnicalName;
            public int KarmaCost;
        }

        internal sealed class SkillLevelInfo
        {
            public List<SkillInfo> Skills = new List<SkillInfo>();
        }

        internal sealed class SkillTreeInfo
        {
            public string TechnicalName;
            public List<SkillLevelInfo> SkillLevels = new List<SkillLevelInfo>();
        }

        internal sealed class ItemDefinitionInfo
        {
            public string ItemId;
            public int MaxStacksize;
            public int SellPrice;
        }

        internal sealed class ShopEntryInfo
        {
            public string ItemId;
            public int Price;
            public MetagameplayAvailabilityCondition Condition;
        }

        internal sealed class StorylineInfo
        {
            public string TechnicalName;
            public List<ChapterInfo> Chapters = new List<ChapterInfo>();
        }

        internal sealed class ChapterInfo
        {
            public int Index;
            public string TechnicalName;
            public string Hub;
            public List<string> RequiredMissions = new List<string>();
            public List<string> RequiredUnlocks = new List<string>();
            public List<string> DialogNpcIds = new List<string>();
        }

        private static readonly object CacheLock = new object();
        private static string _cachedStaticDataDir;
        private static DateTime _cachedMetagameplayLastWriteUtc;
        private static DateTime _cachedGlobalsLastWriteUtc;
        private static MetagameplayStaticDataIndex _cachedIndex;
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        public readonly Dictionary<string, SkillTreeInfo> SkillTrees;
        public readonly Dictionary<string, List<string>> InitialSkills;
        public readonly Dictionary<string, Dictionary<string, ShopEntryInfo>> ShopEntries;
        public readonly Dictionary<string, ItemDefinitionInfo> ItemDefinitions;
        public readonly Dictionary<string, ulong> Bodytypes;
        public readonly Dictionary<string, StorylineInfo> Storylines;
        public readonly Dictionary<string, int> MissionCurrencyRewards;
        public readonly Dictionary<string, ItemChange[]> MissionItemChanges;
        public readonly Dictionary<string, string[]> MissionRewardUnlocks;
        public readonly Dictionary<string, string[]> MissionUnlockDeactivations;

        private MetagameplayStaticDataIndex()
        {
            SkillTrees = new Dictionary<string, SkillTreeInfo>(StringComparer.Ordinal);
            InitialSkills = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            ShopEntries = new Dictionary<string, Dictionary<string, ShopEntryInfo>>(StringComparer.OrdinalIgnoreCase);
            ItemDefinitions = new Dictionary<string, ItemDefinitionInfo>(StringComparer.OrdinalIgnoreCase);
            Bodytypes = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            Storylines = new Dictionary<string, StorylineInfo>(StringComparer.OrdinalIgnoreCase);
            MissionCurrencyRewards = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            MissionItemChanges = new Dictionary<string, ItemChange[]>(StringComparer.OrdinalIgnoreCase);
            MissionRewardUnlocks = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            MissionUnlockDeactivations = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }

        public static MetagameplayStaticDataIndex Load(string staticDataDir)
        {
            var metagameplayPath = !string.IsNullOrEmpty(staticDataDir)
                ? Path.Combine(staticDataDir, "metagameplay.json")
                : null;
            var globalsPath = !string.IsNullOrEmpty(staticDataDir)
                ? Path.Combine(staticDataDir, "globals.json")
                : null;

            var hasMetagameplay = !string.IsNullOrEmpty(metagameplayPath) && File.Exists(metagameplayPath);
            var hasGlobals = !string.IsNullOrEmpty(globalsPath) && File.Exists(globalsPath);
            if (!hasMetagameplay && !hasGlobals)
            {
                return new MetagameplayStaticDataIndex();
            }

            var metagameplayLastWriteUtc = hasMetagameplay ? File.GetLastWriteTimeUtc(metagameplayPath) : default(DateTime);
            var globalsLastWriteUtc = hasGlobals ? File.GetLastWriteTimeUtc(globalsPath) : default(DateTime);
            lock (CacheLock)
            {
                if (_cachedIndex != null
                    && string.Equals(_cachedStaticDataDir, staticDataDir, StringComparison.OrdinalIgnoreCase)
                    && _cachedMetagameplayLastWriteUtc == metagameplayLastWriteUtc
                    && _cachedGlobalsLastWriteUtc == globalsLastWriteUtc)
                {
                    return _cachedIndex;
                }

                _cachedIndex = LoadFromPaths(metagameplayPath, globalsPath);
                _cachedStaticDataDir = staticDataDir;
                _cachedMetagameplayLastWriteUtc = metagameplayLastWriteUtc;
                _cachedGlobalsLastWriteUtc = globalsLastWriteUtc;
                return _cachedIndex;
            }
        }

        public bool TryGetSkill(string skillTreeTechnicalName, string skillTechnicalName, out SkillTreeInfo skillTree, out int levelIndex, out SkillInfo skill)
        {
            skillTree = null;
            levelIndex = -1;
            skill = null;

            if (string.IsNullOrEmpty(skillTreeTechnicalName) || string.IsNullOrEmpty(skillTechnicalName))
            {
                return false;
            }

            if (!SkillTrees.TryGetValue(skillTreeTechnicalName, out skillTree) || skillTree == null || skillTree.SkillLevels == null)
            {
                return false;
            }

            for (var i = 0; i < skillTree.SkillLevels.Count; i++)
            {
                var level = skillTree.SkillLevels[i];
                if (level == null || level.Skills == null)
                {
                    continue;
                }

                for (var j = 0; j < level.Skills.Count; j++)
                {
                    var candidate = level.Skills[j];
                    if (candidate != null && string.Equals(candidate.TechnicalName, skillTechnicalName, StringComparison.Ordinal))
                    {
                        levelIndex = i;
                        skill = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        public bool TryGetShopPrice(string shopKeeper, string itemId, out int price)
        {
            price = 0;
            if (string.IsNullOrEmpty(shopKeeper) || string.IsNullOrEmpty(itemId))
            {
                return false;
            }

            ShopEntryInfo entry;
            if (!TryGetShopEntry(shopKeeper, itemId, out entry) || entry == null)
            {
                return false;
            }

            price = entry.Price;
            return true;
        }

        public bool TryGetShopEntry(string shopKeeper, string itemId, out ShopEntryInfo entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(shopKeeper) || string.IsNullOrEmpty(itemId))
            {
                return false;
            }

            Dictionary<string, ShopEntryInfo> shop;
            if (!ShopEntries.TryGetValue(shopKeeper, out shop) || shop == null)
            {
                return false;
            }

            return shop.TryGetValue(itemId, out entry) && entry != null;
        }

        public bool TryGetItemDefinition(string itemId, out ItemDefinitionInfo itemDefinition)
        {
            itemDefinition = null;
            if (string.IsNullOrEmpty(itemId))
            {
                return false;
            }

            return ItemDefinitions.TryGetValue(itemId, out itemDefinition) && itemDefinition != null;
        }

        public bool TryGetBodytypeId(ulong metatypeId, ulong genderId, out ulong bodytypeId)
        {
            bodytypeId = 0UL;
            if (metatypeId == 0UL || genderId == 0UL)
            {
                return false;
            }

            return Bodytypes.TryGetValue(BuildBodytypeKey(metatypeId, genderId), out bodytypeId) && bodytypeId != 0UL;
        }

        public bool TryGetMissionCurrencyReward(string rewardSection, string missionName, string missionOutcome, string currencyId, out int earnedValue)
        {
            earnedValue = 0;
            if (string.IsNullOrEmpty(rewardSection) || string.IsNullOrEmpty(missionName) || string.IsNullOrEmpty(missionOutcome) || string.IsNullOrEmpty(currencyId))
            {
                return false;
            }

            return MissionCurrencyRewards.TryGetValue(BuildMissionCurrencyRewardKey(rewardSection, missionName, missionOutcome, currencyId), out earnedValue);
        }

        public bool TryGetMissionItemChanges(string rewardSection, string missionName, string missionOutcome, out ItemChange[] itemChanges)
        {
            itemChanges = null;
            if (string.IsNullOrEmpty(rewardSection) || string.IsNullOrEmpty(missionName) || string.IsNullOrEmpty(missionOutcome))
            {
                return false;
            }

            return MissionItemChanges.TryGetValue(BuildMissionRewardKey(rewardSection, missionName, missionOutcome), out itemChanges)
                && itemChanges != null
                && itemChanges.Length > 0;
        }

        public bool TryGetMissionRewardUnlocks(string rewardSection, string missionName, string missionOutcome, out string[] unlocks)
        {
            unlocks = null;
            if (string.IsNullOrEmpty(rewardSection) || string.IsNullOrEmpty(missionName) || string.IsNullOrEmpty(missionOutcome))
            {
                return false;
            }

            return MissionRewardUnlocks.TryGetValue(BuildMissionRewardKey(rewardSection, missionName, missionOutcome), out unlocks)
                && unlocks != null
                && unlocks.Length > 0;
        }

        public bool TryGetMissionUnlockDeactivationsOnVictory(string missionName, out string[] unlocks)
        {
            unlocks = null;
            if (string.IsNullOrEmpty(missionName))
            {
                return false;
            }

            return MissionUnlockDeactivations.TryGetValue(missionName, out unlocks)
                && unlocks != null
                && unlocks.Length > 0;
        }

        public bool TryGetStoryline(string storylineTechnicalName, out StorylineInfo storyline)
        {
            storyline = null;
            if (string.IsNullOrEmpty(storylineTechnicalName))
            {
                return false;
            }

            return Storylines.TryGetValue(storylineTechnicalName, out storyline) && storyline != null;
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        private static MetagameplayStaticDataIndex LoadFromPaths(string metagameplayPath, string globalsPath)
        {
            var result = new MetagameplayStaticDataIndex();

            try
            {
                if (!string.IsNullOrEmpty(metagameplayPath) && File.Exists(metagameplayPath))
                {
                    ParseMetagameplayFile(metagameplayPath, result);
                }

                if (!string.IsNullOrEmpty(globalsPath) && File.Exists(globalsPath))
                {
                    ParseGlobalsFile(globalsPath, result);
                }
            }
            catch
            {
            }

            return result;
        }

        private static void ParseMetagameplayFile(string path, MetagameplayStaticDataIndex result)
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            ParseItemDefinitionsFromJson(json, result);

            var root = Json.DeserializeObject(json);
            var components = TryGetObjectArray(root as IDictionary, "Components");
            if (components == null)
            {
                components = ToObjectArray(root);
            }
            if (components == null)
            {
                return;
            }

            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i] as IDictionary;
                if (component == null)
                {
                    continue;
                }

                var typeName = GetString(component, "TypeName");
                if (!string.IsNullOrEmpty(typeName) && typeName.IndexOf("SkillTreeData", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ParseSkillTreeData(component, result);
                }

                if (!string.IsNullOrEmpty(typeName) && typeName.IndexOf("ShopListData", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ParseShopListData(component, result);
                }

                ParseBodytypeData(component, result);
                ParseStorylineData(component, result);
                ParseNestedItemDefinitions(component, result);
            }
        }

        private static void ParseItemDefinitionsFromJson(string json, MetagameplayStaticDataIndex result)
        {
            if (string.IsNullOrEmpty(json) || result == null)
            {
                return;
            }

            var itemRegex = new Regex(
                "\\\"TypeName\\\"\\s*:\\s*\\\"Cliffhanger\\.SRO\\.ServerClientCommons\\.Definitions\\.[^\\\"]*ItemDefinition, Cliffhanger\\.SRO\\.ServerClientCommons\\\"" +
                ".*?\\\"Id\\\"\\s*:\\s*\\\"(?<id>[^\\\"]+)\\\"" +
                ".*?\\\"MaxStacksize\\\"\\s*:\\s*(?<max>-?\\d+)" +
                ".*?\\\"SellPrice\\\"\\s*:\\s*(?<sell>-?\\d+)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            var matches = itemRegex.Matches(json);
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                if (match == null)
                {
                    continue;
                }

                var itemId = match.Groups["id"] != null ? match.Groups["id"].Value : null;
                if (string.IsNullOrEmpty(itemId) || result.ItemDefinitions.ContainsKey(itemId))
                {
                    continue;
                }

                int maxStacksize;
                if (!int.TryParse(match.Groups["max"] != null ? match.Groups["max"].Value : null, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxStacksize)
                    || maxStacksize <= 0)
                {
                    maxStacksize = 1;
                }

                int sellPrice;
                if (!int.TryParse(match.Groups["sell"] != null ? match.Groups["sell"].Value : null, NumberStyles.Integer, CultureInfo.InvariantCulture, out sellPrice))
                {
                    sellPrice = 0;
                }

                result.ItemDefinitions[itemId] = new ItemDefinitionInfo
                {
                    ItemId = itemId,
                    MaxStacksize = maxStacksize,
                    SellPrice = sellPrice,
                };
            }
        }

        private static void ParseNestedItemDefinitions(object node, MetagameplayStaticDataIndex result)
        {
            if (node == null || result == null)
            {
                return;
            }

            var dict = node as IDictionary;
            if (dict != null)
            {
                ParseItemDefinition(dict, result);

                foreach (DictionaryEntry entry in dict)
                {
                    ParseNestedItemDefinitions(entry.Value, result);
                }

                return;
            }

            var array = ToObjectArray(node);
            if (array == null)
            {
                return;
            }

            for (var i = 0; i < array.Length; i++)
            {
                ParseNestedItemDefinitions(array[i], result);
            }
        }

        private static void ParseSkillTreeData(IDictionary component, MetagameplayStaticDataIndex result)
        {
            var definitions = TryGetObjectArray(component, "SkillTreeDefinitions");
            if (definitions == null)
            {
                return;
            }

            for (var i = 0; i < definitions.Length; i++)
            {
                var definition = definitions[i] as IDictionary;
                if (definition == null)
                {
                    continue;
                }

                var technicalName = GetString(definition, "TechnicalName");
                if (string.IsNullOrEmpty(technicalName))
                {
                    continue;
                }

                var tree = new SkillTreeInfo();
                tree.TechnicalName = technicalName;

                var levels = TryGetObjectArray(definition, "SkillLevels");
                if (levels != null)
                {
                    for (var levelIndex = 0; levelIndex < levels.Length; levelIndex++)
                    {
                        var levelDict = levels[levelIndex] as IDictionary;
                        if (levelDict == null)
                        {
                            continue;
                        }

                        var level = new SkillLevelInfo();
                        var skills = TryGetObjectArray(levelDict, "SerializedSkills");
                        if (skills != null)
                        {
                            for (var skillIndex = 0; skillIndex < skills.Length; skillIndex++)
                            {
                                var skillDict = skills[skillIndex] as IDictionary;
                                if (skillDict == null)
                                {
                                    continue;
                                }

                                var skillTechnicalName = GetString(skillDict, "TechnicalName");
                                if (string.IsNullOrEmpty(skillTechnicalName))
                                {
                                    continue;
                                }

                                var skill = new SkillInfo();
                                skill.TechnicalName = skillTechnicalName;
                                skill.KarmaCost = GetInt32(skillDict, "KarmaCost", 0);
                                level.Skills.Add(skill);

                                if (skill.KarmaCost == 0)
                                {
                                    List<string> initialSkills;
                                    if (!result.InitialSkills.TryGetValue(technicalName, out initialSkills) || initialSkills == null)
                                    {
                                        initialSkills = new List<string>();
                                        result.InitialSkills[technicalName] = initialSkills;
                                    }
                                    if (!initialSkills.Contains(skillTechnicalName))
                                    {
                                        initialSkills.Add(skillTechnicalName);
                                    }
                                }
                            }
                        }

                        tree.SkillLevels.Add(level);
                    }
                }

                result.SkillTrees[technicalName] = tree;
            }
        }

        private static void ParseShopListData(IDictionary component, MetagameplayStaticDataIndex result)
        {
            var shopDefinitions = TryGetObjectArray(component, "ShopListDefinitions");
            if (shopDefinitions == null)
            {
                return;
            }

            for (var i = 0; i < shopDefinitions.Length; i++)
            {
                var shopDefinition = shopDefinitions[i] as IDictionary;
                if (shopDefinition == null)
                {
                    continue;
                }

                var internalName = GetString(shopDefinition, "InternalName");
                if (string.IsNullOrEmpty(internalName))
                {
                    continue;
                }

                Dictionary<string, ShopEntryInfo> shopEntries;
                if (!result.ShopEntries.TryGetValue(internalName, out shopEntries) || shopEntries == null)
                {
                    shopEntries = new Dictionary<string, ShopEntryInfo>(StringComparer.OrdinalIgnoreCase);
                    result.ShopEntries[internalName] = shopEntries;
                }

                var entries = TryGetObjectArray(shopDefinition, "ShopListEntries");
                if (entries == null)
                {
                    continue;
                }

                for (var j = 0; j < entries.Length; j++)
                {
                    var entry = entries[j] as IDictionary;
                    if (entry == null)
                    {
                        continue;
                    }

                    var itemId = GetString(entry, "ItemId");
                    if (string.IsNullOrEmpty(itemId))
                    {
                        continue;
                    }

                    var shopEntry = new ShopEntryInfo();
                    shopEntry.ItemId = itemId;
                    shopEntry.Price = GetInt32(entry, "Price", 0);
                    shopEntry.Condition = ParseAvailabilityCondition(entry.Contains("Condition") ? entry["Condition"] as IDictionary : null);
                    shopEntries[itemId] = shopEntry;
                }
            }
        }

        private static MetagameplayAvailabilityCondition ParseAvailabilityCondition(IDictionary dict)
        {
            if (dict == null)
            {
                return null;
            }

            var condition = new MetagameplayAvailabilityCondition();
            condition.TypeName = GetString(dict, "TypeName");
            condition.StorylineReference = GetString(dict, "StorylineReference");
            condition.ChapterIndex = GetInt32(dict, "ChapterIndex", 0);
            condition.From = GetDateTime(dict, "From");
            condition.To = GetDateTime(dict, "To");

            var unlocks = ToObjectArray(dict.Contains("Unlocks") ? dict["Unlocks"] : null);
            if (unlocks != null && unlocks.Length > 0)
            {
                condition.Unlocks = new List<string>();
                for (var i = 0; i < unlocks.Length; i++)
                {
                    var unlock = unlocks[i] as string;
                    if (!string.IsNullOrEmpty(unlock))
                    {
                        condition.Unlocks.Add(unlock);
                    }
                }
            }

            var innerConditions = ToObjectArray(dict.Contains("InnerConditions") ? dict["InnerConditions"] : null);
            if (innerConditions != null && innerConditions.Length > 0)
            {
                condition.InnerConditions = new List<MetagameplayAvailabilityCondition>();
                for (var i = 0; i < innerConditions.Length; i++)
                {
                    var parsedInner = ParseAvailabilityCondition(innerConditions[i] as IDictionary);
                    if (parsedInner != null)
                    {
                        condition.InnerConditions.Add(parsedInner);
                    }
                }
            }

            if (dict.Contains("InnerCondition") && dict["InnerCondition"] is IDictionary)
            {
                condition.InnerCondition = ParseAvailabilityCondition(dict["InnerCondition"] as IDictionary);
            }

            return condition;
        }

        private static void ParseItemDefinition(IDictionary component, MetagameplayStaticDataIndex result)
        {
            var itemId = GetString(component, "Id");
            if (string.IsNullOrEmpty(itemId))
            {
                return;
            }

            var hasSellPrice = component.Contains("SellPrice");
            var hasMaxStacksize = component.Contains("MaxStacksize");
            if (!hasSellPrice && !hasMaxStacksize)
            {
                return;
            }

            if (result.ItemDefinitions.ContainsKey(itemId))
            {
                return;
            }

            var itemDefinition = new ItemDefinitionInfo();
            itemDefinition.ItemId = itemId;
            itemDefinition.MaxStacksize = GetInt32(component, "MaxStacksize", 1);
            if (itemDefinition.MaxStacksize <= 0)
            {
                itemDefinition.MaxStacksize = 1;
            }
            itemDefinition.SellPrice = GetInt32(component, "SellPrice", 0);
            result.ItemDefinitions[itemId] = itemDefinition;
        }

        private static void ParseBodytypeData(IDictionary component, MetagameplayStaticDataIndex result)
        {
            var definitions = TryGetObjectArray(component, "BodytypeDefinitions");
            if (definitions == null)
            {
                return;
            }

            for (var i = 0; i < definitions.Length; i++)
            {
                var definition = definitions[i] as IDictionary;
                if (definition == null)
                {
                    continue;
                }

                var id = GetUInt64(definition, "Id", 0UL);
                var metatype = GetUInt64(definition, "MetatypeId", 0UL);
                var gender = GetUInt64(definition, "GenderId", 0UL);
                if (id == 0UL || metatype == 0UL || gender == 0UL)
                {
                    continue;
                }

                result.Bodytypes[BuildBodytypeKey(metatype, gender)] = id;
            }
        }

        private static void ParseStorylineData(IDictionary component, MetagameplayStaticDataIndex result)
        {
            var storylines = TryGetObjectArray(component, "Storylines");
            if (storylines == null)
            {
                return;
            }

            for (var i = 0; i < storylines.Length; i++)
            {
                var storylineDict = storylines[i] as IDictionary;
                if (storylineDict == null)
                {
                    continue;
                }

                var technicalName = GetString(storylineDict, "TechnicalName");
                if (string.IsNullOrEmpty(technicalName))
                {
                    continue;
                }

                var info = new StorylineInfo();
                info.TechnicalName = technicalName;

                var chapters = TryGetObjectArray(storylineDict, "Chapters");
                if (chapters != null)
                {
                    for (var chapterIndex = 0; chapterIndex < chapters.Length; chapterIndex++)
                    {
                        var chapterDict = chapters[chapterIndex] as IDictionary;
                        if (chapterDict == null)
                        {
                            continue;
                        }

                        var chapter = new ChapterInfo();
                        chapter.Index = chapterIndex;
                        chapter.TechnicalName = GetString(chapterDict, "TechnicalName");
                        chapter.Hub = GetString(chapterDict, "Hub");

                        var requiredUnlocks = TryGetObjectArray(chapterDict, "RequiredUnlocksForNextChapter");
                        if (requiredUnlocks != null)
                        {
                            for (var unlockIndex = 0; unlockIndex < requiredUnlocks.Length; unlockIndex++)
                            {
                                var unlockName = requiredUnlocks[unlockIndex] as string;
                                if (!string.IsNullOrEmpty(unlockName))
                                {
                                    chapter.RequiredUnlocks.Add(unlockName);
                                }
                            }
                        }

                        var requiredMissions = TryGetObjectArray(chapterDict, "RequiredMissionsForNextChapter");
                        if (requiredMissions != null)
                        {
                            for (var missionIndex = 0; missionIndex < requiredMissions.Length; missionIndex++)
                            {
                                var missionRef = requiredMissions[missionIndex] as IDictionary;
                                if (missionRef == null)
                                {
                                    continue;
                                }

                                var missionName = GetString(missionRef, "Mission");
                                if (!string.IsNullOrEmpty(missionName))
                                {
                                    chapter.RequiredMissions.Add(missionName);
                                }
                            }
                        }

                        var dialogs = TryGetObjectArray(chapterDict, "DialogsForChapter");
                        if (dialogs != null)
                        {
                            for (var dialogIndex = 0; dialogIndex < dialogs.Length; dialogIndex++)
                            {
                                var dialogDef = dialogs[dialogIndex] as IDictionary;
                                if (dialogDef == null)
                                {
                                    continue;
                                }

                                var npcId = GetString(dialogDef, "Id");
                                if (!string.IsNullOrEmpty(npcId) && !chapter.DialogNpcIds.Contains(npcId))
                                {
                                    chapter.DialogNpcIds.Add(npcId);
                                }
                            }
                        }

                        info.Chapters.Add(chapter);
                    }
                }

                result.Storylines[technicalName] = info;
            }
        }

        private static void ParseGlobalsFile(string path, MetagameplayStaticDataIndex result)
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            var root = Json.DeserializeObject(json);
            var components = TryGetObjectArray(root as IDictionary, "Components");
            if (components == null)
            {
                return;
            }

            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i] as IDictionary;
                if (component == null)
                {
                    continue;
                }

                var missions = TryGetObjectArray(component, "MissionDefinitions");
                if (missions == null)
                {
                    continue;
                }

                for (var missionIndex = 0; missionIndex < missions.Length; missionIndex++)
                {
                    var mission = missions[missionIndex] as IDictionary;
                    if (mission == null)
                    {
                        continue;
                    }

                    var missionName = GetString(mission, "Name");
                    if (string.IsNullOrEmpty(missionName))
                    {
                        continue;
                    }

                    var rewards = mission.Contains("Rewards") ? mission["Rewards"] as IDictionary : null;
                    if (rewards != null)
                    {
                        TryAddMissionCurrencyRewards(result.MissionCurrencyRewards, "Rewards", missionName, rewards);
                        TryAddMissionItemChanges(result.MissionItemChanges, "Rewards", missionName, rewards);
                        TryAddMissionRewardUnlocks(result.MissionRewardUnlocks, "Rewards", missionName, rewards);
                    }

                    var storyRewards = mission.Contains("StoryRewards") ? mission["StoryRewards"] as IDictionary : null;
                    if (storyRewards != null)
                    {
                        TryAddMissionCurrencyRewards(result.MissionCurrencyRewards, "StoryRewards", missionName, storyRewards);
                        TryAddMissionItemChanges(result.MissionItemChanges, "StoryRewards", missionName, storyRewards);
                        TryAddMissionRewardUnlocks(result.MissionRewardUnlocks, "StoryRewards", missionName, storyRewards);
                    }

                    TryAddMissionUnlockDeactivations(result.MissionUnlockDeactivations, missionName, mission);
                }
            }
        }

        private static void TryAddMissionCurrencyRewards(Dictionary<string, int> map, string rewardSection, string missionName, IDictionary rewardDef)
        {
            if (map == null || string.IsNullOrEmpty(rewardSection) || string.IsNullOrEmpty(missionName) || rewardDef == null)
            {
                return;
            }

            var currencyRewards = TryGetObjectArray(rewardDef, "MissionCurrencyReward");
            if (currencyRewards == null)
            {
                return;
            }

            for (var i = 0; i < currencyRewards.Length; i++)
            {
                var reward = currencyRewards[i] as IDictionary;
                if (reward == null)
                {
                    continue;
                }

                var currencyId = GetString(reward, "CurrencyId");
                var outcome = GetString(reward, "MissionOutcome");
                if (string.IsNullOrEmpty(currencyId) || string.IsNullOrEmpty(outcome))
                {
                    continue;
                }

                map[BuildMissionCurrencyRewardKey(rewardSection, missionName, outcome, currencyId)] = GetInt32(reward, "EarnedValue", 0);
            }
        }

        private static void TryAddMissionItemChanges(Dictionary<string, ItemChange[]> map, string rewardSection, string missionName, IDictionary rewardDef)
        {
            if (map == null || string.IsNullOrEmpty(rewardSection) || string.IsNullOrEmpty(missionName) || rewardDef == null)
            {
                return;
            }

            var itemChanges = TryGetObjectArray(rewardDef, "ItemChanges");
            if (itemChanges == null || itemChanges.Length == 0)
            {
                return;
            }

            var perOutcome = new Dictionary<string, List<ItemChange>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < itemChanges.Length; i++)
            {
                var itemChange = itemChanges[i] as IDictionary;
                if (itemChange == null)
                {
                    continue;
                }

                var itemId = GetString(itemChange, "ItemDefintionId");
                var outcome = GetString(itemChange, "MissionOutcome");
                if (string.IsNullOrEmpty(outcome))
                {
                    outcome = "Victory";
                }

                var delta = GetInt32(itemChange, "Delta", 0);
                if (string.IsNullOrEmpty(itemId) || delta == 0)
                {
                    continue;
                }

                List<ItemChange> bucket;
                if (!perOutcome.TryGetValue(outcome, out bucket) || bucket == null)
                {
                    bucket = new List<ItemChange>();
                    perOutcome[outcome] = bucket;
                }

                try
                {
                    bucket.Add(new ItemChange(itemId, delta)
                    {
                        Quality = GetInt32(itemChange, "Quality", 0),
                        Flavour = GetInt32(itemChange, "Flavour", -1),
                    });
                }
                catch
                {
                }
            }

            foreach (var entry in perOutcome)
            {
                if (!string.IsNullOrEmpty(entry.Key) && entry.Value != null && entry.Value.Count > 0)
                {
                    map[BuildMissionRewardKey(rewardSection, missionName, entry.Key)] = entry.Value.ToArray();
                }
            }
        }

        private static void TryAddMissionRewardUnlocks(Dictionary<string, string[]> map, string rewardSection, string missionName, IDictionary rewardDef)
        {
            if (map == null || string.IsNullOrEmpty(rewardSection) || string.IsNullOrEmpty(missionName) || rewardDef == null)
            {
                return;
            }

            var unlocks = TryGetObjectArray(rewardDef, "GrantedUnlocks");
            if (unlocks == null || unlocks.Length == 0)
            {
                return;
            }

            var values = new List<string>();
            for (var i = 0; i < unlocks.Length; i++)
            {
                var unlock = unlocks[i] as string;
                if (!string.IsNullOrEmpty(unlock) && !values.Contains(unlock))
                {
                    values.Add(unlock);
                }
            }

            if (values.Count > 0)
            {
                map[BuildMissionRewardKey(rewardSection, missionName, "Victory")] = values.ToArray();
            }
        }

        private static void TryAddMissionUnlockDeactivations(Dictionary<string, string[]> map, string missionName, IDictionary mission)
        {
            if (map == null || string.IsNullOrEmpty(missionName) || mission == null)
            {
                return;
            }

            var unlocks = TryGetObjectArray(mission, "UnlocksDeactivatedOnVictory");
            if (unlocks == null || unlocks.Length == 0)
            {
                return;
            }

            var values = new List<string>();
            for (var i = 0; i < unlocks.Length; i++)
            {
                var unlock = unlocks[i] as string;
                if (!string.IsNullOrEmpty(unlock) && !values.Contains(unlock))
                {
                    values.Add(unlock);
                }
            }

            if (values.Count > 0)
            {
                map[missionName] = values.ToArray();
            }
        }

        private static string BuildBodytypeKey(ulong metatypeId, ulong genderId)
        {
            return metatypeId.ToString(CultureInfo.InvariantCulture) + "|" + genderId.ToString(CultureInfo.InvariantCulture);
        }

        private static string BuildMissionRewardKey(string rewardSection, string missionName, string missionOutcome)
        {
            return rewardSection + "|" + missionName + "|" + missionOutcome;
        }

        private static string BuildMissionCurrencyRewardKey(string rewardSection, string missionName, string missionOutcome, string currencyId)
        {
            return BuildMissionRewardKey(rewardSection, missionName, missionOutcome) + "|" + currencyId;
        }

        private static object[] TryGetObjectArray(IDictionary dict, string key)
        {
            if (dict == null || string.IsNullOrEmpty(key) || !dict.Contains(key))
            {
                return null;
            }

            return ToObjectArray(dict[key]);
        }

        private static object[] ToObjectArray(object value)
        {
            if (value == null)
            {
                return null;
            }

            var arr = value as object[];
            if (arr != null)
            {
                return arr;
            }

            var list = value as ArrayList;
            if (list != null)
            {
                var copy = new object[list.Count];
                list.CopyTo(copy);
                return copy;
            }

            return null;
        }

        private static string GetString(IDictionary dict, string key)
        {
            if (dict == null || string.IsNullOrEmpty(key) || !dict.Contains(key) || dict[key] == null)
            {
                return null;
            }

            return dict[key] as string;
        }

        private static int GetInt32(IDictionary dict, string key, int defaultValue)
        {
            if (dict == null || string.IsNullOrEmpty(key) || !dict.Contains(key) || dict[key] == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(dict[key], CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static ulong GetUInt64(IDictionary dict, string key, ulong defaultValue)
        {
            if (dict == null || string.IsNullOrEmpty(key) || !dict.Contains(key) || dict[key] == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToUInt64(dict[key], CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static DateTime GetDateTime(IDictionary dict, string key)
        {
            if (dict == null || string.IsNullOrEmpty(key) || !dict.Contains(key) || dict[key] == null)
            {
                return default(DateTime);
            }

            try
            {
                return Convert.ToDateTime(dict[key], CultureInfo.InvariantCulture);
            }
            catch
            {
                return default(DateTime);
            }
        }
    }
}
