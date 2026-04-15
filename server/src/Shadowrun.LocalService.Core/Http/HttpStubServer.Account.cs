using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Shadowrun.LocalService.Core.Protocols;

namespace Shadowrun.LocalService.Core.Http
{
    public sealed partial class HttpStubServer
    {
        private HttpResponse TryServeAccount(string path, byte[] bodyBytes)
        {
            if (!StartsWith(path, "/AccountSystem/"))
            {
                return null;
            }

            if (EndsWith(path, "/Accounts/Steam/Authenticate"))
            {
                var session = Guid.NewGuid();

                ulong steamId = 0;
                string identity = null;

                try
                {
                    var dict = TryParseJsonDictionary(bodyBytes);
                    var ticketHex = GetString(dict, "Ticket");
                    if (TryExtractSteamId64FromAuthTicketHex(ticketHex, out steamId))
                    {
                        if (_userStore != null)
                        {
                            identity = _userStore.GetOrCreateIdentityHashForSteamId(steamId);
                        }

                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "http-steam-auth",
                            accountId = identity,
                            steamId64 = steamId.ToString(CultureInfo.InvariantCulture),
                            ticketHexLen = ticketHex != null ? ticketHex.Length : 0,
                        });
                    }
                }
                catch
                {
                    steamId = 0;
                    identity = null;
                }

                if (steamId == 0 || IsNullOrWhiteSpace(identity))
                {
                    return JsonResponse(401, new Dictionary<string, object>
                    {
                        { "Code", 1 },
                        { "Message", "SteamError" },
                        { "SessionHash", Guid.Empty.ToString() },
                        {
                            "SteamError",
                            new Dictionary<string, object>
                            {
                                { "Code", 1 },
                                { "Description", "Missing or invalid Steam auth ticket (SteamID64 not found)." },
                            }
                        },
                    });
                }

                try { _sessionIdentityMap.SetIdentityForSession(session.ToString(), identity); } catch { }
                return JsonResponse(200, new Dictionary<string, object>
                {
                    { "Code", 0 },
                    { "Message", "OK" },
                    { "SessionHash", session.ToString() },
                    {
                        "SteamError",
                        new Dictionary<string, object>
                        {
                            { "Code", 0 },
                            { "Description", "OK" },
                        }
                    },
                });
            }

            if (EndsWith(path, "/Accounts/Cliffhanger/Authenticate"))
            {
                string email = null;
                string password = null;
                try
                {
                    var dict = TryParseJsonDictionary(bodyBytes);
                    email = GetString(dict, "Email");
                    password = GetString(dict, "Password");
                }
                catch
                {
                }

                string identity = null;
                bool isVerified = false;
                string authMessage = null;
                var ok = _userStore != null
                    && _userStore.TryAuthenticateCliffhangerCredentials(email, password, out identity, out isVerified, out authMessage)
                    && !IsNullOrWhiteSpace(identity);

                if (!ok)
                {
                    var loginMessage = IsNullOrWhiteSpace(authMessage) ? "IncorrectPassword" : authMessage;
                    if (string.Equals(loginMessage, "InvalidCredentials", StringComparison.OrdinalIgnoreCase))
                    {
                        loginMessage = "IncorrectPassword";
                    }

                    var loginStatusCode = GetAuthFailureStatusCode(loginMessage, 500);

                    return JsonResponse(loginStatusCode, new Dictionary<string, object>
                    {
                        { "Code", 1 },
                        { "Message", loginMessage },
                        { "SessionHash", Guid.Empty.ToString() },
                        { "IsVerified", false },
                    });
                }

                var session = Guid.NewGuid();
                try { _sessionIdentityMap.SetIdentityForSession(session.ToString(), identity); } catch { }

                return JsonResponse(200, new Dictionary<string, object>
                {
                    { "Code", 0 },
                    { "Message", "OK" },
                    { "SessionHash", session.ToString() },
                    { "IsVerified", isVerified },
                });
            }

