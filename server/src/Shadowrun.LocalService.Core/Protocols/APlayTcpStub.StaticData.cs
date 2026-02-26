using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private static readonly object BodytypeMapLock = new object();
        private static Dictionary<string, ulong> _bodytypeMap;
        private static string _bodytypeMapSourceDir;

        private static readonly object MissionRewardMapLock = new object();
        private static Dictionary<string, int> _missionRewardMap;
        private static Dictionary<string, ItemChange[]> _missionRewardItemChangesMap;
        private static string _missionRewardMapSourceDir;
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private static readonly object StorylineMapLock = new object();
        private static Dictionary<string, StorylineInfo> _storylineMap;
        private static string _storylineMapSourceDir;

        private static readonly object SkillKarmaCostMapLock = new object();
        private static Dictionary<string, int> _skillKarmaCostMap;
        private static string _skillKarmaCostMapSourceDir;

        private static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        private bool TryResolveSkillKarmaCost(string skillTechnicalName, out int karmaCost)
        {
            karmaCost = 0;
            try
            {
                if (IsNullOrWhiteSpace(skillTechnicalName))
                {
                    return false;
                }

                var dir = _options != null ? _options.StaticDataDir : null;
                if (IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    return false;
                }

                Dictionary<string, int> map;
                lock (SkillKarmaCostMapLock)
                {
                    if (_skillKarmaCostMap == null || !string.Equals(_skillKarmaCostMapSourceDir, dir, StringComparison.OrdinalIgnoreCase))
                    {
                        _skillKarmaCostMap = LoadSkillKarmaCostMap(dir);
                        _skillKarmaCostMapSourceDir = dir;
                    }
                    map = _skillKarmaCostMap;
                }

                if (map == null || map.Count == 0)
                {
                    return false;
                }

                int found;
                if (map.TryGetValue(skillTechnicalName.Trim(), out found) && found > 0)
                {
                    karmaCost = found;
                    return true;
                }

                return false;
            }
            catch
            {
                karmaCost = 0;
                return false;
            }
        }

        private static Dictionary<string, int> LoadSkillKarmaCostMap(string staticDataDir)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var path = Path.Combine(staticDataDir, "metagameplay.json");
                if (!File.Exists(path))
                {
                    return result;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var root = Json.DeserializeObject(json);

                object[] rootArr = null;
                var rootDict = root as IDictionary;
                if (rootDict != null && rootDict.Contains("Components") && rootDict["Components"] != null)
                {
                    rootArr = rootDict["Components"] as object[];
                    if (rootArr == null)
                    {
                        var rootList = rootDict["Components"] as ArrayList;
                        if (rootList != null)
                        {
                            rootArr = new object[rootList.Count];
                            rootList.CopyTo(rootArr);
                        }
                    }
                }
                if (rootArr == null)
                {
                    rootArr = root as object[];
                    if (rootArr == null)
                    {
                        var rootList = root as ArrayList;
                        if (rootList != null)
                        {
                            rootArr = new object[rootList.Count];
                            rootList.CopyTo(rootArr);
                        }
                    }
                }

                if (rootArr == null || rootArr.Length == 0)
                {
                    return result;
                }

                for (var i = 0; i < rootArr.Length; i++)
                {
                    var comp = rootArr[i] as IDictionary;
                    if (comp == null)
                    {
                        continue;
                    }

                    var typeName = comp.Contains("TypeName") ? (comp["TypeName"] as string) : null;
                    if (IsNullOrWhiteSpace(typeName) || typeName.IndexOf("SkillTreeData", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var defs = comp.Contains("SkillTreeDefinitions") ? comp["SkillTreeDefinitions"] : null;
                    object[] defArr = defs as object[];
                    if (defArr == null)
                    {
                        var list = defs as ArrayList;
                        if (list != null)
                        {
                            defArr = new object[list.Count];
                            list.CopyTo(defArr);
                        }
                    }
                    if (defArr == null || defArr.Length == 0)
                    {
                        continue;
                    }

                    for (var d = 0; d < defArr.Length; d++)
                    {
                        var def = defArr[d] as IDictionary;
                        if (def == null)
                        {
                            continue;
                        }

                        var levelsObj = def.Contains("SkillLevels") ? def["SkillLevels"] : null;
                        object[] levels = levelsObj as object[];
                        if (levels == null)
                        {
                            var levelsList = levelsObj as ArrayList;
                            if (levelsList != null)
                            {
                                levels = new object[levelsList.Count];
                                levelsList.CopyTo(levels);
                            }
                        }
                        if (levels == null || levels.Length == 0)
                        {
                            continue;
                        }

                        for (var l = 0; l < levels.Length; l++)
                        {
                            var level = levels[l] as IDictionary;
                            if (level == null)
                            {
                                continue;
                            }

                            var skillsObj = level.Contains("SerializedSkills") ? level["SerializedSkills"] : null;
                            object[] skills = skillsObj as object[];
                            if (skills == null)
                            {
                                var skillsList = skillsObj as ArrayList;
                                if (skillsList != null)
                                {
                                    skills = new object[skillsList.Count];
                                    skillsList.CopyTo(skills);
                                }
                            }
                            if (skills == null || skills.Length == 0)
                            {
                                continue;
                            }

                            for (var s = 0; s < skills.Length; s++)
                            {
                                var skill = skills[s] as IDictionary;
                                if (skill == null)
                                {
                                    continue;
                                }

                                var tech = skill.Contains("TechnicalName") ? (skill["TechnicalName"] as string) : null;
                                if (IsNullOrWhiteSpace(tech))
                                {
                                    continue;
                                }

                                var cost = 0;
                                try
                                {
                                    if (skill.Contains("KarmaCost") && skill["KarmaCost"] != null)
                                    {
                                        cost = Convert.ToInt32(skill["KarmaCost"], CultureInfo.InvariantCulture);
                                    }
                                }
                                catch
                                {
                                    cost = 0;
                                }

                                if (cost <= 0)
                                {
                                    continue;
                                }

                                // First write wins; costs are per-skill technical name.
                                if (!result.ContainsKey(tech))
                                {
                                    result[tech] = cost;
                                }
                            }
                        }
                    }
                }

                return result;
            }
            catch
            {
                return result;
            }
        }

        private bool TryResolveBodytypeId(ulong metatypeId, ulong genderId, out ulong bodytypeId)
        {
            bodytypeId = 0UL;
            try
            {
                var dir = _options != null ? _options.StaticDataDir : null;
                if (IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    return false;
                }

                Dictionary<string, ulong> map;
                lock (BodytypeMapLock)
                {
                    if (_bodytypeMap == null || !string.Equals(_bodytypeMapSourceDir, dir, StringComparison.OrdinalIgnoreCase))
                    {
                        _bodytypeMap = LoadBodytypeMap(dir);
                        _bodytypeMapSourceDir = dir;
                    }
                    map = _bodytypeMap;
                }

                if (map == null)
                {
                    return false;
                }

                ulong found;
                if (map.TryGetValue(metatypeId.ToString() + "|" + genderId.ToString(), out found) && found != 0UL)
                {
                    bodytypeId = found;
                    return true;
                }

                return false;
            }
            catch
            {
                bodytypeId = 0UL;
                return false;
            }
        }

        private static Dictionary<string, ulong> LoadBodytypeMap(string staticDataDir)
        {
            var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(staticDataDir, "metagameplay.json");
                if (!File.Exists(path))
                {
                    return result;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var root = Json.DeserializeObject(json);
                // The file format in our extracted static-data is usually a composite object:
                // { "TypeName": "...Composite...", "Components": [ { ... }, ... ] }
                // but older extraction formats can be arrays directly.
                object[] rootArr = null;
                var rootDict = root as IDictionary;
                if (rootDict != null && rootDict.Contains("Components") && rootDict["Components"] != null)
                {
                    rootArr = rootDict["Components"] as object[];
                    if (rootArr == null)
                    {
                        var rootList = rootDict["Components"] as ArrayList;
                        if (rootList != null)
                        {
                            rootArr = new object[rootList.Count];
                            rootList.CopyTo(rootArr);
                        }
                    }
                }
                if (rootArr == null)
                {
                    rootArr = root as object[];
                    if (rootArr == null)
                    {
                        var rootList = root as ArrayList;
                        if (rootList != null)
                        {
                            rootArr = new object[rootList.Count];
                            rootList.CopyTo(rootArr);
                        }
                    }
                }

                if (rootArr == null)
                {
                    return result;
                }

                for (var i = 0; i < rootArr.Length; i++)
                {
                    var dict = rootArr[i] as IDictionary;
                    if (dict == null)
                    {
                        continue;
                    }

                    if (!dict.Contains("BodytypeDefinitions") || dict["BodytypeDefinitions"] == null)
                    {
                        continue;
                    }

                    var defsArr = dict["BodytypeDefinitions"] as object[];
                    if (defsArr == null)
                    {
                        var defsList = dict["BodytypeDefinitions"] as ArrayList;
                        if (defsList != null)
                        {
                            defsArr = new object[defsList.Count];
                            defsList.CopyTo(defsArr);
                        }
                    }
                    if (defsArr == null)
                    {
                        continue;
                    }

                    for (var j = 0; j < defsArr.Length; j++)
                    {
                        var def = defsArr[j] as IDictionary;
                        if (def == null)
                        {
                            continue;
                        }

                        ulong id;
                        ulong metatype;
                        ulong gender;
                        if (!TryGetUlong(def, "Id", out id) || id == 0UL)
                        {
                            continue;
                        }
                        if (!TryGetUlong(def, "MetatypeId", out metatype) || metatype == 0UL)
                        {
                            continue;
                        }
                        if (!TryGetUlong(def, "GenderId", out gender) || gender == 0UL)
                        {
                            continue;
                        }

                        result[metatype.ToString() + "|" + gender.ToString()] = id;
                    }
                }
            }
            catch
            {
                return result;
            }

            return result;
        }

        private bool TryResolveMissionCurrencyReward(string missionName, string missionOutcome, string currencyId, out int earnedValue)
        {
            return TryResolveMissionCurrencyRewardInternal("Rewards", missionName, missionOutcome, currencyId, out earnedValue);
        }

        private bool TryResolveMissionStoryCurrencyReward(string missionName, string missionOutcome, string currencyId, out int earnedValue)
        {
            return TryResolveMissionCurrencyRewardInternal("StoryRewards", missionName, missionOutcome, currencyId, out earnedValue);
        }

        private bool TryResolveMissionStoryItemChanges(string missionName, string missionOutcome, out ItemChange[] itemChanges)
        {
            return TryResolveMissionItemChangesInternal("StoryRewards", missionName, missionOutcome, out itemChanges);
        }

        private bool TryResolveMissionItemChangesInternal(string rewardSection, string missionName, string missionOutcome, out ItemChange[] itemChanges)
        {
            itemChanges = null;
            try
            {
                if (IsNullOrWhiteSpace(rewardSection) || IsNullOrWhiteSpace(missionName) || IsNullOrWhiteSpace(missionOutcome))
                {
                    return false;
                }

                var dir = _options != null ? _options.StaticDataDir : null;
                if (IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    return false;
                }

                Dictionary<string, ItemChange[]> map;
                lock (MissionRewardMapLock)
                {
                    if (_missionRewardMap == null || _missionRewardItemChangesMap == null || !string.Equals(_missionRewardMapSourceDir, dir, StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, int> currency;
                        Dictionary<string, ItemChange[]> items;
                        LoadMissionRewardMaps(dir, out currency, out items);
                        _missionRewardMap = currency;
                        _missionRewardItemChangesMap = items;
                        _missionRewardMapSourceDir = dir;
                    }
                    map = _missionRewardItemChangesMap;
                }

                if (map == null)
                {
                    return false;
                }

                var key = rewardSection + "|" + missionName + "|" + missionOutcome;
                ItemChange[] found;
                if (map.TryGetValue(key, out found) && found != null && found.Length > 0)
                {
                    itemChanges = found;
                    return true;
                }

                return false;
            }
            catch
            {
                itemChanges = null;
                return false;
            }
        }

        private bool TryResolveMissionCurrencyRewardInternal(string rewardSection, string missionName, string missionOutcome, string currencyId, out int earnedValue)
        {
            earnedValue = 0;
            try
            {
                if (IsNullOrWhiteSpace(rewardSection) || IsNullOrWhiteSpace(missionName) || IsNullOrWhiteSpace(missionOutcome) || IsNullOrWhiteSpace(currencyId))
                {
                    return false;
                }

                var dir = _options != null ? _options.StaticDataDir : null;
                if (IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    return false;
                }

                Dictionary<string, int> map;
                lock (MissionRewardMapLock)
                {
                    if (_missionRewardMap == null || !string.Equals(_missionRewardMapSourceDir, dir, StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, int> currency;
                        Dictionary<string, ItemChange[]> items;
                        LoadMissionRewardMaps(dir, out currency, out items);
                        _missionRewardMap = currency;
                        _missionRewardItemChangesMap = items;
                        _missionRewardMapSourceDir = dir;
                    }
                    map = _missionRewardMap;
                }

                if (map == null)
                {
                    return false;
                }

                var key = rewardSection + "|" + missionName + "|" + missionOutcome + "|" + currencyId;
                int found;
                if (map.TryGetValue(key, out found))
                {
                    earnedValue = found;
                    return true;
                }
                return false;
            }
            catch
            {
                earnedValue = 0;
                return false;
            }
        }

        private static Dictionary<string, int> LoadMissionRewardMap(string staticDataDir)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(staticDataDir, "globals.json");
                if (!File.Exists(path))
                {
                    return result;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var root = Json.DeserializeObject(json);

                // globals.json is a Composite with a "Components" array.
                object[] components = null;
                var rootDict = root as IDictionary;
                if (rootDict != null && rootDict.Contains("Components") && rootDict["Components"] != null)
                {
                    components = rootDict["Components"] as object[];
                    if (components == null)
                    {
                        var list = rootDict["Components"] as ArrayList;
                        if (list != null)
                        {
                            components = new object[list.Count];
                            list.CopyTo(components);
                        }
                    }
                }
                if (components == null)
                {
                    return result;
                }

                for (var i = 0; i < components.Length; i++)
                {
                    var comp = components[i] as IDictionary;
                    if (comp == null)
                    {
                        continue;
                    }

                    // Find the MissionDefinition collection component.
                    // We don't rely on exact TypeName; just look for a "MissionDefinitions" array.
                    if (!comp.Contains("MissionDefinitions") || comp["MissionDefinitions"] == null)
                    {
                        continue;
                    }

                    object[] missionsArr = comp["MissionDefinitions"] as object[];
                    if (missionsArr == null)
                    {
                        var missionsList = comp["MissionDefinitions"] as ArrayList;
                        if (missionsList != null)
                        {
                            missionsArr = new object[missionsList.Count];
                            missionsList.CopyTo(missionsArr);
                        }
                    }
                    if (missionsArr == null)
                    {
                        continue;
                    }

                    for (var j = 0; j < missionsArr.Length; j++)
                    {
                        var mission = missionsArr[j] as IDictionary;
                        if (mission == null)
                        {
                            continue;
                        }

                        var missionName = mission.Contains("Name") ? (mission["Name"] as string) : null;
                        if (IsNullOrWhiteSpace(missionName))
                        {
                            continue;
                        }

                        // Rewards (immediate) and StoryRewards (redeemed in hub) both contain MissionCurrencyReward.
                        // Nuyen is typically in StoryRewards.
                        var rewards = mission.Contains("Rewards") ? (mission["Rewards"] as IDictionary) : null;
                        if (rewards != null)
                        {
                            TryAddMissionCurrencyRewards(result, "Rewards", missionName, rewards);
                        }

                        var storyRewards = mission.Contains("StoryRewards") ? (mission["StoryRewards"] as IDictionary) : null;
                        if (storyRewards != null)
                        {
                            TryAddMissionCurrencyRewards(result, "StoryRewards", missionName, storyRewards);
                        }
                    }
                }
            }
            catch
            {
                return result;
            }

            return result;
        }

        private static void LoadMissionRewardMaps(string staticDataDir, out Dictionary<string, int> currencyMap, out Dictionary<string, ItemChange[]> itemChangesMap)
        {
            currencyMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            itemChangesMap = new Dictionary<string, ItemChange[]>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var path = Path.Combine(staticDataDir, "globals.json");
                if (!File.Exists(path))
                {
                    return;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var root = Json.DeserializeObject(json);

                object[] components = null;
                var rootDict = root as IDictionary;
                if (rootDict != null && rootDict.Contains("Components") && rootDict["Components"] != null)
                {
                    components = rootDict["Components"] as object[];
                    if (components == null)
                    {
                        var list = rootDict["Components"] as ArrayList;
                        if (list != null)
                        {
                            components = new object[list.Count];
                            list.CopyTo(components);
                        }
                    }
                }
                if (components == null)
                {
                    return;
                }

                for (var i = 0; i < components.Length; i++)
                {
                    var comp = components[i] as IDictionary;
                    if (comp == null)
                    {
                        continue;
                    }

                    if (!comp.Contains("MissionDefinitions") || comp["MissionDefinitions"] == null)
                    {
                        continue;
                    }

                    object[] missionsArr = comp["MissionDefinitions"] as object[];
                    if (missionsArr == null)
                    {
                        var missionsList = comp["MissionDefinitions"] as ArrayList;
                        if (missionsList != null)
                        {
                            missionsArr = new object[missionsList.Count];
                            missionsList.CopyTo(missionsArr);
                        }
                    }
                    if (missionsArr == null)
                    {
                        continue;
                    }

                    for (var j = 0; j < missionsArr.Length; j++)
                    {
                        var mission = missionsArr[j] as IDictionary;
                        if (mission == null)
                        {
                            continue;
                        }

                        var missionName = mission.Contains("Name") ? (mission["Name"] as string) : null;
                        if (IsNullOrWhiteSpace(missionName))
                        {
                            continue;
                        }

                        var rewards = mission.Contains("Rewards") ? (mission["Rewards"] as IDictionary) : null;
                        if (rewards != null)
                        {
                            TryAddMissionCurrencyRewards(currencyMap, "Rewards", missionName, rewards);
                            TryAddMissionItemChanges(itemChangesMap, "Rewards", missionName, rewards);
                        }

                        var storyRewards = mission.Contains("StoryRewards") ? (mission["StoryRewards"] as IDictionary) : null;
                        if (storyRewards != null)
                        {
                            TryAddMissionCurrencyRewards(currencyMap, "StoryRewards", missionName, storyRewards);
                            TryAddMissionItemChanges(itemChangesMap, "StoryRewards", missionName, storyRewards);
                        }
                    }
                }
            }
            catch
            {
                // best-effort; leave maps as-is
                return;
            }
        }

        private static void TryAddMissionItemChanges(Dictionary<string, ItemChange[]> map, string rewardSection, string missionName, IDictionary rewardDef)
        {
            try
            {
                if (map == null || IsNullOrWhiteSpace(rewardSection) || IsNullOrWhiteSpace(missionName) || rewardDef == null)
                {
                    return;
                }

                if (!rewardDef.Contains("ItemChanges") || rewardDef["ItemChanges"] == null)
                {
                    return;
                }

                object[] itemChanges = rewardDef["ItemChanges"] as object[];
                if (itemChanges == null)
                {
                    var list = rewardDef["ItemChanges"] as ArrayList;
                    if (list != null)
                    {
                        itemChanges = new object[list.Count];
                        list.CopyTo(itemChanges);
                    }
                }
                if (itemChanges == null || itemChanges.Length == 0)
                {
                    return;
                }

                // ItemChange objects don't always carry an explicit MissionOutcome; default to Victory.
                var perOutcome = new Dictionary<string, List<ItemChange>>(StringComparer.OrdinalIgnoreCase);

                for (var k = 0; k < itemChanges.Length; k++)
                {
                    var ic = itemChanges[k] as IDictionary;
                    if (ic == null)
                    {
                        continue;
                    }

                    var itemId = ic.Contains("ItemDefintionId") ? (ic["ItemDefintionId"] as string) : null;
                    var outcome = ic.Contains("MissionOutcome") ? (ic["MissionOutcome"] as string) : null;
                    if (IsNullOrWhiteSpace(outcome))
                    {
                        outcome = "Victory";
                    }

                    var delta = 0;
                    var quality = 0;
                    var flavour = -1;
                    try
                    {
                        if (ic.Contains("Delta") && ic["Delta"] != null)
                        {
                            delta = Convert.ToInt32(ic["Delta"], CultureInfo.InvariantCulture);
                        }
                    }
                    catch
                    {
                        delta = 0;
                    }
                    try
                    {
                        if (ic.Contains("Quality") && ic["Quality"] != null)
                        {
                            quality = Convert.ToInt32(ic["Quality"], CultureInfo.InvariantCulture);
                        }
                    }
                    catch
                    {
                        quality = 0;
                    }
                    try
                    {
                        if (ic.Contains("Flavour") && ic["Flavour"] != null)
                        {
                            flavour = Convert.ToInt32(ic["Flavour"], CultureInfo.InvariantCulture);
                        }
                    }
                    catch
                    {
                        flavour = -1;
                    }

                    if (IsNullOrWhiteSpace(itemId) || delta == 0)
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
                        var change = new ItemChange(itemId, delta)
                        {
                            Quality = quality,
                            Flavour = flavour,
                        };
                        bucket.Add(change);
                    }
                    catch
                    {
                    }
                }

                foreach (var kvp in perOutcome)
                {
                    var outcome = kvp.Key;
                    var list = kvp.Value;
                    if (IsNullOrWhiteSpace(outcome) || list == null || list.Count == 0)
                    {
                        continue;
                    }

                    map[rewardSection + "|" + missionName + "|" + outcome] = list.ToArray();
                }
            }
            catch
            {
                return;
            }
        }

        private static void TryAddMissionCurrencyRewards(Dictionary<string, int> map, string rewardSection, string missionName, IDictionary rewardDef)
        {
            try
            {
                if (map == null || IsNullOrWhiteSpace(rewardSection) || IsNullOrWhiteSpace(missionName) || rewardDef == null)
                {
                    return;
                }

                if (!rewardDef.Contains("MissionCurrencyReward") || rewardDef["MissionCurrencyReward"] == null)
                {
                    return;
                }

                object[] currencyRewards = rewardDef["MissionCurrencyReward"] as object[];
                if (currencyRewards == null)
                {
                    var crList = rewardDef["MissionCurrencyReward"] as ArrayList;
                    if (crList != null)
                    {
                        currencyRewards = new object[crList.Count];
                        crList.CopyTo(currencyRewards);
                    }
                }
                if (currencyRewards == null)
                {
                    return;
                }

                for (var k = 0; k < currencyRewards.Length; k++)
                {
                    var cr = currencyRewards[k] as IDictionary;
                    if (cr == null)
                    {
                        continue;
                    }

                    var currencyId = cr.Contains("CurrencyId") ? (cr["CurrencyId"] as string) : null;
                    var outcome = cr.Contains("MissionOutcome") ? (cr["MissionOutcome"] as string) : null;
                    int value = 0;
                    try
                    {
                        if (cr.Contains("EarnedValue") && cr["EarnedValue"] != null)
                        {
                            value = Convert.ToInt32(cr["EarnedValue"], CultureInfo.InvariantCulture);
                        }
                    }
                    catch
                    {
                        value = 0;
                    }

                    if (IsNullOrWhiteSpace(currencyId) || IsNullOrWhiteSpace(outcome))
                    {
                        continue;
                    }

                    // Key: rewardSection|mission|outcome|currency
                    map[rewardSection + "|" + missionName + "|" + outcome + "|" + currencyId] = value;
                }
            }
            catch
            {
                return;
            }
        }

        private sealed class StorylineInfo
        {
            public string TechnicalName;
            public List<ChapterInfo> Chapters = new List<ChapterInfo>();
        }

        private sealed class ChapterInfo
        {
            public int Index;
            public string TechnicalName;
            public string Hub;
            public List<string> RequiredMissions = new List<string>();
            public List<string> RequiredUnlocks = new List<string>();
            public List<string> DialogNpcIds = new List<string>();
        }

        private bool TryGetStoryline(string storylineTechnicalName, out StorylineInfo storyline)
        {
            storyline = null;
            try
            {
                if (IsNullOrWhiteSpace(storylineTechnicalName))
                {
                    return false;
                }

                var dir = _options != null ? _options.StaticDataDir : null;
                if (IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    return false;
                }

                Dictionary<string, StorylineInfo> map;
                lock (StorylineMapLock)
                {
                    if (_storylineMap == null || !string.Equals(_storylineMapSourceDir, dir, StringComparison.OrdinalIgnoreCase))
                    {
                        _storylineMap = LoadStorylineMap(dir);
                        _storylineMapSourceDir = dir;
                    }
                    map = _storylineMap;
                }

                if (map == null)
                {
                    return false;
                }

                return map.TryGetValue(storylineTechnicalName, out storyline) && storyline != null;
            }
            catch
            {
                storyline = null;
                return false;
            }
        }

        private static Dictionary<string, StorylineInfo> LoadStorylineMap(string staticDataDir)
        {
            var result = new Dictionary<string, StorylineInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(staticDataDir, "metagameplay.json");
                if (!File.Exists(path))
                {
                    return result;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var root = Json.DeserializeObject(json);

                object[] rootArr = null;
                var rootDict = root as IDictionary;
                if (rootDict != null && rootDict.Contains("Components") && rootDict["Components"] != null)
                {
                    rootArr = rootDict["Components"] as object[];
                    if (rootArr == null)
                    {
                        var rootList = rootDict["Components"] as ArrayList;
                        if (rootList != null)
                        {
                            rootArr = new object[rootList.Count];
                            rootList.CopyTo(rootArr);
                        }
                    }
                }
                if (rootArr == null)
                {
                    rootArr = root as object[];
                    if (rootArr == null)
                    {
                        var rootList = root as ArrayList;
                        if (rootList != null)
                        {
                            rootArr = new object[rootList.Count];
                            rootList.CopyTo(rootArr);
                        }
                    }
                }
                if (rootArr == null)
                {
                    return result;
                }

                for (var i = 0; i < rootArr.Length; i++)
                {
                    var comp = rootArr[i] as IDictionary;
                    if (comp == null)
                    {
                        continue;
                    }

                    // StorylineData component has a "Storylines" array.
                    if (!comp.Contains("Storylines") || comp["Storylines"] == null)
                    {
                        continue;
                    }

                    object[] storylinesArr = comp["Storylines"] as object[];
                    if (storylinesArr == null)
                    {
                        var storylinesList = comp["Storylines"] as ArrayList;
                        if (storylinesList != null)
                        {
                            storylinesArr = new object[storylinesList.Count];
                            storylinesList.CopyTo(storylinesArr);
                        }
                    }
                    if (storylinesArr == null)
                    {
                        continue;
                    }

                    for (var s = 0; s < storylinesArr.Length; s++)
                    {
                        var storylineDict = storylinesArr[s] as IDictionary;
                        if (storylineDict == null)
                        {
                            continue;
                        }

                        var technicalName = storylineDict.Contains("TechnicalName") ? (storylineDict["TechnicalName"] as string) : null;
                        if (IsNullOrWhiteSpace(technicalName))
                        {
                            continue;
                        }

                        var info = new StorylineInfo();
                        info.TechnicalName = technicalName;

                        object[] chaptersArr = null;
                        if (storylineDict.Contains("Chapters") && storylineDict["Chapters"] != null)
                        {
                            chaptersArr = storylineDict["Chapters"] as object[];
                            if (chaptersArr == null)
                            {
                                var chaptersList = storylineDict["Chapters"] as ArrayList;
                                if (chaptersList != null)
                                {
                                    chaptersArr = new object[chaptersList.Count];
                                    chaptersList.CopyTo(chaptersArr);
                                }
                            }
                        }

                        if (chaptersArr != null)
                        {
                            for (var c = 0; c < chaptersArr.Length; c++)
                            {
                                var chapterDict = chaptersArr[c] as IDictionary;
                                if (chapterDict == null)
                                {
                                    continue;
                                }

                                var ch = new ChapterInfo();
                                ch.Index = c;
                                ch.TechnicalName = chapterDict.Contains("TechnicalName") ? (chapterDict["TechnicalName"] as string) : null;
                                ch.Hub = chapterDict.Contains("Hub") ? (chapterDict["Hub"] as string) : null;

                                // Required unlocks
                                if (chapterDict.Contains("RequiredUnlocksForNextChapter") && chapterDict["RequiredUnlocksForNextChapter"] != null)
                                {
                                    var unlocksArr = chapterDict["RequiredUnlocksForNextChapter"] as object[];
                                    if (unlocksArr == null)
                                    {
                                        var unlocksList = chapterDict["RequiredUnlocksForNextChapter"] as ArrayList;
                                        if (unlocksList != null)
                                        {
                                            unlocksArr = new object[unlocksList.Count];
                                            unlocksList.CopyTo(unlocksArr);
                                        }
                                    }
                                    if (unlocksArr != null)
                                    {
                                        for (var u = 0; u < unlocksArr.Length; u++)
                                        {
                                            var unlockName = unlocksArr[u] as string;
                                            if (!IsNullOrWhiteSpace(unlockName))
                                            {
                                                ch.RequiredUnlocks.Add(unlockName);
                                            }
                                        }
                                    }
                                }

                                // Required missions (for next chapter) => missions of this chapter.
                                if (chapterDict.Contains("RequiredMissionsForNextChapter") && chapterDict["RequiredMissionsForNextChapter"] != null)
                                {
                                    object[] missionsArr = chapterDict["RequiredMissionsForNextChapter"] as object[];
                                    if (missionsArr == null)
                                    {
                                        var missionsList = chapterDict["RequiredMissionsForNextChapter"] as ArrayList;
                                        if (missionsList != null)
                                        {
                                            missionsArr = new object[missionsList.Count];
                                            missionsList.CopyTo(missionsArr);
                                        }
                                    }
                                    if (missionsArr != null)
                                    {
                                        for (var m = 0; m < missionsArr.Length; m++)
                                        {
                                            var missionRef = missionsArr[m] as IDictionary;
                                            if (missionRef == null)
                                            {
                                                continue;
                                            }
                                            var missionName = missionRef.Contains("Mission") ? (missionRef["Mission"] as string) : null;
                                            if (!IsNullOrWhiteSpace(missionName))
                                            {
                                                ch.RequiredMissions.Add(missionName);
                                            }
                                        }
                                    }
                                }

                                // NPCs with chapter dialogs (used for interaction-state fallback when explicit
                                // InteractedWithNpcMessage traffic is absent in some client flows).
                                if (chapterDict.Contains("DialogsForChapter") && chapterDict["DialogsForChapter"] != null)
                                {
                                    object[] dialogsArr = chapterDict["DialogsForChapter"] as object[];
                                    if (dialogsArr == null)
                                    {
                                        var dialogsList = chapterDict["DialogsForChapter"] as ArrayList;
                                        if (dialogsList != null)
                                        {
                                            dialogsArr = new object[dialogsList.Count];
                                            dialogsList.CopyTo(dialogsArr);
                                        }
                                    }

                                    if (dialogsArr != null)
                                    {
                                        for (var d = 0; d < dialogsArr.Length; d++)
                                        {
                                            var dialogDef = dialogsArr[d] as IDictionary;
                                            if (dialogDef == null)
                                            {
                                                continue;
                                            }

                                            var npcId = dialogDef.Contains("Id") ? (dialogDef["Id"] as string) : null;
                                            if (IsNullOrWhiteSpace(npcId))
                                            {
                                                continue;
                                            }

                                            if (!ch.DialogNpcIds.Contains(npcId))
                                            {
                                                ch.DialogNpcIds.Add(npcId);
                                            }
                                        }
                                    }
                                }

                                info.Chapters.Add(ch);
                            }
                        }

                        result[technicalName] = info;
                    }
                }

                return result;
            }
            catch
            {
                return result;
            }
        }
    }
}
