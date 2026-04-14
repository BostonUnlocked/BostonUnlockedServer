using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Globalization;
using Shadowrun.LocalService.Core.Persistence;
using Shadowrun.LocalService.Core.Protocols;

namespace Shadowrun.LocalService.Core.Http
{
    public sealed partial class HttpStubServer
    {
        private static readonly JavaScriptSerializer Json = CreateSerializer();
        private const string PatchesLivePrefix = "/Patches/SRO/StandaloneWindows/live";
        private const int MaxLoggedRequestBodyBytes = 4096;

        private readonly LocalServiceOptions _options;
        private readonly RequestLogger _logger;
        private readonly ISessionIdentityMap _sessionIdentityMap;
        private readonly IPlayerInfoRepository _playerInfoRepository;
        private readonly LocalUserStore _userStore;
        private readonly HubPresenceRegistry _hubPresenceRegistry;
        private readonly object _statusPageCacheLock = new object();
        private readonly object _missionNameCacheLock = new object();
        private string _cachedStatusPageHtml;
        private DateTime _cachedStatusPageGeneratedUtc = DateTime.MinValue;
        private Dictionary<string, string> _cachedMissionEnglishNames;
        private DateTime _cachedMissionEnglishNamesLoadedUtc = DateTime.MinValue;

        private static readonly TimeSpan StatusPageCacheDuration = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MissionNameCacheDuration = TimeSpan.FromMinutes(5);

        public HttpStubServer(LocalServiceOptions options, RequestLogger logger)
            : this(options, logger, new LocalUserStore(options, logger))
        {
        }

        public HttpStubServer(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore)
            : this(options, logger, userStore, new ExpiringSessionIdentityMap(), new LocalUserStorePlayerInfoRepository(userStore), null)
        {
        }

        public HttpStubServer(
            LocalServiceOptions options,
            RequestLogger logger,
            LocalUserStore userStore,
            ISessionIdentityMap sessionIdentityMap,
            IPlayerInfoRepository playerInfoRepository)
            : this(options, logger, userStore, sessionIdentityMap, playerInfoRepository, null)
        {
        }

