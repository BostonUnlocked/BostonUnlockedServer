using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

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

        private static readonly object CacheLock = new object();
        private static string _cachedPath;
        private static DateTime _cachedLastWriteUtc;
        private static MetagameplayStaticDataIndex _cachedIndex;
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        public readonly Dictionary<string, SkillTreeInfo> SkillTrees;
        public readonly Dictionary<string, List<string>> InitialSkills;
        public readonly Dictionary<string, Dictionary<string, int>> ShopPrices;
        public readonly Dictionary<string, ItemDefinitionInfo> ItemDefinitions;

        private MetagameplayStaticDataIndex()
        {
            SkillTrees = new Dictionary<string, SkillTreeInfo>(StringComparer.Ordinal);
            InitialSkills = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            ShopPrices = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            ItemDefinitions = new Dictionary<string, ItemDefinitionInfo>(StringComparer.OrdinalIgnoreCase);
        }

        public static MetagameplayStaticDataIndex Load(string staticDataDir)
        {
            var path = !string.IsNullOrEmpty(staticDataDir)
                ? Path.Combine(staticDataDir, "metagameplay.json")
                : null;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return new MetagameplayStaticDataIndex();
            }

            var lastWriteUtc = File.GetLastWriteTimeUtc(path);
            lock (CacheLock)
            {
                if (_cachedIndex != null
                    && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase)
                    && _cachedLastWriteUtc == lastWriteUtc)
                {
                    return _cachedIndex;
                }

                _cachedIndex = LoadFromPath(path);
                _cachedPath = path;
                _cachedLastWriteUtc = lastWriteUtc;
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

            Dictionary<string, int> shop;
            if (!ShopPrices.TryGetValue(shopKeeper, out shop) || shop == null)
            {
                return false;
            }

            return shop.TryGetValue(itemId, out price);
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

        private static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        private static MetagameplayStaticDataIndex LoadFromPath(string path)
        {
            var result = new MetagameplayStaticDataIndex();

            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrEmpty(json))
                {
                    return result;
                }

                var root = Json.DeserializeObject(json);
                var components = TryGetObjectArray(root as IDictionary, "Components");
                if (components == null)
                {
                    components = ToObjectArray(root);
                }
                if (components == null)
                {
                    return result;
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
                        continue;
                    }

                    if (!string.IsNullOrEmpty(typeName) && typeName.IndexOf("ShopListData", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ParseShopListData(component, result);
                        continue;
                    }

                    ParseItemDefinition(component, result);
                }
            }
            catch
            {
            }

            return result;
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

                Dictionary<string, int> shopEntries;
                if (!result.ShopPrices.TryGetValue(internalName, out shopEntries) || shopEntries == null)
                {
                    shopEntries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    result.ShopPrices[internalName] = shopEntries;
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

                    shopEntries[itemId] = GetInt32(entry, "Price", 0);
                }
            }
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
    }
}
