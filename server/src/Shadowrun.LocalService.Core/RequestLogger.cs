using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Shadowrun.LocalService.Core
{
public sealed class RequestLogger
{
    private const string EventsStream = "events";
    private const string DiagnosticsStream = "diagnostics";
    private const string PlayerBugsStream = "player-bugs";
    private const string SchemaVersion = "1";

    private static readonly JavaScriptSerializer Json = CreateSerializer();
    private static long _connectionSequence;

    private readonly string _eventsPrefix;
    private readonly string _diagnosticsPrefix;
    private readonly string _playerBugsPrefix;
    private readonly bool _fileLoggingEnabled;
    private readonly object _writeLock = new object();
    private readonly object _contextLock = new object();
    private readonly TimeSpan _rotationInterval;
    private readonly TimeSpan _retentionPeriod;
    private readonly Dictionary<string, StreamState> _streamStateByName = new Dictionary<string, StreamState>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConnectionContext> _contextByConnectionHash = new Dictionary<string, ConnectionContext>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _connectionHashByProtocolPeer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastCleanupUtc;

    public RequestLogger(string eventsPrefix, string diagnosticsPrefix)
        : this(eventsPrefix, diagnosticsPrefix, null, 5, 1)
    {
    }

    public RequestLogger(string eventsPrefix, string diagnosticsPrefix, int rotationIntervalMinutes, int retentionDays)
        : this(eventsPrefix, diagnosticsPrefix, null, rotationIntervalMinutes, retentionDays)
    {
    }

    public RequestLogger(string eventsPrefix, string diagnosticsPrefix, string playerBugsPrefix, int rotationIntervalMinutes, int retentionDays)
    {
        if (IsNullOrWhiteSpace(eventsPrefix))
        {
            _eventsPrefix = null;
            _diagnosticsPrefix = null;
            _playerBugsPrefix = null;
            _fileLoggingEnabled = false;
            _rotationInterval = TimeSpan.FromMinutes(5);
            _retentionPeriod = TimeSpan.FromDays(1);
            return;
        }

        _eventsPrefix = eventsPrefix;
        _diagnosticsPrefix = IsNullOrWhiteSpace(diagnosticsPrefix) ? null : diagnosticsPrefix;
        _playerBugsPrefix = IsNullOrWhiteSpace(playerBugsPrefix) ? null : playerBugsPrefix;
        _fileLoggingEnabled = true;
        _rotationInterval = TimeSpan.FromMinutes(rotationIntervalMinutes > 0 ? rotationIntervalMinutes : 5);
        _retentionPeriod = TimeSpan.FromDays(retentionDays > 0 ? retentionDays : 1);

        EnsureParentDirectory(_eventsPrefix);
        if (_diagnosticsPrefix != null)
        {
            EnsureParentDirectory(_diagnosticsPrefix);
        }
        if (_playerBugsPrefix != null)
        {
            EnsureParentDirectory(_playerBugsPrefix);
        }
    }

    public void Reset()
    {
        CleanupExpiredFiles(DateTimeOffset.UtcNow);
    }

    public void Log(object payload)
    {
        WriteStructured(EventsStream, "info", payload);
    }

    public void LogLow(object payload)
    {
        WriteStructured(DiagnosticsStream, "debug", payload);
    }

    public void LogAi(object payload)
    {
        WriteStructured(DiagnosticsStream, "debug", payload);
    }

    public void LogAdmin(object payload)
    {
        WriteStructured(EventsStream, "info", payload);
    }

    public void LogPlayerBug(object payload)
    {
        WriteStructured(PlayerBugsStream, "info", payload);
    }

    public static string UtcNowIso()
    {
        return DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
    }

    public static string CreateConnectionHash(string protocol, string remoteEndpoint)
    {
        var sequence = Interlocked.Increment(ref _connectionSequence);
        return ComputeStableConnectionHash((protocol ?? string.Empty) + "|" + (remoteEndpoint ?? string.Empty) + "|" + sequence.ToString(CultureInfo.InvariantCulture));
    }

    public static string ComputeStableConnectionHash(string value)
    {
        unchecked
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            var hash = offsetBasis;
            var text = value ?? string.Empty;
            for (var i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= prime;
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
    }

    public void RegisterConnectionContext(string protocol, string peer, string connectionHash)
    {
        if (!_fileLoggingEnabled || IsNullOrWhiteSpace(connectionHash))
        {
            return;
        }

        lock (_contextLock)
        {
            var context = GetOrCreateContext_NoLock(protocol, peer, connectionHash);
            if (context == null)
            {
                return;
            }

            context.Protocol = IsNullOrWhiteSpace(protocol) ? context.Protocol : protocol;
            context.Peer = IsNullOrWhiteSpace(peer) ? context.Peer : peer;
        }
    }

    public void UpdateConnectionAccountId(string protocol, string peer, string connectionHash, Guid accountId)
    {
        UpdateConnectionAccountId(protocol, peer, connectionHash, GuidToString(accountId));
    }

    public void UpdateConnectionAccountId(string protocol, string peer, string connectionHash, string accountId)
    {
        if (!_fileLoggingEnabled || IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        lock (_contextLock)
        {
            var context = GetOrCreateContext_NoLock(protocol, peer, connectionHash);
            if (context != null)
            {
                context.AccountId = NormalizeGuidish(accountId);
            }
        }
    }

    public void UpdateConnectionHostAccountId(string protocol, string peer, string connectionHash, Guid hostAccountId)
    {
        UpdateConnectionHostAccountId(protocol, peer, connectionHash, GuidToString(hostAccountId));
    }

    public void UpdateConnectionHostAccountId(string protocol, string peer, string connectionHash, string hostAccountId)
    {
        if (!_fileLoggingEnabled)
        {
            return;
        }

        lock (_contextLock)
        {
            var context = GetOrCreateContext_NoLock(protocol, peer, connectionHash);
            if (context != null)
            {
                context.HostAccountId = NormalizeGuidish(hostAccountId);
            }
        }
    }

    public void ClearConnectionHostAccountId(string protocol, string peer, string connectionHash)
    {
        if (!_fileLoggingEnabled)
        {
            return;
        }

        lock (_contextLock)
        {
            ConnectionContext context;
            if (TryResolveContext_NoLock(protocol, peer, connectionHash, out context) && context != null)
            {
                context.HostAccountId = null;
            }
        }
    }

    public void ClearConnectionContext(string protocol, string peer, string connectionHash)
    {
        if (!_fileLoggingEnabled)
        {
            return;
        }

        lock (_contextLock)
        {
            ConnectionContext context;
            if (!TryResolveContext_NoLock(protocol, peer, connectionHash, out context) || context == null)
            {
                return;
            }

            _contextByConnectionHash.Remove(context.ConnectionHash);

            var peerKey = BuildProtocolPeerKey(context.Protocol, context.Peer);
            string mappedHash;
            if (!IsNullOrWhiteSpace(peerKey)
                && _connectionHashByProtocolPeer.TryGetValue(peerKey, out mappedHash)
                && string.Equals(mappedHash, context.ConnectionHash, StringComparison.OrdinalIgnoreCase))
            {
                _connectionHashByProtocolPeer.Remove(peerKey);
            }
        }
    }

    private void WriteStructured(string streamName, string defaultLevel, object payload)
    {
        if (!_fileLoggingEnabled)
        {
            return;
        }

        var utcNow = DateTimeOffset.UtcNow;
        var record = NormalizePayload(streamName, defaultLevel, payload, utcNow);
        var json = Json.Serialize(record);

        lock (_writeLock)
        {
            CleanupExpiredFiles_NoLock(utcNow);
            var targetPath = GetCurrentBucketPath_NoLock(streamName, utcNow);
            File.AppendAllText(targetPath, json + Environment.NewLine, Encoding.UTF8);
        }
    }

    private Dictionary<string, object> NormalizePayload(string streamName, string defaultLevel, object payload, DateTimeOffset utcNow)
    {
        var raw = ToDictionary(payload);
        var timestamp = GetString(raw, "timestamp");
        if (IsNullOrWhiteSpace(timestamp))
        {
            timestamp = GetString(raw, "ts");
        }
        if (IsNullOrWhiteSpace(timestamp))
        {
            timestamp = utcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        var component = GetString(raw, "component");
        if (IsNullOrWhiteSpace(component))
        {
            component = GetString(raw, "type");
        }
        if (IsNullOrWhiteSpace(component))
        {
            component = streamName;
        }

        var protocol = InferProtocol(component, raw);

        var connectionHash = GetString(raw, "connectionHash");
        var peer = GetString(raw, "peer");
        ConnectionContext context = null;
        if (IsNullOrWhiteSpace(connectionHash) && !IsNullOrWhiteSpace(peer))
        {
            context = ResolveContext(protocol, peer, null);
            if (context != null && !IsNullOrWhiteSpace(context.ConnectionHash))
            {
                connectionHash = context.ConnectionHash;
            }
        }

        var accountId = GetString(raw, "accountId");
        if (IsNullOrWhiteSpace(accountId))
        {
            accountId = GetString(raw, "identityHash");
        }

        if (IsNullOrWhiteSpace(accountId))
        {
            if (context == null)
            {
                context = ResolveContext(protocol, peer, connectionHash);
            }
            if (context != null)
            {
                accountId = context.AccountId;
            }
        }

        var hostAccountId = GetString(raw, "hostAccountId");
        if (IsNullOrWhiteSpace(hostAccountId))
        {
            hostAccountId = GetString(raw, "hostIdentityHash");
        }

        if (IsNullOrWhiteSpace(hostAccountId))
        {
            if (context == null)
            {
                context = ResolveContext(protocol, peer, connectionHash);
            }
            if (context != null)
            {
                hostAccountId = context.HostAccountId;
            }
        }

        if (IsNullOrWhiteSpace(connectionHash))
        {
            if (!IsNullOrWhiteSpace(peer))
            {
                connectionHash = ComputeStableConnectionHash(peer);
            }
        }

        var message = GetString(raw, "message");
        if (IsNullOrWhiteSpace(message))
        {
            message = GetString(raw, "note");
        }
        if (IsNullOrWhiteSpace(message))
        {
            message = BuildFallbackMessage(component, raw);
        }

        var level = GetString(raw, "level");
        if (IsNullOrWhiteSpace(level))
        {
            level = InferLevel(defaultLevel, component, raw);
        }

        var normalized = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        normalized["timestamp"] = timestamp;
        normalized["accountId"] = NullIfWhiteSpace(accountId);
        normalized["hostAccountId"] = NullIfWhiteSpace(hostAccountId);
        normalized["connectionHash"] = NullIfWhiteSpace(connectionHash);
        normalized["component"] = component;
        normalized["message"] = message;
        normalized["level"] = level;
        normalized["stream"] = streamName;
        normalized["schemaVersion"] = SchemaVersion;

        AddIfPresent(normalized, "eventName", GetString(raw, "eventName"));
        AddIfPresent(normalized, "action", GetString(raw, "action"));
        AddIfPresent(normalized, "status", GetString(raw, "status"));
        AddIfPresent(normalized, "protocol", protocol);

        CopyAdditionalFields(raw, normalized);
        return normalized;
    }

    private void CopyAdditionalFields(Dictionary<string, object> raw, Dictionary<string, object> normalized)
    {
        foreach (var pair in raw)
        {
            if (pair.Key == null || ShouldSkipOriginalField(pair.Key))
            {
                continue;
            }

            if (!normalized.ContainsKey(pair.Key))
            {
                normalized[pair.Key] = pair.Value;
            }
        }
    }

    private string GetCurrentBucketPath_NoLock(string streamName, DateTimeOffset utcNow)
    {
        StreamState state;
        if (!_streamStateByName.TryGetValue(streamName, out state) || state == null)
        {
            state = new StreamState();
            _streamStateByName[streamName] = state;
        }

        var bucketStartUtc = AlignToBucketStart(utcNow);
        if (state.CurrentPath == null || state.BucketStartUtc != bucketStartUtc)
        {
            var prefix = GetStreamPrefix(streamName);
            state.BucketStartUtc = bucketStartUtc;
            state.CurrentPath = BuildBucketPath(prefix, bucketStartUtc);
        }

        return state.CurrentPath;
    }

    private void CleanupExpiredFiles(DateTimeOffset utcNow)
    {
        if (!_fileLoggingEnabled)
        {
            return;
        }

        lock (_writeLock)
        {
            CleanupExpiredFiles_NoLock(utcNow);
        }
    }

    private void CleanupExpiredFiles_NoLock(DateTimeOffset utcNow)
    {
        if (_retentionPeriod <= TimeSpan.Zero)
        {
            return;
        }

        if (_lastCleanupUtc != default(DateTimeOffset) && (utcNow - _lastCleanupUtc) < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastCleanupUtc = utcNow;
        CleanupStreamFiles(_eventsPrefix, utcNow);
        if (_diagnosticsPrefix != null)
        {
            CleanupStreamFiles(_diagnosticsPrefix, utcNow);
        }
    }

    private void CleanupStreamFiles(string prefix, DateTimeOffset utcNow)
    {
        var directory = Path.GetDirectoryName(prefix);
        var filePrefix = Path.GetFileName(prefix);
        if (IsNullOrWhiteSpace(directory) || IsNullOrWhiteSpace(filePrefix) || !Directory.Exists(directory))
        {
            return;
        }

        var cutoffUtc = utcNow - _retentionPeriod;
        var files = Directory.GetFiles(directory, filePrefix + "-*.jsonl");
        for (var i = 0; i < files.Length; i++)
        {
            var bucketStartUtc = TryParseBucketStartUtc(prefix, files[i]);
            if (!bucketStartUtc.HasValue || bucketStartUtc.Value >= cutoffUtc)
            {
                continue;
            }

            try
            {
                File.Delete(files[i]);
            }
            catch
            {
            }
        }
    }

    private DateTimeOffset? TryParseBucketStartUtc(string prefix, string path)
    {
        var filePrefix = Path.GetFileName(prefix);
        var name = Path.GetFileNameWithoutExtension(path);
        if (IsNullOrWhiteSpace(name) || !name.StartsWith(filePrefix + "-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var timestampText = name.Substring(filePrefix.Length + 1);
        DateTimeOffset parsed;
        if (!DateTimeOffset.TryParseExact(timestampText, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
        {
            return null;
        }

        return parsed.ToUniversalTime();
    }

    private string BuildBucketPath(string prefix, DateTimeOffset bucketStartUtc)
    {
        return prefix + "-" + bucketStartUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".jsonl";
    }

    private DateTimeOffset AlignToBucketStart(DateTimeOffset utcNow)
    {
        var normalized = utcNow.ToUniversalTime();
        var intervalMinutes = _rotationInterval.TotalMinutes >= 1 ? (int)_rotationInterval.TotalMinutes : 5;
        var bucketMinute = (normalized.Minute / intervalMinutes) * intervalMinutes;
        return new DateTimeOffset(normalized.Year, normalized.Month, normalized.Day, normalized.Hour, bucketMinute, 0, TimeSpan.Zero);
    }

    private string GetStreamPrefix(string streamName)
    {
        if (string.Equals(streamName, DiagnosticsStream, StringComparison.OrdinalIgnoreCase) && !IsNullOrWhiteSpace(_diagnosticsPrefix))
        {
            return _diagnosticsPrefix;
        }

        if (string.Equals(streamName, PlayerBugsStream, StringComparison.OrdinalIgnoreCase) && !IsNullOrWhiteSpace(_playerBugsPrefix))
        {
            return _playerBugsPrefix;
        }

        return _eventsPrefix;
    }

    private static string BuildFallbackMessage(string component, Dictionary<string, object> raw)
    {
        var action = GetString(raw, "action");
        var status = GetString(raw, "status");
        var error = GetString(raw, "error");

        if (!IsNullOrWhiteSpace(action) && !IsNullOrWhiteSpace(status))
        {
            return action + " " + status;
        }

        if (!IsNullOrWhiteSpace(action))
        {
            return action;
        }

        if (!IsNullOrWhiteSpace(status))
        {
            return status;
        }

        if (!IsNullOrWhiteSpace(error))
        {
            return error;
        }

        return component;
    }

    private static string InferLevel(string defaultLevel, string component, Dictionary<string, object> raw)
    {
        if (!IsNullOrWhiteSpace(GetString(raw, "error")) || ContainsWord(component, "error") || ContainsWord(component, "fatal"))
        {
            return "error";
        }

        if (ContainsWord(component, "warn"))
        {
            return "warn";
        }

        return defaultLevel;
    }

    private static string InferProtocol(string component, Dictionary<string, object> raw)
    {
        var protocol = GetString(raw, "protocol");
        if (!IsNullOrWhiteSpace(protocol))
        {
            return protocol;
        }

        if (ContainsWord(component, "aplay"))
        {
            return "aplay";
        }

        if (ContainsWord(component, "photon"))
        {
            return "photon";
        }

        if (ContainsWord(component, "http"))
        {
            return "http";
        }

        return null;
    }

    private static bool ContainsWord(string value, string needle)
    {
        if (IsNullOrWhiteSpace(value) || IsNullOrWhiteSpace(needle))
        {
            return false;
        }

        return value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Dictionary<string, object> ToDictionary(object payload)
    {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (payload == null)
        {
            return dict;
        }

        var genericDictionary = payload as IDictionary<string, object>;
        if (genericDictionary != null)
        {
            foreach (var pair in genericDictionary)
            {
                dict[pair.Key] = pair.Value;
            }
            return dict;
        }

        var dictionary = payload as IDictionary;
        if (dictionary != null)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key != null ? entry.Key.ToString() : null;
                if (!IsNullOrWhiteSpace(key))
                {
                    dict[key] = entry.Value;
                }
            }
            return dict;
        }

        var properties = payload.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            dict[property.Name] = property.GetValue(payload, null);
        }
        return dict;
    }

    private static string GetString(Dictionary<string, object> raw, string key)
    {
        object value;
        if (raw == null || !raw.TryGetValue(key, out value) || value == null)
        {
            return null;
        }

        var text = value as string;
        if (text != null)
        {
            return text;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static bool ShouldSkipOriginalField(string key)
    {
        return string.Equals(key, "ts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "timestamp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "type", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "peer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "note", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "identityHash", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "hostIdentityHash", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIfPresent(Dictionary<string, object> target, string key, string value)
    {
        if (!IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }

    private static string NullIfWhiteSpace(string value)
    {
        return IsNullOrWhiteSpace(value) ? null : value;
    }

    private ConnectionContext ResolveContext(string protocol, string peer, string connectionHash)
    {
        lock (_contextLock)
        {
            ConnectionContext context;
            return TryResolveContext_NoLock(protocol, peer, connectionHash, out context) ? context : null;
        }
    }

    private ConnectionContext GetOrCreateContext_NoLock(string protocol, string peer, string connectionHash)
    {
        ConnectionContext context;
        if (TryResolveContext_NoLock(protocol, peer, connectionHash, out context) && context != null)
        {
            if (!IsNullOrWhiteSpace(protocol))
            {
                context.Protocol = protocol;
            }
            if (!IsNullOrWhiteSpace(peer))
            {
                context.Peer = peer;
            }
            return context;
        }

        if (IsNullOrWhiteSpace(connectionHash))
        {
            connectionHash = !IsNullOrWhiteSpace(peer) ? ComputeStableConnectionHash(peer) : null;
            if (IsNullOrWhiteSpace(connectionHash))
            {
                return null;
            }
        }

        context = new ConnectionContext();
        context.ConnectionHash = connectionHash;
        context.Protocol = protocol;
        context.Peer = peer;
        _contextByConnectionHash[connectionHash] = context;

        var peerKey = BuildProtocolPeerKey(protocol, peer);
        if (!IsNullOrWhiteSpace(peerKey))
        {
            _connectionHashByProtocolPeer[peerKey] = connectionHash;
        }

        return context;
    }

    private bool TryResolveContext_NoLock(string protocol, string peer, string connectionHash, out ConnectionContext context)
    {
        context = null;
        if (!IsNullOrWhiteSpace(connectionHash) && _contextByConnectionHash.TryGetValue(connectionHash, out context) && context != null)
        {
            return true;
        }

        var peerKey = BuildProtocolPeerKey(protocol, peer);
        string mappedHash;
        if (IsNullOrWhiteSpace(peerKey)
            || !_connectionHashByProtocolPeer.TryGetValue(peerKey, out mappedHash)
            || IsNullOrWhiteSpace(mappedHash))
        {
            return false;
        }

        return _contextByConnectionHash.TryGetValue(mappedHash, out context) && context != null;
    }

    private static string BuildProtocolPeerKey(string protocol, string peer)
    {
        if (IsNullOrWhiteSpace(protocol) || IsNullOrWhiteSpace(peer))
        {
            return null;
        }

        return protocol.Trim().ToLowerInvariant() + "|" + peer.Trim().ToLowerInvariant();
    }

    private static string GuidToString(Guid value)
    {
        return value == Guid.Empty ? null : value.ToString("D");
    }

    private static string NormalizeGuidish(string value)
    {
        if (IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return new Guid(value.Trim()).ToString("D");
        }
        catch
        {
            return value.Trim();
        }
    }

    private static JavaScriptSerializer CreateSerializer()
    {
        var serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = int.MaxValue;
        serializer.RecursionLimit = 32;
        return serializer;
    }

    private static bool IsNullOrWhiteSpace(string value)
    {
        return value == null || value.Trim().Length == 0;
    }

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private sealed class StreamState
    {
        public DateTimeOffset BucketStartUtc;
        public string CurrentPath;
    }

    private sealed class ConnectionContext
    {
        public string Protocol;
        public string Peer;
        public string ConnectionHash;
        public string AccountId;
        public string HostAccountId;
    }
}

}
