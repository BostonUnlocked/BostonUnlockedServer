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
    private readonly object _writeLock = new object();

    public RequestLogger(string path, string lowPath)
    {
        _path = path;
        _lowPath = IsNullOrWhiteSpace(lowPath) ? null : lowPath;
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
    }

    public void Reset()
    {
        lock (_writeLock)
        {
            File.WriteAllText(_path, string.Empty);
            if (_lowPath != null)
            {
                File.WriteAllText(_lowPath, string.Empty);
            }
        }
    }

    public void Log(object payload)
    {
        var json = Json.Serialize(payload);
        lock (_writeLock)
        {
            File.AppendAllText(_path, json + Environment.NewLine);
        }
    }

    public void LogLow(object payload)
    {
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
