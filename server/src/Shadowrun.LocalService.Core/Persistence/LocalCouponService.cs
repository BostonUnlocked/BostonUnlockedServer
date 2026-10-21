using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Shadowrun.LocalService.Core.Coupons;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed partial class LocalUserStore
    {
        private sealed class LocalCouponService
        {
            private static readonly object CouponItemPackageLock = new object();
            private static Dictionary<string, List<string>> CachedCouponItemPackages;
            private static string CachedCouponItemPackagesSourceIdentity;
            private static readonly string[] BonusCosmeticsPackageItems = new[]
            {
                "Item_AncientsLogoNeon",
                "Item_AstralCoat",
                "Item_BandanaSkull",
                "Item_BasicMageVest",
                "Item_BeardBraided",
                "Item_BeardConchita",
                "Item_BeardFull",
                "Item_BeardStubble",
                "Item_BikerPants",
                "Item_CyberdocOverall",
                "Item_DefaultLowerUnderwear",
                "Item_DefaultUpperUnderwear",
                "Item_DocGloves",
                "Item_DocOverall",
                "Item_DoubleDragon1Neon",
                "Item_DragonHead1Neon",
                "Item_ElegantTop",
                "Item_HairDreads",
                "Item_HairDreadsUndercut",
                "Item_HairLadyLuck",
                "Item_HairLong2",
                "Item_HairMohawkLimp",
                "Item_HairMohawkSideburns",
                "Item_HairPancake",
                "Item_HarebrainedShirt",
                "Item_HeartsBoxers",
                "Item_HipsterBoots",
                "Item_HornsBasic",
                "Item_KimonoMitsuhama",
                "Item_KimonoTop",
                "Item_KimonoWuxing",
                "Item_LeatherBoots",
                "Item_LeatherCoat",
                "Item_MagicalLongsleeve",
                "Item_NinjaGloves",
                "Item_Pack1HackerTop",
                "Item_Pack1MageCoat",
                "Item_Pack3Corset",
                "Item_Pack4BagBoots",
                "Item_Pack4HospitalGown",
                "Item_PuffTie",
                "Item_PuffTieVest",
                "Item_RastaHatRasta",
                "Item_RunnerLongsleeve",
                "Item_ShortsSunrise",
                "Item_Sideburns",
                "Item_SneakersAmerica",
                "Item_SoycafShirt",
                "Item_StreetCorset",
                "Item_SuitJacket",
                "Item_Tribal2Neon",
                "Item_WesternTop",
            };
            private static readonly string[] HalloweenPackageItems = new[]
            {
                "Item_halloweenpet_01",
            };

            private const string CouponGameName = "SRO";
            private const string CouponPackagesPlayerInfoKey = "CouponPackages";
            private const string LegacyUnobtainableCosmeticsPackageName = "UnobtainableCosmetics";

            private readonly LocalUserStore _owner;

            public LocalCouponService(LocalUserStore owner)
            {
                _owner = owner;
            }

            public bool TryResolveCouponItemPackageCode(string code, out string packageTechnicalName)
            {
                packageTechnicalName = null;
                if (LocalUserStore.IsNullOrWhiteSpace(code))
                {
                    return false;
                }

                if (!HonoredCouponCodes.IsHonored(code))
                {
                    return false;
                }

                var packages = GetOrLoadCouponItemPackagesByTechnicalName();
                if (packages == null || packages.Count <= 0)
                {
                    return false;
                }

                List<string> ignored;
                if (packages.TryGetValue(code, out ignored))
                {
                    packageTechnicalName = code;
                    return true;
                }

                var normalized = NormalizeCouponCode(code);
                foreach (var kvp in packages)
                {
                    if (string.Equals(NormalizeCouponCode(kvp.Key), normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        packageTechnicalName = kvp.Key;
                        return true;
                    }
                }

                return false;
            }

            public bool ApplyCouponItemPackageToAllCareers(string identityHash, string packageTechnicalName)
            {
                if (!LocalUserStore.IsGuidish(identityHash) || LocalUserStore.IsNullOrWhiteSpace(packageTechnicalName))
                {
                    return false;
                }

                lock (_owner._lock)
                {
                    var identity = LocalUserStore.NormalizeGuidish(identityHash);
                    var account = _owner.LoadAccountForIdentityNoThrow(identity, true) ?? _owner.LoadAccountNoThrow();
                    var careersObj = account["Careers"];
                    var careersList = LocalUserStore.CoerceToArrayList(careersObj);
                    if (careersList == null)
                    {
                        careersList = LocalUserStore.BuildDefaultCareers(identity);
                        account["Careers"] = careersList;
                    }
                    else if (!(careersObj is ArrayList))
                    {
                        account["Careers"] = careersList;
                    }

                    var anyChanged = false;
                    for (var i = 0; i < careersList.Count; i++)
                    {
                        var dict = careersList[i] as IDictionary;
                        if (dict == null)
                        {
                            continue;
                        }

                        var slot = CareerSlot.FromDictionary(dict);
                        if (slot == null || !slot.IsOccupied)
                        {
                            continue;
                        }

                        var slotChanged = false;
                        if (ApplyCouponItemPackageToCareerNoLock(slot, packageTechnicalName))
                        {
                            slotChanged = true;
                        }

                        if (!slotChanged)
                        {
                            continue;
                        }

                        var updated = slot.ToDictionary();
                        foreach (DictionaryEntry entry in updated)
                        {
                            dict[entry.Key] = entry.Value;
                        }
                        anyChanged = true;
                    }

                    if (anyChanged)
                    {
                        _owner.SaveAccountNoThrow(account);
                    }

                    return anyChanged;
                }
            }

            public bool ApplyCouponItemPackagesToCareerNoLock(string identityHash, CareerSlot slot)
            {
                if (slot == null || !slot.IsOccupied || !LocalUserStore.IsGuidish(identityHash))
                {
                    return false;
                }

                var changed = false;
                var entitled = GetCouponItemPackageEntitlementsForIdentityNoLock(identityHash);
                if (entitled == null || entitled.Count <= 0)
                {
                    return false;
                }

                for (var i = 0; i < entitled.Count; i++)
                {
                    if (ApplyCouponItemPackageToCareerNoLock(slot, entitled[i]))
                    {
                        changed = true;
                    }
                }

                return changed;
            }

            public bool EnsureAllCouponEntitlementItemsPresentNoLock(CareerSlot slot, List<string> entitledPackages)
            {
                if (slot == null || !slot.IsOccupied || entitledPackages == null || entitledPackages.Count <= 0)
                {
                    return false;
                }

                var packages = GetOrLoadCouponItemPackagesByTechnicalName();
                if (packages == null || packages.Count <= 0)
                {
                    return false;
                }

                if (slot.ItemPossessions == null)
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                var requiredByItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var appliedPackages = new List<string>();
                for (var i = 0; i < entitledPackages.Count; i++)
                {
                    var packageName = entitledPackages[i];
                    if (LocalUserStore.IsNullOrWhiteSpace(packageName))
                    {
                        continue;
                    }

                    List<string> items;
                    if (!packages.TryGetValue(packageName, out items) || items == null || items.Count <= 0)
                    {
                        continue;
                    }

                    appliedPackages.Add(packageName);
                    for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
                    {
                        var itemId = items[itemIndex];
                        if (LocalUserStore.IsNullOrWhiteSpace(itemId))
                        {
                            continue;
                        }

                        int required;
                        if (!requiredByItem.TryGetValue(itemId, out required) || required < 0)
                        {
                            required = 0;
                        }

                        if (required < int.MaxValue)
                        {
                            required++;
                        }

                        requiredByItem[itemId] = required;
                    }
                }

                var changed = false;
                foreach (var kvp in requiredByItem)
                {
                    var possessionKey = kvp.Key + "|0|-1";
                    var required = kvp.Value;
                    int existing;
                    if (!slot.ItemPossessions.TryGetValue(possessionKey, out existing) || existing < 0)
                    {
                        existing = 0;
                    }

                    if (existing >= required)
                    {
                        continue;
                    }

                    AddOwnedItemAmount(slot, kvp.Key, required - existing);
                    changed = true;
                }

                if (slot.AppliedCouponItemPackages == null)
                {
                    slot.AppliedCouponItemPackages = new List<string>();
                }

                for (var i = 0; i < appliedPackages.Count; i++)
                {
                    if (!ListContainsIgnoreCase(slot.AppliedCouponItemPackages, appliedPackages[i]))
                    {
                        slot.AppliedCouponItemPackages.Add(appliedPackages[i]);
                        changed = true;
                    }
                }

                return changed;
            }

            public List<string> GetCouponItemPackageEntitlementsForIdentityNoLock(string identityHash)
            {
                var packages = new List<string>();
                if (!LocalUserStore.IsGuidish(identityHash))
                {
                    return packages;
                }

                // Entitlements are persisted keys, independent of character profile construction.
                var playerInfo = _owner._playerInfoStore.Get(identityHash, CouponGameName);
                if (playerInfo == null)
                {
                    return packages;
                }

                string raw;
                if (!playerInfo.TryGetValue(CouponPackagesPlayerInfoKey, out raw) || LocalUserStore.IsNullOrWhiteSpace(raw))
                {
                    return packages;
                }

                var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var split = raw.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < split.Length; i++)
                {
                    var packageName = split[i] != null ? split[i].Trim() : null;
                    if (string.Equals(packageName, LegacyUnobtainableCosmeticsPackageName, StringComparison.OrdinalIgnoreCase))
                    {
                        packageName = "BONUSCOSMETICS";
                    }

                    if (LocalUserStore.IsNullOrWhiteSpace(packageName) || seen.ContainsKey(packageName) || !HonoredCouponCodes.IsHonored(packageName))
                    {
                        continue;
                    }

                    seen[packageName] = true;
                    packages.Add(packageName);
                }

                return packages;
            }

            private bool ApplyCouponItemPackageToCareerNoLock(CareerSlot slot, string packageTechnicalName)
            {
                if (slot == null || !slot.IsOccupied || LocalUserStore.IsNullOrWhiteSpace(packageTechnicalName))
                {
                    return false;
                }

                if (slot.AppliedCouponItemPackages == null)
                {
                    slot.AppliedCouponItemPackages = new List<string>();
                }

                var alreadyApplied = ListContainsIgnoreCase(slot.AppliedCouponItemPackages, packageTechnicalName);
                var packages = GetOrLoadCouponItemPackagesByTechnicalName();
                if (packages == null)
                {
                    return false;
                }

                List<string> items;
                if (!packages.TryGetValue(packageTechnicalName, out items) || items == null || items.Count <= 0)
                {
                    return false;
                }

                if (slot.ItemPossessions == null)
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                if (alreadyApplied)
                {
                    return false;
                }

                for (var i = 0; i < items.Count; i++)
                {
                    AddOwnedItemAmount(slot, items[i], 1);
                }

                slot.AppliedCouponItemPackages.Add(packageTechnicalName);
                return true;
            }

            private Dictionary<string, List<string>> GetOrLoadCouponItemPackagesByTechnicalName()
            {
                try
                {
                    var staticDataDir = _owner._options != null ? _owner._options.StaticDataDir : null;

                    lock (CouponItemPackageLock)
                    {
                        bool staticDataAvailable;
                        var sourceIdentity = GetCouponItemPackageSourceIdentity(staticDataDir, out staticDataAvailable);
                        if (CachedCouponItemPackages != null && string.Equals(CachedCouponItemPackagesSourceIdentity, sourceIdentity, StringComparison.OrdinalIgnoreCase))
                        {
                            return CachedCouponItemPackages;
                        }

                        // Built-in packages do not depend on extracted game data.
                        CachedCouponItemPackages = staticDataAvailable
                            ? LoadCouponItemPackagesByTechnicalName(staticDataDir)
                            : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        AddBuiltInCouponItemPackages(CachedCouponItemPackages);

                        CachedCouponItemPackagesSourceIdentity = sourceIdentity;
                        return CachedCouponItemPackages;
                    }
                }
                catch (Exception ex)
                {
                    LogCouponItemPackageDataError(ex);
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }
            }

            private static Dictionary<string, List<string>> LoadCouponItemPackagesByTechnicalName(string staticDataDir)
            {
                var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (LocalUserStore.IsNullOrWhiteSpace(staticDataDir))
                    {
                        return result;
                    }

                    var path = Path.Combine(staticDataDir, "serverData.json");
                    if (!File.Exists(path))
                    {
                        return result;
                    }

                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (LocalUserStore.IsNullOrWhiteSpace(json))
                    {
                        return result;
                    }

                    var packagePattern =
                        "\\\"TypeName\\\"\\s*:\\s*\\\"Cliffhanger\\.SRO\\.ServerClientCommons\\.Definitions\\.ItemPackageDefinition, Cliffhanger\\.SRO\\.ServerClientCommons\\\"" +
                        "\\s*,\\s*\\\"TechnicalName\\\"\\s*:\\s*\\\"(?<name>[^\\\"]+)\\\"" +
                        "\\s*,\\s*\\\"Items\\\"\\s*:\\s*\\[(?<items>.*?)\\]";

                    var packageRegex = new Regex(packagePattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    var itemRegex = new Regex("\\\"(?<item>[^\\\"]+)\\\"", RegexOptions.Singleline);

                    var matches = packageRegex.Matches(json);
                    for (var i = 0; i < matches.Count; i++)
                    {
                        var match = matches[i];
                        if (match == null)
                        {
                            continue;
                        }

                        var name = match.Groups["name"] != null ? match.Groups["name"].Value : null;
                        if (LocalUserStore.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        var itemsBlob = match.Groups["items"] != null ? match.Groups["items"].Value : null;
                        var items = new List<string>();
                        if (!LocalUserStore.IsNullOrWhiteSpace(itemsBlob))
                        {
                            var itemMatches = itemRegex.Matches(itemsBlob);
                            for (var itemIndex = 0; itemIndex < itemMatches.Count; itemIndex++)
                            {
                                var itemMatch = itemMatches[itemIndex];
                                var itemId = itemMatch != null && itemMatch.Groups["item"] != null ? itemMatch.Groups["item"].Value : null;
                                if (!LocalUserStore.IsNullOrWhiteSpace(itemId))
                                {
                                    items.Add(itemId);
                                }
                            }
                        }

                        if (items.Count > 0)
                        {
                            result[name] = items;
                        }
                    }
                }
                catch
                {
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                return result;
            }

            private static void AddBuiltInCouponItemPackages(Dictionary<string, List<string>> packages)
            {
                if (packages == null)
                {
                    return;
                }

                AddBuiltInCouponItemPackage(packages, "BONUSCOSMETICS", BonusCosmeticsPackageItems);
                AddBuiltInCouponItemPackage(packages, "HALLOWEEN", HalloweenPackageItems);
            }

            private static string GetCouponItemPackageSourceIdentity(string staticDataDir, out bool staticDataAvailable)
            {
                var identity = new StringBuilder();
                staticDataAvailable = !LocalUserStore.IsNullOrWhiteSpace(staticDataDir) && Directory.Exists(staticDataDir);
                if (!staticDataAvailable)
                {
                    identity.Append("|static-data:missing:").Append(staticDataDir ?? string.Empty);
                }
                else
                {
                    AppendFileIdentity(identity, Path.Combine(staticDataDir, "serverData.json"));
                }

                return identity.ToString();
            }

            private static void AppendFileIdentity(StringBuilder identity, string path)
            {
                identity.Append('|').Append(path);
                if (!File.Exists(path))
                {
                    identity.Append(":missing");
                    return;
                }

                var info = new FileInfo(path);
                identity.Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
            }

            private void LogCouponItemPackageDataError(Exception exception)
            {
                if (_owner == null || _owner._logger == null)
                {
                    return;
                }

                try
                {
                    _owner._logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        level = "warning",
                        type = "coupon-data",
                        op = "package-cache-unavailable",
                        error = exception != null ? exception.Message : "Unknown data error",
                    });
                }
                catch
                {
                }
            }

            private static void AddBuiltInCouponItemPackage(Dictionary<string, List<string>> packages, string packageTechnicalName, string[] itemIds)
            {
                if (packages == null || LocalUserStore.IsNullOrWhiteSpace(packageTechnicalName) || itemIds == null || itemIds.Length <= 0)
                {
                    return;
                }

                var items = new List<string>(itemIds.Length);
                for (var i = 0; i < itemIds.Length; i++)
                {
                    var itemId = itemIds[i];
                    if (!LocalUserStore.IsNullOrWhiteSpace(itemId))
                    {
                        items.Add(itemId);
                    }
                }

                if (items.Count > 0)
                {
                    packages[packageTechnicalName] = items;
                }
            }

            private static string NormalizeCouponCode(string value)
            {
                if (LocalUserStore.IsNullOrWhiteSpace(value))
                {
                    return string.Empty;
                }

                var sb = new StringBuilder(value.Length);
                for (var i = 0; i < value.Length; i++)
                {
                    var ch = value[i];
                    if (char.IsLetterOrDigit(ch))
                    {
                        sb.Append(char.ToUpperInvariant(ch));
                    }
                }

                return sb.ToString();
            }

            private static bool ListContainsIgnoreCase(List<string> list, string value)
            {
                if (list == null || LocalUserStore.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                for (var i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool EnsureCouponItemPackageItemsPresentNoLock(CareerSlot slot, List<string> items)
            {
                if (slot == null || items == null || items.Count <= 0)
                {
                    return false;
                }

                if (slot.ItemPossessions == null)
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                var requiredByItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < items.Count; i++)
                {
                    var itemId = items[i];
                    if (LocalUserStore.IsNullOrWhiteSpace(itemId))
                    {
                        continue;
                    }

                    int required;
                    if (!requiredByItem.TryGetValue(itemId, out required) || required < 0)
                    {
                        required = 0;
                    }

                    if (required < int.MaxValue)
                    {
                        required++;
                    }

                    requiredByItem[itemId] = required;
                }

                var changed = false;
                foreach (var kvp in requiredByItem)
                {
                    var possessionKey = kvp.Key + "|0|-1";
                    var required = kvp.Value;
                    int existing;
                    if (!slot.ItemPossessions.TryGetValue(possessionKey, out existing) || existing < 0)
                    {
                        existing = 0;
                    }

                    if (existing >= required)
                    {
                        continue;
                    }

                    AddOwnedItemAmount(slot, kvp.Key, required - existing);
                    changed = true;
                }

                return changed;
            }

            private static void AddOwnedItemAmount(CareerSlot slot, string itemId, int amount)
            {
                if (slot == null || LocalUserStore.IsNullOrWhiteSpace(itemId) || amount <= 0)
                {
                    return;
                }

                if (slot.ItemPossessions == null)
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }

                var possessionKey = itemId + "|0|-1";
                int existing;
                if (!slot.ItemPossessions.TryGetValue(possessionKey, out existing) || existing < 0)
                {
                    existing = 0;
                }

                if (existing > int.MaxValue - amount)
                {
                    slot.ItemPossessions[possessionKey] = int.MaxValue;
                    return;
                }

                slot.ItemPossessions[possessionKey] = existing + amount;
            }
        }
    }
}