            if (EndsWith(path, "/Accounts/Cliffhanger/Register"))
            {
                string email = null;
                string password = null;
                string tag = null;
                try
                {
                    var dict = TryParseJsonDictionary(bodyBytes);
                    email = GetString(dict, "Email");
                    password = GetString(dict, "Password");
                    tag = GetString(dict, "Tag");
                }
                catch
                {
                }

                string identity = null;
                string registerMessage = null;
                var ok = _userStore != null
                    && _userStore.TryRegisterCliffhangerCredentials(email, password, tag, out identity, out registerMessage);

                var registerMessageNormalized = ok ? "OK" : (IsNullOrWhiteSpace(registerMessage) ? "RegisterFailed" : registerMessage);
                var registerStatusCode = ok ? 200 : GetAuthFailureStatusCode(registerMessageNormalized, 500);

                return JsonResponse(registerStatusCode, new Dictionary<string, object>
                {
                    { "Code", ok ? 0 : 1 },
                    { "Message", registerMessageNormalized },
                    { "Success", ok },
                });
            }

            if (EndsWith(path, "/Accounts/Cliffhanger/Verify"))
            {
                return JsonResponse(401, new Dictionary<string, object>
                {
                    { "Code", 1 },
                    { "Message", "IncorrectPassword" },
                    { "SessionHash", Guid.Empty.ToString() },
                    { "IsVerified", false },
                });
            }

            if (EndsWith(path, "/Accounts/Cliffhanger/RequestPasswordReset"))
            {
                return JsonResponse(501, new Dictionary<string, object>
                {
                    { "Code", 1 },
                    { "Message", "NotSupported" },
                    { "Success", false },
                });
            }

            if (EndsWith(path, "/Accounts/GetAccountForHash"))
            {
                string identity = null;
                string sessionHash = null;
                try
                {
                    var dict = TryParseJsonDictionary(bodyBytes);
                    sessionHash = GetString(dict, "SessionHash");
                }
                catch
                {
                }

                try
                {
                    string mapped;
                    if (!IsNullOrWhiteSpace(sessionHash) && _sessionIdentityMap.TryGetIdentityForSession(sessionHash, out mapped) && !IsNullOrWhiteSpace(mapped))
                    {
                        identity = mapped;
                    }
                }
                catch
                {
                }

                if (IsNullOrWhiteSpace(sessionHash) || IsNullOrWhiteSpace(identity))
                {
                    return JsonResponse(401, new Dictionary<string, object>
                    {
                        { "IdentityHash", Guid.Empty.ToString() },
                        { "ApplicationKeyName", "SRO-GAME-KEY" },
                        { "GameName", "SRO" },
                        { "IsGameBorrowed", false },
                        { "Code", 1 },
                        { "Message", "IdentityNotFound" },
                    });
                }

                return JsonResponse(200, new Dictionary<string, object>
                {
                    { "IdentityHash", identity },
                    { "ApplicationKeyName", "SRO-GAME-KEY" },
                    { "GameName", "SRO" },
                    { "IsGameBorrowed", false },
                    { "Code", 0 },
                    { "Message", "OK" },
                });
            }

