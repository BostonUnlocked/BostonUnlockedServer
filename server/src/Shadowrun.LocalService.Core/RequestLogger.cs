using System;
using System.IO;
using System.Web.Script.Serialization;

namespace Shadowrun.LocalService.Core
{
public sealed class RequestLogger
{
    private static readonly JavaScriptSerializer Json = CreateSerializer();

    private readonly string _path;
    private readonly string _lowPath;
    private readonly string _aiPath;
    private readonly string _adminPath;
    private readonly bool _fileLoggingEnabled;
    private readonly object _writeLock = new object();

    public RequestLogger(string path, string lowPath)
        : this(path, lowPath, null)
    {
    }

    public RequestLogger(string path, string lowPath, string aiPath)
        : this(path, lowPath, aiPath, null)
    {
    }

    public RequestLogger(string path, string lowPath, string aiPath, string adminPath)
    {
        if (IsNullOrWhiteSpace(path))
        {
            // Disabled: do not write logs to disk.
            _path = null;
            _lowPath = null;
            _aiPath = null;
            _adminPath = null;
            _fileLoggingEnabled = false;
            return;
        }

        _path = path;
        _lowPath = IsNullOrWhiteSpace(lowPath) ? null : lowPath;
        _aiPath = IsNullOrWhiteSpace(aiPath) ? null : aiPath;
        _adminPath = IsNullOrWhiteSpace(adminPath) ? null : adminPath;
        _fileLoggingEnabled = true;

        var parent = Path.GetDirectoryName(_path);
        if (!IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (_lowPath != null)
        {
            var lowParent = Path.GetDirectoryName(_lowPath);
            if (!IsNullOrWhiteSpace(lowParent))
            {
                Directory.CreateDirectory(lowParent);
            }
        }

        if (_aiPath != null)
        {
            var aiParent = Path.GetDirectoryName(_aiPath);
            if (!IsNullOrWhiteSpace(aiParent))
            {
                Directory.CreateDirectory(aiParent);
            }
        }

        if (_adminPath != null)
        {
            var adminParent = Path.GetDirectoryName(_adminPath);
            if (!IsNullOrWhiteSpace(adminParent))
            {
                Directory.CreateDirectory(adminParent);
            }
        }
    }

    public void Reset()
    {
        if (!_fileLoggingEnabled)
        {
            return;
        }

        lock (_writeLock)
        {
            File.WriteAllText(_path, string.Empty);
            if (_lowPath != null)
            {
                File.WriteAllText(_lowPath, string.Empty);
            }

            if (_aiPath != null)
            {
                File.WriteAllText(_aiPath, string.Empty);
            }

            if (_adminPath != null)
            {
                File.WriteAllText(_adminPath, string.Empty);
            }
        }
    }

    public void Log(object payload)
    {
        if (!_fileLoggingEnabled)
        {
            return;
        }

        var json = Json.Serialize(payload);
        lock (_writeLock)
        {
            File.AppendAllText(_path, json + Environment.NewLine);
        }
    }

    public void LogLow(object payload)
    {
        if (!_fileLoggingEnabled)
        {
            return;
        }

        if (_lowPath == null)
        {
            Log(payload);
            return;
        }

        var json = Json.Serialize(payload);
        lock (_writeLock)
        {
            File.AppendAllText(_lowPath, json + Environment.NewLine);
        }
    }

    public void LogAi(object payload)
    {
        if (!_fileLoggingEnabled)
        {
            return;
        }

        if (_aiPath == null)
        {
            Log(payload);
            return;
        }

        var json = Json.Serialize(payload);
        lock (_writeLock)
        {
            File.AppendAllText(_aiPath, json + Environment.NewLine);
        }
    }

    public void LogAdmin(object payload)
    {
        if (!_fileLoggingEnabled)
        {
            return;
        }

        if (_adminPath == null)
        {
            Log(payload);
            return;
        }

        var json = Json.Serialize(payload);
        lock (_writeLock)
        {
            File.AppendAllText(_adminPath, json + Environment.NewLine);
        }
    }

    public static string UtcNowIso()
    {
        return DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz");
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
}

}