        public HttpStubServer(
            LocalServiceOptions options,
            RequestLogger logger,
            LocalUserStore userStore,
            ISessionIdentityMap sessionIdentityMap,
            IPlayerInfoRepository playerInfoRepository,
            HubPresenceRegistry hubPresenceRegistry)
        {
            _options = options;
            _logger = logger;
            _userStore = userStore ?? new LocalUserStore(options, logger);
            _sessionIdentityMap = sessionIdentityMap ?? new InMemorySessionIdentityMap();
            _hubPresenceRegistry = hubPresenceRegistry;

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
                var connectionHash = RequestLogger.CreateConnectionHash("http", endpoint);
                _logger.RegisterConnectionContext("http", endpoint, connectionHash);
                try
                {
                    using (var stream = client.GetStream())
                    {
                        HttpRequest request;
                        try
                        {
                            request = ReadSingleRequest(stream);
                        }
                        catch (Exception ex)
                        {
                            SafeLog(new { ts = RequestLogger.UtcNowIso(), type = "http-error", connectionHash = connectionHash, peer = endpoint, message = ex.Message });
                            TryWriteSimpleErrorResponse(stream, 400, "Bad Request");
                            return;
                        }

                        if (request == null)
                        {
                            return;
                        }

                        try
                        {
                            SafeLog(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "http-request",
                                accountId = ResolveRequestAccountId(request),
                                connectionHash = connectionHash,
                                peer = endpoint,
                                method = request.Method,
                                host = request.Host,
                                path = request.Path,
                                query = request.Query,
                                userAgent = request.UserAgent,
                                contentType = request.ContentType,
                                contentLength = request.BodyBytes != null ? request.BodyBytes.Length : 0,
                                body = BuildSafeRequestBodyForLog(request.Path, request.BodyBytes),
                            });

                            var response = RouteRequest(request);
                            var suppressBody = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
                            WriteResponse(stream, response, suppressBody);
                        }
                        catch (Exception ex)
                        {
                            SafeLog(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "http-error",
                                connectionHash = connectionHash,
                                peer = endpoint,
                                path = request.Path,
                                message = ex.Message,
                            });

                            TryWriteSimpleErrorResponse(stream, 500, "Internal Server Error");
                            return;
                        }
                    }
                }
                finally
                {
                    _logger.ClearConnectionContext("http", endpoint, connectionHash);
                }
            }
        }

        private void SafeLog(object payload)
        {
            try
            {
                _logger.Log(payload);
            }
            catch
            {
            }
        }

        private static void TryWriteSimpleErrorResponse(NetworkStream stream, int statusCode, string message)
        {
            if (stream == null)
            {
                return;
            }

            try
            {
                WriteResponse(stream, TextResponse(statusCode, message, "text/plain; charset=utf-8"), false);
            }
            catch
            {
            }
        }

        private string ResolveRequestAccountId(HttpRequest request)
        {
            if (request == null)
            {
                return null;
            }

            var accountIdFromPath = ResolveRequestAccountIdFromPath(request.Path);
            if (!IsNullOrWhiteSpace(accountIdFromPath))
            {
                return accountIdFromPath;
            }

            if (request.BodyBytes == null || request.BodyBytes.Length == 0)
            {
                return null;
            }

            IDictionary dict;
            try
            {
                dict = TryParseJsonDictionary(request.BodyBytes);
            }
            catch
            {
                dict = null;
            }

            if (dict == null)
            {
                return null;
            }

            var identityHash = GetString(dict, "IdentityHash");
            if (!IsNullOrWhiteSpace(identityHash))
            {
                return identityHash;
            }

            identityHash = GetSingleStringValue(dict, "IdentityHashes");
            if (!IsNullOrWhiteSpace(identityHash))
            {
                return identityHash;
            }

            identityHash = GetString(dict, "AccountId");
            if (!IsNullOrWhiteSpace(identityHash))
            {
                return identityHash;
            }

            var sessionHash = GetString(dict, "SessionHash");
            if (IsNullOrWhiteSpace(sessionHash))
            {
                sessionHash = GetString(dict, "AccountSystemSessionHash");
            }

            string mappedIdentity;
            if (!IsNullOrWhiteSpace(sessionHash)
                && TryResolveIdentityForSessionHash(sessionHash, out mappedIdentity)
                && !IsNullOrWhiteSpace(mappedIdentity))
            {
                return mappedIdentity;
            }

            return null;
        }

        private static string ResolveRequestAccountIdFromPath(string path)
        {
            var normalizedPath = NormalizePathForRoute(path);
            var couponHistoryIdentityHash = ExtractCouponHistoryIdentityHash(normalizedPath);
            if (IsGuidish(couponHistoryIdentityHash))
            {
                return couponHistoryIdentityHash;
            }

            return null;
        }

        private static string GetSingleStringValue(IDictionary dict, string key)
        {
            if (dict == null || IsNullOrWhiteSpace(key) || !dict.Contains(key))
            {
                return null;
            }

            var value = dict[key];
            if (value == null)
            {
                return null;
            }

            var text = value as string;
            if (text != null)
            {
                return text;
            }

            var list = value as IList;
            if (list == null || list.Count != 1 || list[0] == null)
            {
                return null;
            }

            return Convert.ToString(list[0], CultureInfo.InvariantCulture);
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

            var couponResponse = TryServeCoupon(request);
            if (couponResponse != null)
            {
                return couponResponse;
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

            if (string.Equals(path, "/", StringComparison.Ordinal))
            {
                return TextResponse(200, GetCachedServerStatusPageHtml(), "text/html; charset=utf-8");
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
            string explicitContentType = null;
            var patchPathRequest = false;

            if (EndsWith(path, "/LauncherConfig.xml"))
            {
                filePath = Path.Combine(_options.ConfigDir, "LauncherConfig.xml");
            }
            else if (EndsWith(path, "/config.xml"))
            {
                filePath = Path.Combine(_options.ConfigDir, "config.xml");
            }
            else if (TryResolvePatchPath(path, out filePath, out explicitContentType))
            {
                patchPathRequest = true;
            }
            else if (string.Equals(path, PatchesLivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                filePath = Path.Combine(_options.ConfigDir, "patches_live.txt");
            }

            if (filePath == null)
            {
                return null;
            }

            if (!File.Exists(filePath))
            {
                if (patchPathRequest)
                {
                    return TextResponse(404, "Patch asset not found", "text/plain; charset=utf-8");
                }
                return TextResponse(500, "Missing local file: " + filePath, "text/plain; charset=utf-8");
            }

            var contentType = !IsNullOrWhiteSpace(explicitContentType)
                ? explicitContentType
                : (EndsWith(filePath, ".xml") ? "application/xml; charset=utf-8" : "text/plain; charset=utf-8");
            return FileResponse(200, filePath, contentType);
        }

        private bool TryResolvePatchPath(string path, out string filePath, out string contentType)
        {
            filePath = null;
            contentType = null;

            if (IsNullOrWhiteSpace(path) || !StartsWith(path, PatchesLivePrefix + "/"))
            {
                return false;
            }

            var relative = path.Substring(PatchesLivePrefix.Length + 1);
            if (IsNullOrWhiteSpace(relative))
            {
                return false;
            }

            var patchRoot = Path.Combine(_options.ConfigDir, "patches");

            if (string.Equals(relative, "versions.txt", StringComparison.OrdinalIgnoreCase))
            {
                var versionsPath = Path.Combine(patchRoot, "versions.txt");
                filePath = File.Exists(versionsPath)
                    ? versionsPath
                    : Path.Combine(_options.ConfigDir, "patches_live.txt");
                contentType = "text/plain; charset=utf-8";
                return true;
            }

            if (string.Equals(relative, "config.json", StringComparison.OrdinalIgnoreCase))
            {
                filePath = Path.Combine(patchRoot, "config.json");
                contentType = "application/json; charset=utf-8";
                return true;
            }

            if (EndsWith(relative, "/patch.zip"))
            {
                var chunks = relative.Split('/');
                if (chunks.Length == 2
                    && string.Equals(chunks[1], "patch.zip", StringComparison.OrdinalIgnoreCase)
                    && Regex.IsMatch(chunks[0], "^[A-Za-z0-9_.-]+_[A-Za-z0-9_.-]+$"))
                {
                    filePath = Path.Combine(Path.Combine(patchRoot, chunks[0]), "patch.zip");
                    contentType = "application/zip";
                    return true;
                }
            }

            return false;
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

        private static string BuildSafeRequestBodyForLog(string path, byte[] bodyBytes)
        {
            if (bodyBytes == null || bodyBytes.Length == 0)
            {
                return string.Empty;
            }

            if (!ShouldCaptureRequestBody(path))
            {
                return string.Format("[body omitted for path; {0} bytes]", bodyBytes.Length);
            }

            var raw = ConvertBodyBytesToSafeLogString(bodyBytes);
            if (!ShouldRedactSensitiveBody(path))
            {
                return raw;
            }

            if (bodyBytes.Length > MaxLoggedRequestBodyBytes)
            {
                return "[redacted body; truncated]";
            }

            try
            {
                var parsed = Json.DeserializeObject(raw) as IDictionary;
                if (parsed == null)
                {
                    return raw;
                }

                if (parsed.Contains("Password"))
                {
                    parsed["Password"] = "***";
                }

                return Json.Serialize(parsed);
            }
            catch
            {
                return raw;
            }
        }

        private static string ConvertBodyBytesToSafeLogString(byte[] bodyBytes)
        {
            if (bodyBytes == null || bodyBytes.Length == 0)
            {
                return string.Empty;
            }

            if (bodyBytes.Length <= MaxLoggedRequestBodyBytes)
            {
                return Encoding.UTF8.GetString(bodyBytes);
            }

            var truncatedBytes = new byte[MaxLoggedRequestBodyBytes];
            Buffer.BlockCopy(bodyBytes, 0, truncatedBytes, 0, MaxLoggedRequestBodyBytes);
            var truncatedBody = Encoding.UTF8.GetString(truncatedBytes);
            var omittedBytes = bodyBytes.Length - MaxLoggedRequestBodyBytes;
            return string.Format("{0}...[truncated {1} bytes]", truncatedBody, omittedBytes);
        }

        private static bool ShouldCaptureRequestBody(string path)
        {
            if (IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var normalizedPath = NormalizePathForRoute(path);
            return StartsWith(normalizedPath, "/AccountSystem/")
                || StartsWith(normalizedPath, "/CouponSystem/")
                || StartsWith(normalizedPath, "/Matchmaking/")
                || StartsWith(normalizedPath, "/ChatAndFriends/");
        }

        private static bool ShouldRedactSensitiveBody(string path)
        {
            if (IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return EndsWith(path, "/Accounts/Cliffhanger/Authenticate")
                || EndsWith(path, "/Accounts/Cliffhanger/Register")
                || EndsWith(path, "/Accounts/Cliffhanger/ChangePassword");
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

        private string BuildServerStatusPageHtml()
        {
            var allPlayers = BuildOnlineStatusPlayers();
            var acts = BuildActBuckets(allPlayers);
            var renderedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

            var html = new StringBuilder(2048);
            html.Append("<!doctype html><html><head><meta charset=\"utf-8\" />");
            html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
            html.Append("<title>BostonUnlocked Server Status</title>");
            html.Append("<style>");
            html.Append("body{margin:0;background:#0f1419;color:#d9e1ea;font-family:Segoe UI,Arial,sans-serif;}");
            html.Append(".wrap{max-width:980px;margin:32px auto;padding:0 16px;}");
            html.Append(".card{background:#171d24;border:1px solid #2b3642;border-radius:10px;padding:16px 18px;box-shadow:0 8px 28px rgba(0,0,0,0.35);}");
            html.Append("h1{margin:0 0 4px 0;font-size:20px;font-weight:600;color:#eff5fb;}");
            html.Append("h2{margin:16px 0 8px 0;font-size:18px;color:#eff5fb;}");
            html.Append("h3{margin:0 0 6px 0;font-size:15px;color:#dce7f3;}");
            html.Append(".muted{color:#8ea0b2;font-size:13px;}");
            html.Append(".row{display:flex;flex-wrap:wrap;gap:10px;margin:14px 0 12px 0;}");
            html.Append(".pill{background:#1f2833;border:1px solid #304052;border-radius:999px;padding:7px 11px;font-size:13px;}");
            html.Append(".ok{color:#7ee787;border-color:#2f6d4f;background:#133124;}");
            html.Append(".act{margin-top:14px;padding:10px 12px;background:#111820;border:1px solid #283444;border-radius:10px;}");
            html.Append(".act h2{margin-top:0;}");
            html.Append(".section{margin-top:8px;padding:10px;background:#1a232d;border:1px solid #304052;border-radius:8px;}");
            html.Append(".party{background:#1a232d;border:1px solid #304052;border-radius:8px;padding:8px 12px;margin:8px 0;}");
            html.Append(".leader{color:#f0c040;font-size:12px;margin-left:6px;}");
            html.Append("ul{margin:10px 0 0 18px;padding:0;}");
            html.Append("li{margin:5px 0;}");
            html.Append("a{color:#8dc7ff;text-decoration:none;}a:hover{text-decoration:underline;}");
            html.Append("</style></head><body><div class=\"wrap\"><div class=\"card\">");
            html.Append("<h1>BostonUnlocked Server Status</h1>");
            html.Append("<div class=\"row\">");
            html.Append("<div class=\"pill ok\">Status: Online</div>");
            html.Append("<div class=\"pill\">Current players: ");
            html.Append(allPlayers.Count.ToString(CultureInfo.InvariantCulture));
            html.Append("</div></div>");
            html.Append("<div><strong>Logged-in players by act and location</strong></div>");

            if (allPlayers.Count == 0)
            {
                html.Append("<div class=\"muted\" style=\"margin-top:8px;\">No players currently logged in.</div>");
            }
            else
            {
                var actOrder = new int[] { 1, 2, 3, 4 };
                for (var a = 0; a < actOrder.Length; a++)
                {
                    var actNumber = actOrder[a];
                    ActStatusBucket act;
                    if (!acts.TryGetValue(actNumber, out act) || act == null || act.TotalCount <= 0)
                    {
                        continue;
                    }

                    html.Append("<div class=\"act\">");
                    html.Append("<h2>");
                    html.Append(HtmlEncode(GetActDisplayName(actNumber)));
                    html.Append("</h2>");

                    var hubKeys = new List<string>(act.HubPlayersByHubId.Keys);
                    hubKeys.Sort(StringComparer.OrdinalIgnoreCase);
                    for (var h = 0; h < hubKeys.Count; h++)
                    {
                        var hubId = hubKeys[h];
                        List<StatusPlayerRecord> hubPlayers;
                        if (!act.HubPlayersByHubId.TryGetValue(hubId, out hubPlayers) || hubPlayers == null || hubPlayers.Count == 0)
                        {
                            continue;
                        }

                        html.Append("<div class=\"section\">");
                        html.Append("<h3>");
                        html.Append(HtmlEncode(hubId));
                        html.Append("</h3>");
                        AppendGroupedPlayersHtml(html, hubPlayers);
                        html.Append("</div>");
                    }

                    var missionKeys = new List<string>(act.MissionPlayersByMapName.Keys);
                    missionKeys.Sort(StringComparer.OrdinalIgnoreCase);
                    for (var m = 0; m < missionKeys.Count; m++)
                    {
                        var mapName = missionKeys[m];
                        List<StatusPlayerRecord> missionPlayers;
                        if (!act.MissionPlayersByMapName.TryGetValue(mapName, out missionPlayers) || missionPlayers == null || missionPlayers.Count == 0)
                        {
                            continue;
                        }

                        var missionTitle = !IsNullOrWhiteSpace(mapName)
                            ? ResolveMissionDisplayName(mapName)
                            : "Unknown Mission";

                        html.Append("<div class=\"section\">");
                        html.Append("<h3>Mission: ");
                        html.Append(HtmlEncode(missionTitle));
                        html.Append("</h3>");
                        AppendGroupedPlayersHtml(html, missionPlayers);
                        html.Append("</div>");
                    }

                    html.Append("</div>");
                }
            }

            html.Append("<div class=\"muted\" style=\"margin-top:14px;\">Rendered: ");
            html.Append(HtmlEncode(renderedAtUtc));
            html.Append(" | Refresh the page to update values.</div>");
            html.Append("</div></div></body></html>");
            return html.ToString();
        }

        private sealed class StatusPlayerRecord
        {
            public Guid AccountId;
            public string IdentityHash;
            public string CharacterName;
            public string AccountDisplayName;
            public int Chapter;
            public string HubId;
            public string MissionMapName;
        }

        private sealed class ActStatusBucket
        {
            public readonly Dictionary<string, List<StatusPlayerRecord>> HubPlayersByHubId = new Dictionary<string, List<StatusPlayerRecord>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<StatusPlayerRecord>> MissionPlayersByMapName = new Dictionary<string, List<StatusPlayerRecord>>(StringComparer.OrdinalIgnoreCase);

            public int TotalCount
            {
                get
                {
                    var total = 0;
                    foreach (var entry in HubPlayersByHubId)
                    {
                        if (entry.Value != null)
                        {
                            total += entry.Value.Count;
                        }
                    }

                    foreach (var entry in MissionPlayersByMapName)
                    {
                        if (entry.Value != null)
                        {
                            total += entry.Value.Count;
                        }
                    }

                    return total;
                }
            }
        }

        private Dictionary<int, ActStatusBucket> BuildActBuckets(List<StatusPlayerRecord> players)
        {
            var result = new Dictionary<int, ActStatusBucket>();
            if (players == null || players.Count == 0)
            {
                return result;
            }

            var playersByAccount = new Dictionary<Guid, StatusPlayerRecord>();
            for (var i = 0; i < players.Count; i++)
            {
                var record = players[i];
                if (record != null && record.AccountId != Guid.Empty)
                {
                    playersByAccount[record.AccountId] = record;
                }
            }

            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null)
                {
                    continue;
                }

                var locationSource = player;
                Guid hostId;
                if (player.AccountId != Guid.Empty
                    && PartyHubFollowRegistry.TryGetHostForMember(player.AccountId, out hostId)
                    && hostId != Guid.Empty
                    && hostId != player.AccountId)
                {
                    StatusPlayerRecord host;
                    if (playersByAccount.TryGetValue(hostId, out host) && host != null)
                    {
                        locationSource = host;
                    }
                }

                var act = ResolveActNumber(locationSource.Chapter);
                ActStatusBucket bucket;
                if (!result.TryGetValue(act, out bucket) || bucket == null)
                {
                    bucket = new ActStatusBucket();
                    result[act] = bucket;
                }

                if (!IsNullOrWhiteSpace(locationSource.MissionMapName))
                {
                    List<StatusPlayerRecord> missionPlayers;
                    if (!bucket.MissionPlayersByMapName.TryGetValue(locationSource.MissionMapName, out missionPlayers) || missionPlayers == null)
                    {
                        missionPlayers = new List<StatusPlayerRecord>();
                        bucket.MissionPlayersByMapName[locationSource.MissionMapName] = missionPlayers;
                    }

                    missionPlayers.Add(player);
                    continue;
                }

                var hubId = NormalizeHubStatusContainerName(locationSource.HubId);
                List<StatusPlayerRecord> hubPlayers;
                if (!bucket.HubPlayersByHubId.TryGetValue(hubId, out hubPlayers) || hubPlayers == null)
                {
                    hubPlayers = new List<StatusPlayerRecord>();
                    bucket.HubPlayersByHubId[hubId] = hubPlayers;
                }

                hubPlayers.Add(player);
            }

            return result;
        }

        private List<StatusPlayerRecord> BuildOnlineStatusPlayers()
        {
            var onlineAccountIds = AccountTransportLivenessRegistry.SnapshotOnlineAccountIds();
            if (onlineAccountIds == null || onlineAccountIds.Length == 0)
            {
                return new List<StatusPlayerRecord>();
            }

            var deduplicatedOnlineIds = new Dictionary<Guid, bool>();
            for (var i = 0; i < onlineAccountIds.Length; i++)
            {
                var accountId = onlineAccountIds[i];
                if (accountId != Guid.Empty)
                {
                    deduplicatedOnlineIds[accountId] = true;
                }
            }

            var hubParticipants = _hubPresenceRegistry != null
                ? BuildStatusPlayerEntries(_hubPresenceRegistry.SnapshotParticipants())
                : new List<HubPresenceRegistry.Participant>();
            var hubByAccountId = new Dictionary<Guid, HubPresenceRegistry.Participant>();
            for (var i = 0; i < hubParticipants.Count; i++)
            {
                var participant = hubParticipants[i];
                if (participant != null && participant.AccountId != Guid.Empty)
                {
                    hubByAccountId[participant.AccountId] = participant;
                }
            }

            var missionByAccountId = new Dictionary<Guid, MissionRuntimeRegistry.MissionRuntimeParticipant>();
            var missionParticipants = MissionRuntimeRegistry.SnapshotParticipants();
            if (missionParticipants != null)
            {
                for (var i = 0; i < missionParticipants.Length; i++)
                {
                    var missionParticipant = missionParticipants[i];
                    if (missionParticipant.AccountId == Guid.Empty)
                    {
                        continue;
                    }

                    MissionRuntimeRegistry.MissionRuntimeParticipant existing;
                    if (!missionByAccountId.TryGetValue(missionParticipant.AccountId, out existing)
                        || IsNullOrWhiteSpace(existing.MapName) && !IsNullOrWhiteSpace(missionParticipant.MapName))
                    {
                        missionByAccountId[missionParticipant.AccountId] = missionParticipant;
                    }
                }
            }

            var result = new List<StatusPlayerRecord>(deduplicatedOnlineIds.Count);
            foreach (var kvp in deduplicatedOnlineIds)
            {
                var accountId = kvp.Key;
                var identityHash = accountId.ToString("D");

                HubPresenceRegistry.Participant hubParticipant;
                hubByAccountId.TryGetValue(accountId, out hubParticipant);

                MissionRuntimeRegistry.MissionRuntimeParticipant missionParticipant;
                var inMission = missionByAccountId.TryGetValue(accountId, out missionParticipant);

                var slot = TryResolvePreferredCareerSlot(identityHash);
                var characterName = ResolveCharacterNameForStatus(hubParticipant, slot);
                var accountDisplayName = ResolveAccountDisplayNameForIdentity(identityHash, accountId);
                var chapter = slot != null ? slot.MainCampaignCurrentChapter : 0;
                var hubId = ResolveHubIdForStatus(hubParticipant, slot);

                result.Add(new StatusPlayerRecord
                {
                    AccountId = accountId,
                    IdentityHash = identityHash,
                    CharacterName = characterName,
                    AccountDisplayName = accountDisplayName,
                    Chapter = chapter,
                    HubId = hubId,
                    MissionMapName = inMission ? missionParticipant.MapName : null,
                });
            }

            result.Sort(delegate(StatusPlayerRecord a, StatusPlayerRecord b)
            {
                return string.Compare(BuildStatusPlayerEntry(a), BuildStatusPlayerEntry(b), StringComparison.OrdinalIgnoreCase);
            });

            return result;
        }

        private CareerSlot TryResolvePreferredCareerSlot(string identityHash)
        {
            if (_userStore == null || IsNullOrWhiteSpace(identityHash))
            {
                return null;
            }

            List<CareerSlot> careers;
            int lastCareerIndex;
            try
            {
                careers = _userStore.GetCareers(identityHash);
                lastCareerIndex = _userStore.GetLastCareerIndex(identityHash);
            }
            catch
            {
                return null;
            }

            if (careers == null || careers.Count == 0)
            {
                return null;
            }

            CareerSlot fallback = null;
            for (var i = 0; i < careers.Count; i++)
            {
                var slot = careers[i];
                if (slot == null)
                {
                    continue;
                }

                if (fallback == null)
                {
                    fallback = slot;
                }

                if (!slot.IsOccupied)
                {
                    continue;
                }

                if (slot.Index == lastCareerIndex)
                {
                    return slot;
                }

                if (fallback == null || !fallback.IsOccupied)
                {
                    fallback = slot;
                }
            }

            return fallback;
        }

        private string ResolveCharacterNameForStatus(HubPresenceRegistry.Participant hubParticipant, CareerSlot slot)
        {
            if (hubParticipant != null && !IsNullOrWhiteSpace(hubParticipant.CharacterName))
            {
                return hubParticipant.CharacterName.Trim();
            }

            if (slot != null && !IsNullOrWhiteSpace(slot.CharacterName))
            {
                return slot.CharacterName.Trim();
            }

            return "Unknown Character";
        }

        private string ResolveHubIdForStatus(HubPresenceRegistry.Participant hubParticipant, CareerSlot slot)
        {
            if (hubParticipant != null && !IsNullOrWhiteSpace(hubParticipant.HubId))
            {
                return hubParticipant.HubId.Trim();
            }

            if (slot != null && !IsNullOrWhiteSpace(slot.HubId))
            {
                return slot.HubId.Trim();
            }

            return "Unknown Hub";
        }

        private string ResolveAccountDisplayNameForIdentity(string identityHash, Guid accountId)
        {
            string resolved;
            try
            {
                resolved = _userStore != null ? _userStore.GetDisplayName(identityHash) : null;
            }
            catch
            {
                resolved = null;
            }

            if (!IsNullOrWhiteSpace(resolved))
            {
                return resolved.Trim();
            }

            return accountId != Guid.Empty ? accountId.ToString("D") : "Unknown Account";
        }

        private void AppendGroupedPlayersHtml(StringBuilder html, List<StatusPlayerRecord> players)
        {
            if (html == null)
            {
                return;
            }

            if (players == null || players.Count == 0)
            {
                html.Append("<div class=\"muted\">No players in this location.</div>");
                return;
            }

            var partyGroups = new Dictionary<Guid, List<StatusPlayerRecord>>();
            var soloPlayers = new List<StatusPlayerRecord>();

            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null)
                {
                    continue;
                }

                Guid hostId;
                if (player.AccountId != Guid.Empty && PartyHubFollowRegistry.TryGetHostForMember(player.AccountId, out hostId))
                {
                    List<StatusPlayerRecord> group;
                    if (!partyGroups.TryGetValue(hostId, out group) || group == null)
                    {
                        group = new List<StatusPlayerRecord>();
                        partyGroups[hostId] = group;
                    }

                    group.Add(player);
                    continue;
                }

                soloPlayers.Add(player);
            }

            for (var i = soloPlayers.Count - 1; i >= 0; i--)
            {
                var player = soloPlayers[i];
                if (player != null && player.AccountId != Guid.Empty && partyGroups.ContainsKey(player.AccountId))
                {
                    partyGroups[player.AccountId].Insert(0, player);
                    soloPlayers.RemoveAt(i);
                }
            }

            var sortedGroupKeys = new List<Guid>(partyGroups.Keys);
            for (var i = 0; i < sortedGroupKeys.Count; i++)
            {
                var hostKey = sortedGroupKeys[i];
                var members = partyGroups[hostKey];
                members.Sort(delegate(StatusPlayerRecord a, StatusPlayerRecord b)
                {
                    return string.Compare(BuildStatusPlayerEntry(a), BuildStatusPlayerEntry(b), StringComparison.OrdinalIgnoreCase);
                });

                for (var m = 0; m < members.Count; m++)
                {
                    if (members[m].AccountId == hostKey && m > 0)
                    {
                        var leader = members[m];
                        members.RemoveAt(m);
                        members.Insert(0, leader);
                        break;
                    }
                }
            }

            sortedGroupKeys.Sort(delegate(Guid a, Guid b)
            {
                var ga = partyGroups[a];
                var gb = partyGroups[b];
                var nameA = ga.Count > 0 ? BuildStatusPlayerEntry(ga[0]) : string.Empty;
                var nameB = gb.Count > 0 ? BuildStatusPlayerEntry(gb[0]) : string.Empty;
                return string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
            });

            soloPlayers.Sort(delegate(StatusPlayerRecord a, StatusPlayerRecord b)
            {
                return string.Compare(BuildStatusPlayerEntry(a), BuildStatusPlayerEntry(b), StringComparison.OrdinalIgnoreCase);
            });

            for (var g = 0; g < sortedGroupKeys.Count; g++)
            {
                var hostId = sortedGroupKeys[g];
                var members = partyGroups[hostId];
                html.Append("<div class=\"party\"><ul style=\"margin-top:4px;\">");
                for (var m = 0; m < members.Count; m++)
                {
                    var member = members[m];
                    html.Append("<li>");
                    html.Append(HtmlEncode(BuildStatusPlayerEntry(member)));
                    if (member.AccountId == hostId)
                    {
                        html.Append("<span class=\"leader\">&#9733;</span>");
                    }
                    html.Append("</li>");
                }
                html.Append("</ul></div>");
            }

            if (soloPlayers.Count > 0)
            {
                html.Append("<ul>");
                for (var i = 0; i < soloPlayers.Count; i++)
                {
                    html.Append("<li>");
                    html.Append(HtmlEncode(BuildStatusPlayerEntry(soloPlayers[i])));
                    html.Append("</li>");
                }
                html.Append("</ul>");
            }
        }

        private string BuildStatusPlayerEntry(StatusPlayerRecord player)
        {
            if (player == null)
            {
                return "Unknown Character (Unknown Account)";
            }

            var characterName = !IsNullOrWhiteSpace(player.CharacterName)
                ? player.CharacterName.Trim()
                : "Unknown Character";
            var accountDisplayName = !IsNullOrWhiteSpace(player.AccountDisplayName)
                ? player.AccountDisplayName.Trim()
                : (player.AccountId != Guid.Empty ? player.AccountId.ToString("D") : "Unknown Account");
            return characterName + " (" + accountDisplayName + ")";
        }

        private static int ResolveActNumber(int chapter)
        {
            if (chapter < 6)
            {
                return 1;
            }

            if (chapter <= 16)
            {
                return 2;
            }

            if (chapter <= 32)
            {
                return 3;
            }

            return 4;
        }

        private static string GetActDisplayName(int actNumber)
        {
            switch (actNumber)
            {
                case 1:
                    return "Act 1";
                case 2:
                    return "Act 2";
                case 3:
                    return "Act 3";
                default:
                    return "Act 4";
            }
        }

        private static string NormalizeHubStatusContainerName(string hubId)
        {
            if (!IsNullOrWhiteSpace(hubId) && hubId.StartsWith("Matrix_", StringComparison.OrdinalIgnoreCase))
            {
                return "Matrix";
            }

            return "Hub";
        }

        private string ResolveMissionDisplayName(string mapName)
        {
            if (IsNullOrWhiteSpace(mapName))
            {
                return "Unknown Mission";
            }

            var missionNames = GetMissionEnglishNameMap();
            string resolved;
            if (missionNames != null && missionNames.TryGetValue(mapName, out resolved) && !IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            return mapName;
        }

        private Dictionary<string, string> GetMissionEnglishNameMap()
        {
            var now = DateTime.UtcNow;
            if (_cachedMissionEnglishNames != null && now - _cachedMissionEnglishNamesLoadedUtc < MissionNameCacheDuration)
            {
                return _cachedMissionEnglishNames;
            }

            lock (_missionNameCacheLock)
            {
                now = DateTime.UtcNow;
                if (_cachedMissionEnglishNames != null && now - _cachedMissionEnglishNamesLoadedUtc < MissionNameCacheDuration)
                {
                    return _cachedMissionEnglishNames;
                }

                _cachedMissionEnglishNames = LoadMissionEnglishNameMap();
                _cachedMissionEnglishNamesLoadedUtc = now;
                return _cachedMissionEnglishNames;
            }
        }

        private Dictionary<string, string> LoadMissionEnglishNameMap()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> missionToDisplayKey;
            Dictionary<string, string> englishTable;
            try
            {
                missionToDisplayKey = LoadMissionDisplayKeyMap();
                englishTable = LoadEnglishLocalizationTable();
            }
            catch
            {
                return result;
            }

            if (missionToDisplayKey == null || missionToDisplayKey.Count == 0)
            {
                return result;
            }

            foreach (var entry in missionToDisplayKey)
            {
                var mapName = entry.Key;
                var displayKey = entry.Value;
                if (IsNullOrWhiteSpace(mapName))
                {
                    continue;
                }

                string localized;
                if (!IsNullOrWhiteSpace(displayKey)
                    && englishTable != null
                    && englishTable.TryGetValue(displayKey, out localized)
                    && !IsNullOrWhiteSpace(localized))
                {
                    result[mapName] = localized;
                }
            }

            return result;
        }

        private Dictionary<string, string> LoadMissionDisplayKeyMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_options == null || IsNullOrWhiteSpace(_options.StaticDataDir))
            {
                return map;
            }

            var globalsPath = Path.Combine(_options.StaticDataDir, "globals.json");
            if (!File.Exists(globalsPath))
            {
                return map;
            }

            var json = File.ReadAllText(globalsPath);
            if (IsNullOrWhiteSpace(json))
            {
                return map;
            }

            var root = Json.DeserializeObject(json) as IDictionary;
            if (root == null || !root.Contains("Components"))
            {
                return map;
            }

            var components = root["Components"] as IList;
            if (components == null)
            {
                return map;
            }

            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i] as IDictionary;
                if (component == null || !component.Contains("MissionDefinitions"))
                {
                    continue;
                }

                var missionDefinitions = component["MissionDefinitions"] as IList;
                if (missionDefinitions == null)
                {
                    continue;
                }

                for (var m = 0; m < missionDefinitions.Count; m++)
                {
                    var mission = missionDefinitions[m] as IDictionary;
                    if (mission == null)
                    {
                        continue;
                    }

                    var mapName = GetString(mission, "Name");
                    if (IsNullOrWhiteSpace(mapName))
                    {
                        continue;
                    }

                    var displayKey = GetString(mission, "DisplayName");
                    if (!IsNullOrWhiteSpace(displayKey))
                    {
                        map[mapName] = displayKey;
                    }
                }
            }

            return map;
        }

        private Dictionary<string, string> LoadEnglishLocalizationTable()
        {
            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_options == null || IsNullOrWhiteSpace(_options.StreamingAssetsDir))
            {
                return table;
            }

            var englishPath = Path.Combine(Path.Combine(_options.StreamingAssetsDir, "localization"), "English.csv");
            if (!File.Exists(englishPath))
            {
                return table;
            }

            var lines = File.ReadAllLines(englishPath);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (i == 0 && line.StartsWith("id;", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf(';');
                if (separatorIndex <= 0 || separatorIndex + 1 >= line.Length)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim();
                if (IsNullOrWhiteSpace(key) || IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                table[key] = value.Replace("\\n", " ");
            }

            return table;
        }

        private string GetCachedServerStatusPageHtml()
        {
            var now = DateTime.UtcNow;
            var age = now - _cachedStatusPageGeneratedUtc;
            if (_cachedStatusPageHtml != null && age < StatusPageCacheDuration)
            {
                return _cachedStatusPageHtml;
            }

            lock (_statusPageCacheLock)
            {
                now = DateTime.UtcNow;
                age = now - _cachedStatusPageGeneratedUtc;
                if (_cachedStatusPageHtml != null && age < StatusPageCacheDuration)
                {
                    return _cachedStatusPageHtml;
                }

                _cachedStatusPageHtml = BuildServerStatusPageHtml();
                _cachedStatusPageGeneratedUtc = now;
                return _cachedStatusPageHtml;
            }
        }

        private List<HubPresenceRegistry.Participant> BuildStatusPlayerEntries(HubPresenceRegistry.Participant[] participants)
        {
            if (participants == null || participants.Length == 0)
            {
                return new List<HubPresenceRegistry.Participant>();
            }

            var byAccount = new Dictionary<Guid, HubPresenceRegistry.Participant>();
            var byPeer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var looseParticipants = new List<HubPresenceRegistry.Participant>();

            for (var i = 0; i < participants.Length; i++)
            {
                var participant = participants[i];
                if (participant == null)
                {
                    continue;
                }

                var hasCharacterName = !IsNullOrWhiteSpace(participant.CharacterName);
                if (participant.AccountId != Guid.Empty)
                {
                    HubPresenceRegistry.Participant existing;
                    if (!byAccount.TryGetValue(participant.AccountId, out existing) || existing == null)
                    {
                        byAccount[participant.AccountId] = participant;
                        continue;
                    }

                    if (hasCharacterName && IsNullOrWhiteSpace(existing.CharacterName))
                    {
                        byAccount[participant.AccountId] = participant;
                    }
                    continue;
                }

                if (!IsNullOrWhiteSpace(participant.Peer) && !byPeer.Add(participant.Peer))
                {
                    continue;
                }

                looseParticipants.Add(participant);
            }

            var result = new List<HubPresenceRegistry.Participant>(byAccount.Count + looseParticipants.Count);
            foreach (var participant in byAccount.Values)
            {
                result.Add(participant);
            }

            for (var i = 0; i < looseParticipants.Count; i++)
            {
                result.Add(looseParticipants[i]);
            }

            return result;
        }

        private string BuildStatusPlayerEntry(HubPresenceRegistry.Participant participant)
        {
            if (participant == null)
            {
                return "Unknown Character (Unknown Account)";
            }

            var characterName = !IsNullOrWhiteSpace(participant.CharacterName)
                ? participant.CharacterName.Trim()
                : "Unknown Character";
            var accountDisplayName = ResolveAccountDisplayName(participant);
            return characterName + " (" + accountDisplayName + ")";
        }

        private string ResolveAccountDisplayName(HubPresenceRegistry.Participant participant)
        {
            var fallback = participant != null && participant.AccountId != Guid.Empty
                ? participant.AccountId.ToString("D")
                : "Unknown Account";

            if (participant == null || _userStore == null)
            {
                return fallback;
            }

            var identityHash = participant.IdentityHash;
            if (IsNullOrWhiteSpace(identityHash) && participant.AccountId != Guid.Empty)
            {
                identityHash = participant.AccountId.ToString("D");
            }

            if (IsNullOrWhiteSpace(identityHash))
            {
                return fallback;
            }

            string resolved;
            try
            {
                resolved = _userStore.GetDisplayName(identityHash);
            }
            catch
            {
                resolved = null;
            }

            return IsNullOrWhiteSpace(resolved) ? fallback : resolved.Trim();
        }

        private static string HtmlEncode(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
        }

        public interface IPlayerInfoRepository
        {
            Dictionary<string, string> Get(string identityHash, string gameName);
            PlayerInfoChanges Set(string identityHash, string gameName, Dictionary<string, string> updates);
            List<KeyValuePair<string, Dictionary<string, string>>> Search(string gameName, string searchString);
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

        private static HttpResponse FileResponse(int statusCode, string filePath, string contentType)
        {
            return new HttpResponse
            {
                StatusCode = statusCode,
                ReasonPhrase = statusCode == 200 ? "OK" : (statusCode == 404 ? "Not Found" : "Error"),
                ContentType = contentType,
                BodyFilePath = filePath,
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