            if (EndsWith(path, "/Accounts/PlayerActivity/GetPlayerInfo"))
            {
                var requestedIdentityHashes = ParseRequestedIdentityHashes(bodyBytes);
                if (requestedIdentityHashes.Count == 0)
                {
                    return JsonResponse(200, new Dictionary<string, object>
                    {
                        { "PlayerInfoResults", new object[0] },
                        { "Code", 1 },
                        { "Message", "IdentityNotFound" },
                    });
                }

                var requestedKeys = ParseRequestedKeys(bodyBytes);
                var requestedGameName = ParseRequestedGameName(bodyBytes);
                if (IsNullOrWhiteSpace(requestedGameName))
                {
                    requestedGameName = "SRO";
                }

                var distinct = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var results = new List<object>();

                foreach (var identityHash in requestedIdentityHashes)
                {
                    if (IsNullOrWhiteSpace(identityHash))
                    {
                        continue;
                    }
                    if (distinct.ContainsKey(identityHash))
                    {
                        continue;
                    }
                    distinct[identityHash] = true;

                    Guid parsedIdentity;
                    try { parsedIdentity = new Guid(identityHash); }
                    catch { parsedIdentity = Guid.Empty; }
                    if (parsedIdentity == Guid.Empty)
                    {
                        results.Add(new Dictionary<string, object>
                        {
                            { "IdentityHash", identityHash },
                            { "PlayerInfo", new object[0] },
                            { "Code", 0 },
                            { "Message", "OK" },
                        });
                        continue;
                    }

                    var stored = _playerInfoRepository.Get(identityHash, requestedGameName);
                    SanitizeStoredDisplayNames(identityHash, stored);
                    if (stored != null && _userStore != null && !stored.ContainsKey("LauncherDisplayName"))
                    {
                        stored["LauncherDisplayName"] = _userStore.GetDisplayName(identityHash);
                    }
                    var responseInfo = BuildPlayerInfoResponse(stored, requestedKeys, identityHash);

                    results.Add(new Dictionary<string, object>
                    {
                        { "IdentityHash", identityHash },
                        { "PlayerInfo", responseInfo },
                        { "Code", 0 },
                        { "Message", "OK" },
                    });
                }

                try
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "http-playerinfo",
                        accountId = requestedIdentityHashes.Count == 1 ? requestedIdentityHashes[0] : null,
                        path = "/AccountSystem/Accounts/PlayerActivity/GetPlayerInfo",
                        requested = requestedIdentityHashes != null ? requestedIdentityHashes.ToArray() : new string[0],
                        returned = results.Count,
                    });
                }
                catch
                {
                }

                return JsonResponse(200, new Dictionary<string, object>
                {
                    { "PlayerInfoResults", results.ToArray() },
                    { "Code", 0 },
                    { "Message", "OK" },
                });
            }

            if (EndsWith(path, "/Accounts/PlayerActivity/SearchByPlayer"))
            {
                var dict = TryParseJsonDictionary(bodyBytes);
                var requestedSearchString = GetString(dict, "SearchString");
                var requestedKeys = ParseRequestedKeys(bodyBytes);
                var requestedGameName = ParseRequestedGameName(bodyBytes);
                if (IsNullOrWhiteSpace(requestedGameName))
                {
                    requestedGameName = "SRO";
                }

                var matches = _playerInfoRepository != null
                    ? _playerInfoRepository.Search(requestedGameName, requestedSearchString)
                    : new List<KeyValuePair<string, Dictionary<string, string>>>();

                var results = new List<object>();
                for (var i = 0; i < matches.Count; i++)
                {
                    var identityHash = matches[i].Key;
                    var stored = matches[i].Value ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    SanitizeStoredDisplayNames(identityHash, stored);
                    if (_userStore != null && !stored.ContainsKey("LauncherDisplayName"))
                    {
                        stored["LauncherDisplayName"] = _userStore.GetDisplayName(identityHash);
                    }

                    results.Add(new Dictionary<string, object>
                    {
                        { "IdentityHash", identityHash },
                        { "PlayerInfo", BuildPlayerInfoResponse(stored, requestedKeys, identityHash) },
                        { "Code", 0 },
                        { "Message", "OK" },
                    });
                }

                try
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "http-playerinfo-search",
                        path = "/AccountSystem/Accounts/PlayerActivity/SearchByPlayer",
                        search = requestedSearchString,
                        gameName = requestedGameName,
                        returned = results.Count,
                    });
                }
                catch
                {
                }

                return JsonResponse(200, new Dictionary<string, object>
                {
                    { "PlayerInfoResults", results.ToArray() },
                    { "Code", 0 },
                    { "Message", "OK" },
                });
            }

            if (EndsWith(path, "/Accounts/PlayerActivity/SetPlayerInfo"))
            {
                var dict = TryParseJsonDictionary(bodyBytes);
                var gameName = GetString(dict, "GameName");
                if (IsNullOrWhiteSpace(gameName))
                {
                    gameName = "SRO";
                }

                var identityHash = GetString(dict, "IdentityHash");
                if (IsNullOrWhiteSpace(identityHash))
                {
                    var sessionHash = GetString(dict, "SessionHash");
                    string mappedIdentity;
                    if (!IsNullOrWhiteSpace(sessionHash) && _sessionIdentityMap.TryGetIdentityForSession(sessionHash, out mappedIdentity))
                    {
                        identityHash = mappedIdentity;
                    }
                }
                if (IsNullOrWhiteSpace(identityHash))
                {
                    return JsonResponse(200, new Dictionary<string, object>
                    {
                        { "Added", new Dictionary<string, string>() },
                        { "Updated", new Dictionary<string, string>() },
                        { "Deleted", new string[0] },
                        { "Code", 1 },
                        { "Message", "IdentityNotFound" },
                    });
                }

                var playerInfoUpdates = ParsePlayerInfoUpdates(dict);
                SanitizePlayerInfoUpdates(identityHash, playerInfoUpdates);
                TryValidatePlayerCharacterBlob(identityHash, gameName, playerInfoUpdates);
                ApplyRuntimeAuthoritativePlayerStatus(identityHash, gameName, playerInfoUpdates, "setplayerinfo", true);

                string displayName;
                if (!playerInfoUpdates.ContainsKey("LauncherDisplayName") && playerInfoUpdates.TryGetValue("DisplayName", out displayName))
                {
                    var launcherDisplayName = displayName;
                    if (!IsNullOrWhiteSpace(launcherDisplayName))
                    {
                        var semi = launcherDisplayName.IndexOf(';');
                        if (semi > 0)
                        {
                            launcherDisplayName = launcherDisplayName.Substring(0, semi);
                        }
                    }
                    playerInfoUpdates["LauncherDisplayName"] = launcherDisplayName;
                }

                if (playerInfoUpdates.TryGetValue("DisplayName", out displayName) && !IsNullOrWhiteSpace(displayName))
                {
                    var semi = displayName.IndexOf(';');
                    if (semi > 0 && semi + 1 < displayName.Length)
                    {
                        var incomingCharacterName = displayName.Substring(semi + 1).Trim();
                        if (!IsNullOrWhiteSpace(incomingCharacterName))
                        {
                            string authoritativeCharacterName = null;
                            var authoritativeCareerIndex = -1;

                            if (_userStore != null)
                            {
                                try
                                {
                                    authoritativeCareerIndex = _userStore.GetLastCareerIndex(identityHash);
                                    var slot = _userStore.GetOrCreateCareer(identityHash, authoritativeCareerIndex, false);
                                    if (slot != null && !IsNullOrWhiteSpace(slot.CharacterName))
                                    {
                                        authoritativeCharacterName = slot.CharacterName.Trim();
                                    }
                                }
                                catch
                                {
                                }
                            }

                            if (!IsNullOrWhiteSpace(authoritativeCharacterName)
                                && !string.Equals(authoritativeCharacterName, incomingCharacterName, StringComparison.OrdinalIgnoreCase))
                            {
                                string launcherDisplayName;
                                if (!playerInfoUpdates.TryGetValue("LauncherDisplayName", out launcherDisplayName) || IsNullOrWhiteSpace(launcherDisplayName))
                                {
                                    launcherDisplayName = ResolvePreferredAccountDisplayName(identityHash);
                                    playerInfoUpdates["LauncherDisplayName"] = launcherDisplayName;
                                }

                                playerInfoUpdates["DisplayName"] = launcherDisplayName + ";" + authoritativeCharacterName;

                                if (playerInfoUpdates.ContainsKey("CharacterName"))
                                {
                                    playerInfoUpdates["CharacterName"] = authoritativeCharacterName;
                                }

                                var removedPlayerCharacter = false;
                                if (playerInfoUpdates.ContainsKey("PlayerCharacter"))
                                {
                                    playerInfoUpdates.Remove("PlayerCharacter");
                                    removedPlayerCharacter = true;
                                }

                                _logger.LogLow(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "playerinfo-character-suffix-mismatch",
                                    identityHash = identityHash,
                                    gameName = gameName,
                                    incomingCharacterName = incomingCharacterName,
                                    authoritativeCharacterName = authoritativeCharacterName,
                                    authoritativeCareerIndex = authoritativeCareerIndex,
                                    removedPlayerCharacter = removedPlayerCharacter,
                                    action = "normalized-to-authoritative-career",
                                    reason = "setplayerinfo-displayname-non-authoritative",
                                });
                            }
                            else
                            {
                                _logger.LogLow(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "playerinfo-character-suffix-observed",
                                    identityHash = identityHash,
                                    gameName = gameName,
                                    incomingCharacterName = incomingCharacterName,
                                    authoritativeCharacterName = authoritativeCharacterName,
                                    authoritativeCareerIndex = authoritativeCareerIndex,
                                    action = "no-normalization",
                                });
                            }
                        }
                    }
                }

                var changes = _playerInfoRepository.Set(identityHash, gameName, playerInfoUpdates);

                return JsonResponse(200, new Dictionary<string, object>
                {
                    { "Added", changes.Added },
                    { "Updated", changes.Updated },
                    { "Deleted", changes.Deleted },
                    { "Code", 0 },
                    { "Message", "OK" },
                });
            }

            if (EndsWith(path, "/Accounts/Sessions/Heartbeat"))
            {
                return TextResponse(200, "true", "application/json; charset=utf-8");
            }

            return JsonResponse(500, new Dictionary<string, object>
            {
                { "Code", 1 },
                { "Message", "UnhandledAccountPath" },
                { "ok", false },
                { "offlineStub", true },
                { "path", path },
            });
        }

        private static int GetAuthFailureStatusCode(string message, int fallback)
        {
            if (IsNullOrWhiteSpace(message))
            {
                return fallback;
            }

            if (string.Equals(message, "IncorrectPassword", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "WrongPassword", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "InvalidCredentials", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "AccountNotFound", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "IdentityNotFound", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "SteamError", StringComparison.OrdinalIgnoreCase))
            {
                return 401;
            }

            if (string.Equals(message, "EmailAlreadyRegistered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "NotUnique", StringComparison.OrdinalIgnoreCase))
            {
                return 409;
            }

            if (string.Equals(message, "InvalidEmail", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "InvalidPassword", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "InvalidRequest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "CodeInvalid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "InvalidResetCode", StringComparison.OrdinalIgnoreCase))
            {
                return 400;
            }

            if (string.Equals(message, "NotSupported", StringComparison.OrdinalIgnoreCase))
            {
                return 501;
            }

            return fallback;
        }

        private static string ParseRequestedGameName(byte[] bodyBytes)
        {
            try
            {
                var dict = TryParseJsonDictionary(bodyBytes);
                return GetString(dict, "GameName");
            }
            catch
            {
                return null;
            }
        }

        private static List<string> ParseRequestedKeys(byte[] bodyBytes)
        {
            var results = new List<string>();
            if (bodyBytes == null || bodyBytes.Length == 0)
            {
                return results;
            }

            try
            {
                var dict = TryParseJsonDictionary(bodyBytes);
                if (dict == null || !dict.Contains("Keys"))
                {
                    return results;
                }

                var keysObj = dict["Keys"];
                var arr = keysObj as Array;
                if (arr == null)
                {
                    return results;
                }

                foreach (var entry in arr)
                {
                    var s = entry as string;
                    if (!IsNullOrWhiteSpace(s))
                    {
                        results.Add(s);
                    }
                }
            }
            catch
            {
                return results;
            }

            return results;
        }

        private object[] BuildPlayerInfoResponse(Dictionary<string, string> stored, List<string> requestedKeys, string identityHash)
        {
            if (stored == null)
            {
                stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            SanitizeStoredDisplayNames(identityHash, stored);
            ApplyRuntimeAuthoritativePlayerStatus(identityHash, "SRO", stored, "buildplayerinforesponse", false);

            if (!stored.ContainsKey("LauncherDisplayName"))
            {
                stored["LauncherDisplayName"] = BuildStableDisplayName(identityHash);
            }

            var items = new List<object>();

            if (requestedKeys == null || requestedKeys.Count == 0)
            {
                foreach (var kvp in stored)
                {
                    items.Add(new Dictionary<string, object> { { "Key", kvp.Key }, { "Value", kvp.Value } });
                }
                return items.ToArray();
            }

            foreach (var key in requestedKeys)
            {
                if (IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                string value;
                if (stored.TryGetValue(key, out value))
                {
                    items.Add(new Dictionary<string, object> { { "Key", key }, { "Value", value } });
                    continue;
                }

                if (string.Equals(key, "LauncherDisplayName", StringComparison.OrdinalIgnoreCase))
                {
                    if (stored.TryGetValue("DisplayName", out value) && !IsNullOrWhiteSpace(value))
                    {
                        var semi = value.IndexOf(';');
                        if (semi > 0)
                        {
                            value = value.Substring(0, semi);
                        }
                        items.Add(new Dictionary<string, object> { { "Key", key }, { "Value", value } });
                    }
                }
            }

            return items.ToArray();
        }

        private void ApplyRuntimeAuthoritativePlayerStatus(string identityHash, string gameName, Dictionary<string, string> playerInfo, string source, bool logOverrides)
        {
            if (playerInfo == null)
            {
                return;
            }

            Guid accountId;
            try
            {
                accountId = new Guid(identityHash);
            }
            catch
            {
                return;
            }

            if (accountId == Guid.Empty)
            {
                return;
            }

            var runtimeOnline = AccountTransportLivenessRegistry.IsOnline(accountId);
            var hasHubPresence = false;
            if (_hubPresenceRegistry != null)
            {
                HubPresenceRegistry.Participant participant;
                hasHubPresence = _hubPresenceRegistry.TryGetParticipantForAccount(accountId, out participant);
            }

            var runtimeInMission = false;
            if (runtimeOnline)
            {
                var missionParticipants = MissionRuntimeRegistry.SnapshotParticipants();
                if (missionParticipants != null)
                {
                    for (var i = 0; i < missionParticipants.Length; i++)
                    {
                        if (missionParticipants[i].AccountId == accountId)
                        {
                            runtimeInMission = true;
                            break;
                        }
                    }
                }
            }

            string previousOnline;
            var hadOnline = playerInfo.TryGetValue("Online", out previousOnline);
            var runtimeOnlineText = runtimeOnline.ToString();
            playerInfo["Online"] = runtimeOnlineText;

            string previousInMission;
            var hadInMission = playerInfo.TryGetValue("InMission", out previousInMission);
            var runtimeInMissionText = runtimeInMission.ToString();
            playerInfo["InMission"] = runtimeInMissionText;

            if (!logOverrides)
            {
                return;
            }

            var onlineChanged = !hadOnline || !string.Equals(previousOnline, runtimeOnlineText, StringComparison.OrdinalIgnoreCase);
            var inMissionChanged = !hadInMission || !string.Equals(previousInMission, runtimeInMissionText, StringComparison.OrdinalIgnoreCase);

            if (!onlineChanged && !inMissionChanged)
            {
                return;
            }

            _logger.LogLow(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "playerinfo-status-runtime-override",
                identityHash = identityHash,
                gameName = gameName,
                source = source,
                runtimeOnline = runtimeOnline,
                runtimeInMission = runtimeInMission,
                hasHubPresence = hasHubPresence,
                previousOnline = hadOnline ? previousOnline : null,
                previousInMission = hadInMission ? previousInMission : null,
                appliedOnline = runtimeOnlineText,
                appliedInMission = playerInfo.ContainsKey("InMission") ? playerInfo["InMission"] : null,
            });
        }

        private void SanitizePlayerInfoUpdates(string identityHash, Dictionary<string, string> updates)
        {
            if (updates == null)
            {
                return;
            }

            var stableDisplayName = ResolvePreferredAccountDisplayName(identityHash);
            string displayName;
            if (updates.TryGetValue("DisplayName", out displayName) && !IsNullOrWhiteSpace(displayName))
            {
                var semi = displayName.IndexOf(';');
                var characterPart = semi >= 0 && semi + 1 < displayName.Length ? displayName.Substring(semi + 1) : null;
                updates["DisplayName"] = semi >= 0 ? (stableDisplayName + ";" + (characterPart ?? string.Empty)) : stableDisplayName;
            }

            string launcherDisplayName;
            if (updates.TryGetValue("LauncherDisplayName", out launcherDisplayName) && !IsNullOrWhiteSpace(launcherDisplayName))
            {
                updates["LauncherDisplayName"] = stableDisplayName;
            }
        }

        private void SanitizeStoredDisplayNames(string identityHash, Dictionary<string, string> stored)
        {
            if (stored == null)
            {
                return;
            }

            var stableDisplayName = ResolvePreferredAccountDisplayName(identityHash);
            string launcherDisplayName;
            if (stored.TryGetValue("LauncherDisplayName", out launcherDisplayName) && !IsNullOrWhiteSpace(launcherDisplayName))
            {
                stored["LauncherDisplayName"] = stableDisplayName;
            }

            string displayName;
            if (stored.TryGetValue("DisplayName", out displayName) && !IsNullOrWhiteSpace(displayName))
            {
                var semi = displayName.IndexOf(';');
                var characterPart = semi >= 0 && semi + 1 < displayName.Length ? displayName.Substring(semi + 1) : null;
                stored["DisplayName"] = semi >= 0 ? (stableDisplayName + ";" + (characterPart ?? string.Empty)) : stableDisplayName;
            }
        }

        private string ResolvePreferredAccountDisplayName(string identityHash)
        {
            if (_userStore != null)
            {
                try
                {
                    var resolved = _userStore.GetDisplayName(identityHash);
                    if (!IsNullOrWhiteSpace(resolved))
                    {
                        return resolved;
                    }
                }
                catch
                {
                }
            }

            return BuildStableDisplayName(identityHash);
        }

        private static string BuildStableDisplayName(string identityHash)
        {
            if (!IsGuidish(identityHash))
            {
                return "OfflineRunner";
            }

            var normalizedIdentity = NormalizeGuidish(identityHash);
            var dash = normalizedIdentity.IndexOf('-');
            return dash > 0 ? normalizedIdentity.Substring(0, dash) : normalizedIdentity;
        }

        private static Dictionary<string, string> ParsePlayerInfoUpdates(IDictionary requestDict)
        {
            var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (requestDict == null)
            {
                return updates;
            }
            if (!requestDict.Contains("PlayerInfo"))
            {
                return updates;
            }

            var playerInfoObj = requestDict["PlayerInfo"];
            var playerInfoDict = playerInfoObj as IDictionary;
            if (playerInfoDict == null)
            {
                return updates;
            }

            foreach (DictionaryEntry entry in playerInfoDict)
            {
                var key = entry.Key as string;
                if (IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var valueObj = entry.Value;
                if (valueObj == null)
                {
                    updates[key] = null;
                    continue;
                }

                var valueStr = valueObj as string;
                if (valueStr != null)
                {
                    updates[key] = valueStr;
                    continue;
                }

                updates[key] = valueObj.ToString();
            }

            return updates;
        }

        private void TryValidatePlayerCharacterBlob(string identityHash, string gameName, Dictionary<string, string> updates)
        {
            if (updates == null)
            {
                return;
            }

            string blob;
            if (!updates.TryGetValue("PlayerCharacter", out blob) || IsNullOrWhiteSpace(blob))
            {
                return;
            }

            var status = "unknown";
            try
            {
                Cliffhanger.SRO.ServerClientCommons.SerializerHelper.FromUncompressedString(blob);
                status = "ok-uncompressed";
            }
            catch
            {
                try
                {
                    Cliffhanger.SRO.ServerClientCommons.SerializerHelper.FromCompressedString(blob);
                    status = "ok-compressed";
                }
                catch
                {
                    status = "invalid";
                }
            }

            _logger.LogLow(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "playerinfo-blob",
                identityHash = identityHash,
                gameName = gameName,
                key = "PlayerCharacter",
                status = status,
                length = blob.Length,
            });
        }

        private static List<string> ParseRequestedIdentityHashes(byte[] bodyBytes)
        {
            var results = new List<string>();
            if (bodyBytes == null || bodyBytes.Length == 0)
            {
                return results;
            }

            try
            {
                var json = Encoding.UTF8.GetString(bodyBytes);
                var obj = Json.DeserializeObject(json);
                var dict = obj as IDictionary;
                if (dict == null)
                {
                    return results;
                }

                if (!dict.Contains("IdentityHashes"))
                {
                    return results;
                }

                var hashesObj = dict["IdentityHashes"];
                var arr = hashesObj as Array;
                if (arr == null)
                {
                    return results;
                }

                foreach (var entry in arr)
                {
                    var s = entry as string;
                    if (!IsNullOrWhiteSpace(s))
                    {
                        results.Add(s);
                    }
                }
            }
            catch
            {
                return results;
            }

            return results;
        }

        private static bool TryExtractSteamId64FromAuthTicketHex(string ticketHex, out ulong steamId64)
        {
            steamId64 = 0;
            if (IsNullOrWhiteSpace(ticketHex))
            {
                return false;
            }

            byte[] bytes;
            if (!TryDecodeHex(ticketHex, out bytes) || bytes == null)
            {
                return false;
            }

            ulong candidate;
            if (TryReadUInt64LE(bytes, 12, out candidate) && IsPlausibleSteamId64(candidate))
            {
                steamId64 = candidate;
                return true;
            }
            if (TryReadUInt64LE(bytes, 64, out candidate) && IsPlausibleSteamId64(candidate))
            {
                steamId64 = candidate;
                return true;
            }

            var max = Math.Min(bytes.Length - 8, 4096);
            for (var offset = 0; offset <= max; offset++)
            {
                if (TryReadUInt64LE(bytes, offset, out candidate) && IsPlausibleSteamId64(candidate))
                {
                    steamId64 = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool IsPlausibleSteamId64(ulong value)
        {
            return value >= 76561190000000000UL && value <= 76561230000000000UL;
        }

        private static bool TryReadUInt64LE(byte[] bytes, int offset, out ulong value)
        {
            value = 0;
            if (bytes == null || offset < 0 || offset + 8 > bytes.Length)
            {
                return false;
            }

            value =
                ((ulong)bytes[offset + 0]) |
                ((ulong)bytes[offset + 1] << 8) |
                ((ulong)bytes[offset + 2] << 16) |
                ((ulong)bytes[offset + 3] << 24) |
                ((ulong)bytes[offset + 4] << 32) |
                ((ulong)bytes[offset + 5] << 40) |
                ((ulong)bytes[offset + 6] << 48) |
                ((ulong)bytes[offset + 7] << 56);
            return true;
        }

        private static bool TryDecodeHex(string hex, out byte[] bytes)
        {
            bytes = null;
            if (hex == null)
            {
                return false;
            }

            hex = hex.Trim();
            if (hex.Length == 0)
            {
                bytes = new byte[0];
                return true;
            }
            if ((hex.Length & 1) != 0)
            {
                return false;
            }

            try
            {
                var len = hex.Length / 2;
                var arr = new byte[len];
                for (var i = 0; i < len; i++)
                {
                    arr[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                }
                bytes = arr;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
