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
