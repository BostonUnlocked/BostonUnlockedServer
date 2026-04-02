using System.Text.Json;

namespace Shadowrun.LocalService.Supervisor;

internal sealed class SupervisorAuditLogger
{
    private readonly object _writeLock = new();
    private readonly string _eventsPath;

    public SupervisorAuditLogger()
    {
        var baseDir = AppContext.BaseDirectory;
        var logsDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(logsDir);
        _eventsPath = Path.Combine(logsDir, "supervisor-events.jsonl");
    }

    public void Log(string eventName, object payload)
    {
        var envelope = new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            component = "supervisor",
            eventName,
            payload
        };

        var line = JsonSerializer.Serialize(envelope);
        lock (_writeLock)
        {
            File.AppendAllText(_eventsPath, line + Environment.NewLine);
        }
    }
}
