using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Globalization;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Http
{
    public sealed partial class HttpStubServer
    {
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private readonly LocalServiceOptions _options;
        private readonly RequestLogger _logger;
        private readonly ISessionIdentityMap _sessionIdentityMap;
        private readonly IPlayerInfoRepository _playerInfoRepository;
        private readonly LocalUserStore _userStore;

        public HttpStubServer(LocalServiceOptions options, RequestLogger logger)
            : this(options, logger, new LocalUserStore(options, logger))
        {
        }

        public HttpStubServer(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore)
            : this(options, logger, userStore, new ExpiringSessionIdentityMap(), new LocalUserStorePlayerInfoRepository(userStore))
        {
        }

        public HttpStubServer(
            LocalServiceOptions options,
            RequestLogger logger,
            LocalUserStore userStore,
            ISessionIdentityMap sessionIdentityMap,
            IPlayerInfoRepository playerInfoRepository)
        {
            _options = options;
            _logger = logger;
            _userStore = userStore ?? new LocalUserStore(options, logger);
            _sessionIdentityMap = sessionIdentityMap ?? new InMemorySessionIdentityMap();

            // PlayerInfo is useful to persist (character blob, display name, etc.).
            // If a LocalUserStore is present, default to its repository unless overridden.
            if (playerInfoRepository != null)
            {
                _playerInfoRepository = playerInfoRepository;
            }
            else
            {
                _playerInfoRepository = _userStore != null
                    ? (IPlayerInfoRepository)new LocalUserStorePlayerInfoRepository(_userStore)
                    : (IPlayerInfoRepository)new InMemoryPlayerInfoRepository();
            }
        }

        public void Run(ManualResetEvent stopEvent)
        {
            var address = ResolveBindAddress(_options.Host);
            var listener = new TcpListener(address, _options.Port);
            listener.Start();

            ThreadPool.QueueUserWorkItem(delegate
            {
                stopEvent.WaitOne();
                try { listener.Stop(); }
                catch { }
            });

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "http",
                message = string.Format("http listening on http://{0}:{1}", _options.Host, _options.Port),
            });

            while (!stopEvent.WaitOne(0))
            {
                TcpClient client = null;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (stopEvent.WaitOne(0))
                    {
                        break;
                    }
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (client == null)
                {
                    continue;
                }

                ThreadPool.QueueUserWorkItem(delegate (object state)
                {
                    HandleClient((TcpClient)state);
                }, client);
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 2000;
                client.SendTimeout = 2000;

                var endpoint = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "unknown";
                using (var stream = client.GetStream())
                {
                    HttpRequest request;
                    try
                    {
                        request = ReadSingleRequest(stream);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "http-error", peer = endpoint, message = ex.Message });
                        return;
                    }

                    if (request == null)
                    {
                        return;
                    }

                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "http-request",
                        peer = endpoint,
                        method = request.Method,
                        host = request.Host,
                        path = request.Path,
                        query = request.Query,
                        userAgent = request.UserAgent,
                        contentType = request.ContentType,
                        contentLength = request.BodyBytes != null ? request.BodyBytes.Length : 0,
                        body = request.BodyBytes != null ? Encoding.UTF8.GetString(request.BodyBytes) : string.Empty,
                    });

                    var response = RouteRequest(request);
                    WriteResponse(stream, response);
                }
            }
        }

        private HttpResponse RouteRequest(HttpRequest request)
        {
            var path = request.Path ?? "/";

            var staticResponse = TryServeStatic(path);
            if (staticResponse != null)
            {
                return staticResponse;
            }

            var accountResponse = TryServeAccount(path, request.BodyBytes ?? new byte[0]);
            if (accountResponse != null)
            {
                return accountResponse;
            }

            if (StartsWith(path, "/CouponSystem/"))
            {
                return JsonResponse(200, new object[0]);
            }

            if (StartsWith(path, "/Matchmaking/") || StartsWith(path, "/ChatAndFriends/"))
            {
                return JsonResponse(200, new Dictionary<string, object>
                {
                    { "ok", true },
                    { "offlineStub", true },
                    { "path", path },
                });
            }

            return JsonResponse(404, new Dictionary<string, object>
            {
                { "ok", false },
                { "offlineStub", true },
                { "path", path },
                { "error", "No stub route configured" },
            });
        }

        private HttpResponse TryServeStatic(string path)
        {
            string filePath = null;

            if (string.Equals(path, "/SRO/configs/SRO_23.3/SteamWindows/LauncherConfig.xml", StringComparison.OrdinalIgnoreCase))
            {
                filePath = Path.Combine(_options.ConfigDir, "LauncherConfig.xml");
            }
            else if (string.Equals(path, "/SRO/configs/SRO_23.3/SteamWindows/config.xml", StringComparison.OrdinalIgnoreCase))
            {
                filePath = Path.Combine(_options.ConfigDir, "config.xml");
            }
            else if (string.Equals(path, "/Patches/SRO/StandaloneWindows/live", StringComparison.OrdinalIgnoreCase))
            {
                filePath = Path.Combine(_options.ConfigDir, "patches_live.txt");
            }

            if (filePath == null)
            {
                return null;
            }

            if (!File.Exists(filePath))
            {
                return TextResponse(500, "Missing local file: " + filePath, "text/plain; charset=utf-8");
            }

            var bytes = File.ReadAllBytes(filePath);
            var contentType = EndsWith(filePath, ".xml") ? "application/xml; charset=utf-8" : "text/plain; charset=utf-8";
            return BytesResponse(200, bytes, contentType);
        }

        private HttpResponse TryServeAccount(string path, byte[] bodyBytes)
        {
            if (!StartsWith(path, "/AccountSystem/"))
            {
                return null;
            }

            if (EndsWith(path, "/Accounts/Steam/Authenticate"))
            {
                // Session hashes do not need to persist across service restarts in offline mode.
                // Keep them in-memory and prune them by TTL.
                var session = Guid.NewGuid();

                ulong steamId = 0;
                string identity = null;

                // Enforced: require a usable SteamID64 from the auth ticket.
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
                    return JsonResponse(200, new Dictionary<string, object>
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

            if (EndsWith(path, "/Accounts/GetAccountForHash"))
            {
                // Enforced: require session hash -> identity mapping (minted in Steam/Authenticate).
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
                    return JsonResponse(200, new Dictionary<string, object>
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

                    // The client sometimes queries Guid.Empty as a sentinel for "no player".
                    // Important: the client-side PlayerInfoResolver only resolves queued requests when
                    // the cache contains the requested GUID. If we omit Guid.Empty from PlayerInfoResults,
                    // anything waiting on it may hang indefinitely during early startup.
                    // Best compromise: return a successful entry with an EMPTY PlayerInfo list.
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
                    if (stored != null && _userStore != null && !stored.ContainsKey("LauncherDisplayName"))
                    {
                        stored["LauncherDisplayName"] = _userStore.GetDisplayName(identityHash);
                    }
                    var responseInfo = BuildPlayerInfoResponse(stored, requestedKeys);

                    results.Add(new Dictionary<string, object>
                    {
                        { "IdentityHash", identityHash },
                        {
                            "PlayerInfo",
                            responseInfo
                        },
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

            if (EndsWith(path, "/Accounts/PlayerActivity/SetPlayerInfo"))
            {
                var dict = TryParseJsonDictionary(bodyBytes);
                var gameName = GetString(dict, "GameName");
                if (IsNullOrWhiteSpace(gameName))
                {
                    gameName = "SRO";
                }

                // Prefer identity hash if present; else resolve via session hash.
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

                // If the client sends serialized blobs (e.g., PlayerCharacter), prefer the game's own serializers
                // for validation/inspection rather than custom base64/zip logic.
                TryValidatePlayerCharacterBlob(identityHash, gameName, playerInfoUpdates);

                // Heuristic: client stores DisplayName but often requests LauncherDisplayName.
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

                // If DisplayName includes a second part (e.g. "Launcher;ShadowZero"), treat it as the current
                // career's character name and persist it into the active slot.
                if (_userStore != null && playerInfoUpdates.TryGetValue("DisplayName", out displayName) && !IsNullOrWhiteSpace(displayName))
                {
                    var semi = displayName.IndexOf(';');
                    if (semi > 0 && semi + 1 < displayName.Length)
                    {
                        var characterName = displayName.Substring(semi + 1).Trim();
                        if (!IsNullOrWhiteSpace(characterName))
                        {
                            var slotIndex = _userStore.GetLastCareerIndex(identityHash);
                            var slot = _userStore.GetOrCreateCareer(identityHash, slotIndex, false);
                            if (slot != null && !string.Equals(slot.CharacterName, characterName, StringComparison.Ordinal))
                            {
                                slot.CharacterName = characterName;
                                slot.IsOccupied = true;
                                slot.PendingPersistenceCreation = false;
                                _userStore.UpsertCareer(identityHash, slot);
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

            return JsonResponse(200, new Dictionary<string, object> { { "ok", true }, { "offlineStub", true }, { "path", path } });
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

        private static object[] BuildPlayerInfoResponse(Dictionary<string, string> stored, List<string> requestedKeys)
        {
            // No keys -> return everything we have.
            if (stored == null)
            {
                stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            // Keep legacy stub behavior so early UI code that expects a launcher display name doesn't regress.
            if (!stored.ContainsKey("LauncherDisplayName"))
            {
                stored["LauncherDisplayName"] = "OfflineRunner";
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

                // Compatibility fallback for common key mismatch.
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

            // Many serializers in Cliffhanger.SRO.ServerClientCommons use either "compressed" (ZipUtilities)
            // or "uncompressed" base64 MessageWriter bytes.
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

        private static IDictionary TryParseJsonDictionary(byte[] bodyBytes)
        {
            if (bodyBytes == null || bodyBytes.Length == 0)
            {
                return null;
            }

            var json = Encoding.UTF8.GetString(bodyBytes);
            var obj = Json.DeserializeObject(json);
            return obj as IDictionary;
        }

        private static string GetString(IDictionary dict, string key)
        {
            if (dict == null || IsNullOrWhiteSpace(key))
            {
                return null;
            }
            if (!dict.Contains(key))
            {
                return null;
            }
            return dict[key] as string;
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

        public interface IPlayerInfoRepository
        {
            Dictionary<string, string> Get(string identityHash, string gameName);
            PlayerInfoChanges Set(string identityHash, string gameName, Dictionary<string, string> updates);
        }

        public sealed class PlayerInfoChanges
        {
            public PlayerInfoChanges()
            {
                Added = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Deleted = new List<string>();
            }

            public Dictionary<string, string> Added { get; private set; }
            public Dictionary<string, string> Updated { get; private set; }
            public List<string> Deleted { get; private set; }
        }

        private sealed class LocalUserStoreSessionIdentityMap : ISessionIdentityMap
        {
            private readonly LocalUserStore _store;

            public LocalUserStoreSessionIdentityMap(LocalUserStore store)
            {
                _store = store;
            }

            public void SetIdentityForSession(string sessionHash, string identityHash)
            {
                if (_store == null)
                {
                    return;
                }
                _store.SetIdentityForSession(sessionHash, identityHash);
            }

            public bool TryGetIdentityForSession(string sessionHash, out string identityHash)
            {
                identityHash = null;
                if (_store == null)
                {
                    return false;
                }
                return _store.TryGetIdentityForSession(sessionHash, out identityHash);
            }
        }

        private sealed class LocalUserStorePlayerInfoRepository : IPlayerInfoRepository
        {
            private readonly LocalUserStore _store;

            public LocalUserStorePlayerInfoRepository(LocalUserStore store)
            {
                _store = store;
            }

            public Dictionary<string, string> Get(string identityHash, string gameName)
            {
                if (_store == null)
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                return _store.GetPlayerInfo(identityHash, gameName);
            }

            public PlayerInfoChanges Set(string identityHash, string gameName, Dictionary<string, string> updates)
            {
                if (_store == null)
                {
                    return new PlayerInfoChanges();
                }

                var changes = _store.SetPlayerInfo(identityHash, gameName, updates);
                var result = new PlayerInfoChanges();
                foreach (var kvp in changes.Added) result.Added[kvp.Key] = kvp.Value;
                foreach (var kvp in changes.Updated) result.Updated[kvp.Key] = kvp.Value;
                for (var i = 0; i < changes.Deleted.Count; i++) result.Deleted.Add(changes.Deleted[i]);
                return result;
            }
        }

        private sealed class InMemorySessionIdentityMap : ISessionIdentityMap
        {
            private readonly object _lock = new object();
            private readonly Dictionary<string, string> _sessionToIdentity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public void SetIdentityForSession(string sessionHash, string identityHash)
            {
                if (IsNullOrWhiteSpace(sessionHash) || IsNullOrWhiteSpace(identityHash))
                {
                    return;
                }

                lock (_lock)
                {
                    _sessionToIdentity[NormalizeGuidish(sessionHash)] = NormalizeGuidish(identityHash);
                }
            }

            public bool TryGetIdentityForSession(string sessionHash, out string identityHash)
            {
                identityHash = null;
                if (IsNullOrWhiteSpace(sessionHash))
                {
                    return false;
                }

                lock (_lock)
                {
                    return _sessionToIdentity.TryGetValue(NormalizeGuidish(sessionHash), out identityHash);
                }
            }
        }

        private sealed class InMemoryPlayerInfoRepository : IPlayerInfoRepository
        {
            private readonly object _lock = new object();
            private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _data =
                new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, string> Get(string identityHash, string gameName)
            {
                if (IsNullOrWhiteSpace(identityHash) || IsNullOrWhiteSpace(gameName))
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                identityHash = NormalizeGuidish(identityHash);
                gameName = gameName.Trim();

                lock (_lock)
                {
                    Dictionary<string, Dictionary<string, string>> byGame;
                    if (!_data.TryGetValue(identityHash, out byGame))
                    {
                        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    Dictionary<string, string> info;
                    if (!byGame.TryGetValue(gameName, out info) || info == null)
                    {
                        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    return new Dictionary<string, string>(info, StringComparer.OrdinalIgnoreCase);
                }
            }

            public PlayerInfoChanges Set(string identityHash, string gameName, Dictionary<string, string> updates)
            {
                var changes = new PlayerInfoChanges();
                if (IsNullOrWhiteSpace(identityHash) || IsNullOrWhiteSpace(gameName) || updates == null)
                {
                    return changes;
                }

                identityHash = NormalizeGuidish(identityHash);
                gameName = gameName.Trim();

                lock (_lock)
                {
                    Dictionary<string, Dictionary<string, string>> byGame;
                    if (!_data.TryGetValue(identityHash, out byGame) || byGame == null)
                    {
                        byGame = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                        _data[identityHash] = byGame;
                    }

                    Dictionary<string, string> info;
                    if (!byGame.TryGetValue(gameName, out info) || info == null)
                    {
                        info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        byGame[gameName] = info;
                    }

                    foreach (var kvp in updates)
                    {
                        var key = kvp.Key;
                        if (IsNullOrWhiteSpace(key))
                        {
                            continue;
                        }

                        var value = kvp.Value;
                        string existing;
                        var hadExisting = info.TryGetValue(key, out existing);

                        if (value == null)
                        {
                            if (hadExisting)
                            {
                                info.Remove(key);
                                changes.Deleted.Add(key);
                            }
                            continue;
                        }

                        if (!hadExisting)
                        {
                            info[key] = value;
                            changes.Added[key] = value;
                            continue;
                        }

                        if (!string.Equals(existing, value, StringComparison.Ordinal))
                        {
                            info[key] = value;
                            changes.Updated[key] = value;
                        }
                    }
                }

                return changes;
            }
        }

        private static string NormalizeGuidish(string value)
        {
            if (value == null)
            {
                return null;
            }
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                return trimmed;
            }

            try
            {
                var guid = new Guid(trimmed);
                return guid.ToString("D");
            }
            catch
            {
                return trimmed;
            }
        }

        private static bool TryExtractSteamId64FromAuthTicketHex(string ticketHex, out ulong steamId64)
        {
            steamId64 = 0;
            if (IsNullOrWhiteSpace(ticketHex))
            {
                return false;
            }

            // Ticket is hex-encoded bytes.
            // Empirically (from captured client traffic), SteamID64 appears as a little-endian UInt64
            // at offsets 12 and 64 within the decoded ticket bytes.
            // We treat this as a heuristic: validate it falls in the expected SteamID64 range.

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

            // Fallback: scan for a plausible SteamID64 to be resilient across ticket variants.
            // (Bounded to avoid excessive CPU.)
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
            // Typical SteamID64 values look like 7656119xxxxxxxxxx.
            // Use a broad range check instead of strict parsing (we just need stable uniqueness).
            return value >= 76561190000000000UL && value <= 76561230000000000UL;
        }

        private static bool TryReadUInt64LE(byte[] bytes, int offset, out ulong value)
        {
            value = 0;
            if (bytes == null || offset < 0 || offset + 8 > bytes.Length)
            {
                return false;
            }

            // Manual little-endian read to avoid BitConverter endianness surprises.
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

        private static HttpResponse JsonResponse(int statusCode, object payload)
        {
            var json = Json.Serialize(payload);
            return TextResponse(statusCode, json, "application/json; charset=utf-8");
        }

        private static HttpResponse TextResponse(int statusCode, string text, string contentType)
        {
            return BytesResponse(statusCode, Encoding.UTF8.GetBytes(text ?? string.Empty), contentType);
        }

        private static HttpResponse BytesResponse(int statusCode, byte[] bytes, string contentType)
        {
            return new HttpResponse
            {
                StatusCode = statusCode,
                ReasonPhrase = statusCode == 200 ? "OK" : (statusCode == 404 ? "Not Found" : "Error"),
                ContentType = contentType,
                BodyBytes = bytes,
            };
        }

        private static IPAddress ResolveBindAddress(string host)
        {
            if (string.IsNullOrEmpty(host) || host == "0.0.0.0" || host == "+")
            {
                return IPAddress.Any;
            }
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return IPAddress.Loopback;
            }
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                return ip;
            }
            return IPAddress.Any;
        }

        private static bool StartsWith(string value, string prefix)
        {
            return value != null && prefix != null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EndsWith(string value, string suffix)
        {
            return value != null && suffix != null && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 32;
            return serializer;
        }

    }
}
