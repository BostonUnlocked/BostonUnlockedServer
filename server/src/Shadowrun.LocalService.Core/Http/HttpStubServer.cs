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

        private readonly LocalServiceOptions _options;
        private readonly RequestLogger _logger;
        private readonly ISessionIdentityMap _sessionIdentityMap;
        private readonly IPlayerInfoRepository _playerInfoRepository;
        private readonly LocalUserStore _userStore;
        private readonly HubPresenceRegistry _hubPresenceRegistry;
        private readonly object _statusPageCacheLock = new object();
        private string _cachedStatusPageHtml;
        private DateTime _cachedStatusPageGeneratedUtc = DateTime.MinValue;

        private static readonly TimeSpan StatusPageCacheDuration = TimeSpan.FromSeconds(10);

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
                            _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "http-error", connectionHash = connectionHash, peer = endpoint, message = ex.Message });
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
                }
                finally
                {
                    _logger.ClearConnectionContext("http", endpoint, connectionHash);
                }
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

            var raw = Encoding.UTF8.GetString(bodyBytes);
            if (!ShouldRedactSensitiveBody(path))
            {
                return raw;
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
            var participants = _hubPresenceRegistry != null
                ? _hubPresenceRegistry.SnapshotParticipants()
                : new HubPresenceRegistry.Participant[0];
            var deduplicated = BuildStatusPlayerEntries(participants);
            var renderedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

            // ── Group by party using PartyHubFollowRegistry ──
            var partyGroups = new Dictionary<Guid, List<HubPresenceRegistry.Participant>>();
            var soloPlayers = new List<HubPresenceRegistry.Participant>();

            for (var i = 0; i < deduplicated.Count; i++)
            {
                var p = deduplicated[i];
                Guid hostId;
                if (p.AccountId != Guid.Empty && PartyHubFollowRegistry.TryGetHostForMember(p.AccountId, out hostId))
                {
                    List<HubPresenceRegistry.Participant> group;
                    if (!partyGroups.TryGetValue(hostId, out group))
                    {
                        group = new List<HubPresenceRegistry.Participant>();
                        partyGroups[hostId] = group;
                    }
                    group.Add(p);
                }
                else
                {
                    soloPlayers.Add(p);
                }
            }

            // Move hosts from solo into their party group at position 0 (leader)
            for (var i = soloPlayers.Count - 1; i >= 0; i--)
            {
                var p = soloPlayers[i];
                if (p.AccountId != Guid.Empty && partyGroups.ContainsKey(p.AccountId))
                {
                    partyGroups[p.AccountId].Insert(0, p);
                    soloPlayers.RemoveAt(i);
                }
            }

            // Sort members inside each group: alphabetically, then leader to front
            var sortedGroupKeys = new List<Guid>(partyGroups.Keys);
            foreach (var hostKey in sortedGroupKeys)
            {
                var members = partyGroups[hostKey];
                members.Sort(delegate(HubPresenceRegistry.Participant a, HubPresenceRegistry.Participant b)
                {
                    return string.Compare(BuildStatusPlayerEntry(a), BuildStatusPlayerEntry(b), StringComparison.OrdinalIgnoreCase);
                });
                for (var j = 0; j < members.Count; j++)
                {
                    if (members[j].AccountId == hostKey && j > 0)
                    {
                        var leader = members[j];
                        members.RemoveAt(j);
                        members.Insert(0, leader);
                        break;
                    }
                }
            }

            // Sort party groups by leader / first-member display name
            sortedGroupKeys.Sort(delegate(Guid a, Guid b)
            {
                var ga = partyGroups[a];
                var gb = partyGroups[b];
                var nameA = ga.Count > 0 ? BuildStatusPlayerEntry(ga[0]) : "";
                var nameB = gb.Count > 0 ? BuildStatusPlayerEntry(gb[0]) : "";
                return string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
            });

            // Sort solo players alphabetically
            soloPlayers.Sort(delegate(HubPresenceRegistry.Participant a, HubPresenceRegistry.Participant b)
            {
                return string.Compare(BuildStatusPlayerEntry(a), BuildStatusPlayerEntry(b), StringComparison.OrdinalIgnoreCase);
            });

            // ── Render ──
            var html = new StringBuilder(2048);
            html.Append("<!doctype html><html><head><meta charset=\"utf-8\" />");
            html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
            html.Append("<title>BostonUnlocked Server Status</title>");
            html.Append("<style>");
            html.Append("body{margin:0;background:#0f1419;color:#d9e1ea;font-family:Segoe UI,Arial,sans-serif;}");
            html.Append(".wrap{max-width:760px;margin:32px auto;padding:0 16px;}");
            html.Append(".card{background:#171d24;border:1px solid #2b3642;border-radius:10px;padding:16px 18px;box-shadow:0 8px 28px rgba(0,0,0,0.35);}");
            html.Append("h1{margin:0 0 4px 0;font-size:20px;font-weight:600;color:#eff5fb;}");
            html.Append(".muted{color:#8ea0b2;font-size:13px;}");
            html.Append(".row{display:flex;flex-wrap:wrap;gap:10px;margin:14px 0 12px 0;}");
            html.Append(".pill{background:#1f2833;border:1px solid #304052;border-radius:999px;padding:7px 11px;font-size:13px;}");
            html.Append(".ok{color:#7ee787;border-color:#2f6d4f;background:#133124;}");
            html.Append(".party{background:#1a232d;border:1px solid #304052;border-radius:8px;padding:8px 12px;margin:8px 0;}");
            html.Append(".leader{color:#f0c040;font-size:12px;margin-left:4px;}");
            html.Append("ul{margin:10px 0 0 18px;padding:0;}");
            html.Append("li{margin:5px 0;}");
            html.Append("a{color:#8dc7ff;text-decoration:none;}a:hover{text-decoration:underline;}");
            html.Append("</style></head><body><div class=\"wrap\"><div class=\"card\">");
            html.Append("<h1>BostonUnlocked Server Status</h1>");
            html.Append("<div class=\"row\">");
            html.Append("<div class=\"pill ok\">Status: Online</div>");
            html.Append("<div class=\"pill\">Current players: ");
            html.Append(deduplicated.Count.ToString(CultureInfo.InvariantCulture));
            html.Append("</div></div>");
            html.Append("<div><strong>Logged-in players</strong></div>");

            if (deduplicated.Count == 0)
            {
                html.Append("<div class=\"muted\" style=\"margin-top:8px;\">No players currently logged in.</div>");
            }
            else
            {
                for (var g = 0; g < sortedGroupKeys.Count; g++)
                {
                    var groupHostId = sortedGroupKeys[g];
                    var members = partyGroups[groupHostId];
                    html.Append("<div class=\"party\">");
                    html.Append("<ul style=\"margin-top:4px;\">");
                    for (var m = 0; m < members.Count; m++)
                    {
                        html.Append("<li>");
                        html.Append(HtmlEncode(BuildStatusPlayerEntry(members[m])));
                        if (members[m].AccountId == groupHostId)
                        {
                            html.Append("<span class=\"leader\">\u2605</span>");
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

            html.Append("<div class=\"muted\" style=\"margin-top:14px;\">Rendered: ");
            html.Append(HtmlEncode(renderedAtUtc));
            html.Append(" | Refresh the page to update values.</div>");
            html.Append("</div></div></body></html>");
            return html.ToString();
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
