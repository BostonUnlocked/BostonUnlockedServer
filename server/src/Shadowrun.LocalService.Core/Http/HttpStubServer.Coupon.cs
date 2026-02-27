using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Shadowrun.LocalService.Core.Http
{
    public sealed partial class HttpStubServer
    {
        private const string CouponGameName = "SRO";
        private const string CouponUnlocksPlayerInfoKey = "CouponUnlocks";
        private const string CouponHistoryPlayerInfoKey = "CouponHistory";
        private static readonly Dictionary<string, string> CouponCodeToUnlock = CreateCouponCodeToUnlockMap();

        private HttpResponse TryServeCoupon(HttpRequest request)
        {
            if (request == null)
            {
                return null;
            }

            var path = request.Path ?? "/";
            if (!StartsWith(path, "/CouponSystem/"))
            {
                return null;
            }

            var normalizedPath = NormalizePathForRoute(path);
            if (string.Equals(normalizedPath, "/CouponSystem/Api/v1/Coupon/Redeem", StringComparison.OrdinalIgnoreCase))
            {
                return HandleCouponRedeem(request);
            }

            if (string.Equals(normalizedPath, "/CouponSystem/Api/v1/Coupon/Return", StringComparison.OrdinalIgnoreCase))
            {
                return HandleCouponReturn(request);
            }

            if (StartsWith(normalizedPath, "/CouponSystem/Api/v1/Accounts/") && EndsWith(normalizedPath, "/History"))
            {
                return HandleCouponHistory(normalizedPath);
            }

            return JsonResponse(200, new object[0]);
        }

        private HttpResponse HandleCouponRedeem(HttpRequest request)
        {
            var parameters = ParseUrlEncodedParameters(request);
            var accountId = GetParameter(parameters, "AccountId");
            var code = GetParameter(parameters, "Code");

            if (!IsGuidish(accountId) || IsNullOrWhiteSpace(code))
            {
                return JsonResponse(200, BuildRedeemCouponResult(1, "InvalidRequest", null, null));
            }

            string packageTechnicalName;
            if (!TryResolveCouponPackageTechnicalName(code, out packageTechnicalName))
            {
                return JsonResponse(200, BuildRedeemCouponResult(2, "InvalidCoupon", null, null));
            }

            var history = LoadCouponHistory(accountId);
            IDictionary existing;
            if (TryFindActiveCouponByCode(history, code, out existing))
            {
                var existingTechnicalName = GetString(existing, "PackageTechnicalName") ?? packageTechnicalName;
                return JsonResponse(200, BuildRedeemCouponResult(3, "AlreadyRedeemed", GetCouponPackageDisplayName(existingTechnicalName), existingTechnicalName));
            }

            var unlocks = LoadCouponUnlocks(accountId);
            if (!ContainsIgnoreCase(unlocks, packageTechnicalName))
            {
                unlocks.Add(packageTechnicalName);
            }

            var now = DateTime.UtcNow;
            history.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "CouponCode", code },
                { "TimeRedeemed", now },
                { "TimeClaimed", now },
                { "TimeReturned", null },
                { "PackageTechnicalName", packageTechnicalName },
                { "PackageName", GetCouponPackageDisplayName(packageTechnicalName) },
            });

            SaveCouponState(accountId, unlocks, history);
            return JsonResponse(200, BuildRedeemCouponResult(0, "OK", GetCouponPackageDisplayName(packageTechnicalName), packageTechnicalName));
        }

        private HttpResponse HandleCouponHistory(string normalizedPath)
        {
            var identityHash = ExtractCouponHistoryIdentityHash(normalizedPath);
            if (!IsGuidish(identityHash))
            {
                return JsonResponse(200, new object[0]);
            }

            var history = LoadCouponHistory(identityHash);
            return JsonResponse(200, history.ToArray());
        }

        private HttpResponse HandleCouponReturn(HttpRequest request)
        {
            var parameters = ParseUrlEncodedParameters(request);
            var sessionHash = GetParameter(parameters, "AccountSystemSessionHash");
            var code = GetParameter(parameters, "Code");

            if (IsNullOrWhiteSpace(sessionHash) || IsNullOrWhiteSpace(code))
            {
                return JsonResponse(200, BuildReturnCouponResult(1, "InvalidRequest"));
            }

            string identityHash;
            if (!TryResolveIdentityForSessionHash(sessionHash, out identityHash) || !IsGuidish(identityHash))
            {
                return JsonResponse(200, BuildReturnCouponResult(2, "IdentityNotFound"));
            }

            var history = LoadCouponHistory(identityHash);
            IDictionary matched;
            if (!TryFindActiveCouponByCode(history, code, out matched))
            {
                return JsonResponse(200, BuildReturnCouponResult(3, "CouponNotFound"));
            }

            matched["TimeReturned"] = DateTime.UtcNow;

            var packageTechnicalName = GetString(matched, "PackageTechnicalName");
            var unlocks = LoadCouponUnlocks(identityHash);
            if (!IsNullOrWhiteSpace(packageTechnicalName) && !HasAnyActiveCouponForPackage(history, packageTechnicalName))
            {
                RemoveIgnoreCase(unlocks, packageTechnicalName);
            }

            SaveCouponState(identityHash, unlocks, history);
            return JsonResponse(200, BuildReturnCouponResult(0, "OK"));
        }

        private static Dictionary<string, string> CreateCouponCodeToUnlockMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            AddCouponAlias(map, "SRO-Addon1", "SRO-Addon1");
            AddCouponAlias(map, "SRO-ADDON1", "SRO-Addon1");
            AddCouponAlias(map, "SRO_ADDON1", "SRO-Addon1");
            AddCouponAlias(map, "ADDON1", "SRO-Addon1");
            AddCouponAlias(map, "DLC1", "SRO-Addon1");
            AddCouponAlias(map, "SRO-STEAM-DLC-1", "SRO-Addon1");

            AddCouponAlias(map, "SRO-Addon2", "SRO-Addon2");
            AddCouponAlias(map, "SRO-ADDON2", "SRO-Addon2");
            AddCouponAlias(map, "SRO_ADDON2", "SRO-Addon2");
            AddCouponAlias(map, "ADDON2", "SRO-Addon2");
            AddCouponAlias(map, "DLC2", "SRO-Addon2");
            AddCouponAlias(map, "SRO-STEAM-DLC-2", "SRO-Addon2");

            return map;
        }

        private static void AddCouponAlias(Dictionary<string, string> map, string alias, string technicalName)
        {
            if (map == null || IsNullOrWhiteSpace(alias) || IsNullOrWhiteSpace(technicalName))
            {
                return;
            }

            map[alias] = technicalName;
            map[NormalizeCouponCode(alias)] = technicalName;
        }

        private static string GetCouponPackageDisplayName(string packageTechnicalName)
        {
            if (string.Equals(packageTechnicalName, "SRO-Addon1", StringComparison.OrdinalIgnoreCase))
            {
                return "SRO Addon 1";
            }

            if (string.Equals(packageTechnicalName, "SRO-Addon2", StringComparison.OrdinalIgnoreCase))
            {
                return "SRO Addon 2";
            }

            return packageTechnicalName;
        }

        private static bool TryResolveCouponPackageTechnicalName(string code, out string packageTechnicalName)
        {
            packageTechnicalName = null;
            if (IsNullOrWhiteSpace(code))
            {
                return false;
            }

            if (CouponCodeToUnlock.TryGetValue(code, out packageTechnicalName) && !IsNullOrWhiteSpace(packageTechnicalName))
            {
                return true;
            }

            var normalized = NormalizeCouponCode(code);
            return CouponCodeToUnlock.TryGetValue(normalized, out packageTechnicalName) && !IsNullOrWhiteSpace(packageTechnicalName);
        }

        private static string NormalizeCouponCode(string value)
        {
            if (IsNullOrWhiteSpace(value))
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

        private static Dictionary<string, object> BuildRedeemCouponResult(int code, string message, string packageName, string packageTechnicalName)
        {
            return new Dictionary<string, object>
            {
                { "Code", code },
                { "Message", message },
                { "PackageName", packageName },
                { "PackageTechnicalName", packageTechnicalName },
            };
        }

        private static Dictionary<string, object> BuildReturnCouponResult(int code, string message)
        {
            return new Dictionary<string, object>
            {
                { "Code", code },
                { "Message", message },
            };
        }

        private static string NormalizePathForRoute(string path)
        {
            if (IsNullOrWhiteSpace(path))
            {
                return "/";
            }

            var normalized = path.Trim();
            while (normalized.Length > 1 && normalized[normalized.Length - 1] == '/')
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }

            return normalized;
        }

        private static string ExtractCouponHistoryIdentityHash(string normalizedPath)
        {
            const string prefix = "/CouponSystem/Api/v1/Accounts/";
            const string suffix = "/History";
            if (!StartsWith(normalizedPath, prefix) || !EndsWith(normalizedPath, suffix))
            {
                return null;
            }

            var length = normalizedPath.Length - prefix.Length - suffix.Length;
            if (length <= 0)
            {
                return null;
            }

            var segment = normalizedPath.Substring(prefix.Length, length);
            return UrlDecodeComponent(segment);
        }

        private static Dictionary<string, string> ParseUrlEncodedParameters(HttpRequest request)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request == null)
            {
                return parameters;
            }

            ParseUrlEncodedInto(parameters, request.Query);

            if (request.BodyBytes != null && request.BodyBytes.Length > 0)
            {
                string body = null;
                try
                {
                    body = Encoding.UTF8.GetString(request.BodyBytes);
                }
                catch
                {
                    body = null;
                }
                ParseUrlEncodedInto(parameters, body);
            }

            return parameters;
        }

        private static void ParseUrlEncodedInto(Dictionary<string, string> parameters, string encoded)
        {
            if (parameters == null || IsNullOrWhiteSpace(encoded))
            {
                return;
            }

            var payload = encoded.Trim();
            if (payload.StartsWith("?", StringComparison.Ordinal))
            {
                payload = payload.Substring(1);
            }

            if (IsNullOrWhiteSpace(payload) || payload.IndexOf('=') < 0)
            {
                return;
            }

            var parts = payload.Split('&');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                var eq = part.IndexOf('=');
                var rawKey = eq >= 0 ? part.Substring(0, eq) : part;
                var rawValue = eq >= 0 && eq + 1 < part.Length ? part.Substring(eq + 1) : string.Empty;

                var key = UrlDecodeComponent(rawKey);
                if (IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                parameters[key] = UrlDecodeComponent(rawValue);
            }
        }

        private static string UrlDecodeComponent(string value)
        {
            if (value == null)
            {
                return null;
            }

            var adjusted = value.Replace('+', ' ');
            try
            {
                return Uri.UnescapeDataString(adjusted);
            }
            catch
            {
                return adjusted;
            }
        }

        private static string GetParameter(Dictionary<string, string> parameters, string key)
        {
            if (parameters == null || IsNullOrWhiteSpace(key))
            {
                return null;
            }

            string value;
            if (parameters.TryGetValue(key, out value))
            {
                return value;
            }

            return null;
        }

        private bool TryResolveIdentityForSessionHash(string sessionHash, out string identityHash)
        {
            identityHash = null;
            if (IsNullOrWhiteSpace(sessionHash))
            {
                return false;
            }

            try
            {
                if (_sessionIdentityMap != null && _sessionIdentityMap.TryGetIdentityForSession(sessionHash, out identityHash) && !IsNullOrWhiteSpace(identityHash))
                {
                    return true;
                }
            }
            catch
            {
                identityHash = null;
            }

            try
            {
                if (_userStore != null && _userStore.TryGetIdentityForSession(sessionHash, out identityHash) && !IsNullOrWhiteSpace(identityHash))
                {
                    return true;
                }
            }
            catch
            {
                identityHash = null;
            }

            return false;
        }

        private List<string> LoadCouponUnlocks(string identityHash)
        {
            var unlocks = new List<string>();
            if (_userStore == null || !IsGuidish(identityHash))
            {
                return unlocks;
            }

            var playerInfo = _userStore.GetPlayerInfo(identityHash, CouponGameName);
            if (playerInfo == null)
            {
                return unlocks;
            }

            string raw;
            if (!playerInfo.TryGetValue(CouponUnlocksPlayerInfoKey, out raw) || IsNullOrWhiteSpace(raw))
            {
                return unlocks;
            }

            var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var parts = raw.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var value = parts[i] != null ? parts[i].Trim() : null;
                if (IsNullOrWhiteSpace(value) || seen.ContainsKey(value))
                {
                    continue;
                }
                seen[value] = true;
                unlocks.Add(value);
            }

            return unlocks;
        }

        private List<Dictionary<string, object>> LoadCouponHistory(string identityHash)
        {
            var history = new List<Dictionary<string, object>>();
            if (_userStore == null || !IsGuidish(identityHash))
            {
                return history;
            }

            var playerInfo = _userStore.GetPlayerInfo(identityHash, CouponGameName);
            if (playerInfo == null)
            {
                return history;
            }

            string raw;
            if (!playerInfo.TryGetValue(CouponHistoryPlayerInfoKey, out raw) || IsNullOrWhiteSpace(raw))
            {
                return history;
            }

            try
            {
                var parsed = Json.DeserializeObject(raw) as Array;
                if (parsed == null)
                {
                    return history;
                }

                for (var i = 0; i < parsed.Length; i++)
                {
                    var dict = parsed.GetValue(i) as IDictionary;
                    if (dict == null)
                    {
                        continue;
                    }

                    var entry = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (DictionaryEntry kvp in dict)
                    {
                        var key = kvp.Key as string;
                        if (IsNullOrWhiteSpace(key))
                        {
                            continue;
                        }
                        entry[key] = kvp.Value;
                    }

                    if (entry.Count > 0)
                    {
                        history.Add(entry);
                    }
                }
            }
            catch
            {
            }

            return history;
        }

        private void SaveCouponState(string identityHash, List<string> unlocks, List<Dictionary<string, object>> history)
        {
            if (_userStore == null || !IsGuidish(identityHash))
            {
                return;
            }

            unlocks = unlocks ?? new List<string>();
            history = history ?? new List<Dictionary<string, object>>();

            unlocks.Sort(StringComparer.OrdinalIgnoreCase);
            var update = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { CouponUnlocksPlayerInfoKey, string.Join(";", unlocks.ToArray()) },
                { CouponHistoryPlayerInfoKey, Json.Serialize(history.ToArray()) },
            };

            _userStore.SetPlayerInfo(identityHash, CouponGameName, update);
        }

        private static bool TryFindActiveCouponByCode(List<Dictionary<string, object>> history, string couponCode, out IDictionary entry)
        {
            entry = null;
            if (history == null || IsNullOrWhiteSpace(couponCode))
            {
                return false;
            }

            for (var i = history.Count - 1; i >= 0; i--)
            {
                var candidate = history[i];
                if (candidate == null)
                {
                    continue;
                }

                var savedCode = GetString(candidate, "CouponCode");
                if (!string.Equals(savedCode, couponCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var returned = candidate.ContainsKey("TimeReturned") ? candidate["TimeReturned"] : null;
                if (returned == null || IsNullOrWhiteSpace(returned as string))
                {
                    entry = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool HasAnyActiveCouponForPackage(List<Dictionary<string, object>> history, string packageTechnicalName)
        {
            if (history == null || IsNullOrWhiteSpace(packageTechnicalName))
            {
                return false;
            }

            for (var i = 0; i < history.Count; i++)
            {
                var entry = history[i];
                if (entry == null)
                {
                    continue;
                }

                var candidate = GetString(entry, "PackageTechnicalName");
                if (!string.Equals(candidate, packageTechnicalName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var returned = entry.ContainsKey("TimeReturned") ? entry["TimeReturned"] : null;
                if (returned == null || IsNullOrWhiteSpace(returned as string))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsIgnoreCase(List<string> values, string value)
        {
            if (values == null || IsNullOrWhiteSpace(value))
            {
                return false;
            }

            for (var i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveIgnoreCase(List<string> values, string value)
        {
            if (values == null || IsNullOrWhiteSpace(value))
            {
                return;
            }

            for (var i = values.Count - 1; i >= 0; i--)
            {
                if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    values.RemoveAt(i);
                }
            }
        }

        private static bool IsGuidish(string value)
        {
            if (IsNullOrWhiteSpace(value))
            {
                return false;
            }

            try
            {
                var ignored = new Guid(value);
                return ignored != Guid.Empty;
            }
            catch
            {
                return false;
            }
        }
    }
}
