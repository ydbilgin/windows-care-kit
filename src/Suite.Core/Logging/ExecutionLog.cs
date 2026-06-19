using System.Text.Json;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Logging;

/// <summary>
/// Append-only JSONL audit trail. Every message and data value is run through an
/// <see cref="ILogRedactor"/> before being written (spec §3). Each call appends exactly one JSON
/// object on its own line.
/// </summary>
public sealed class ExecutionLog
{
    private readonly string _path;
    private readonly ILogRedactor _redactor;
    private readonly object _lock = new();

    public ExecutionLog(string path, ILogRedactor redactor)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));

        // Ensure the log directory exists so the first Append on a fresh install does not throw.
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);
        }
        catch (Exception)
        {
            // logging is best-effort — a directory that cannot be created must not break the app
        }
    }

    public void Append(string eventType, string message, IReadOnlyDictionary<string, string?>? data = null)
    {
        string line = FormatEntry(DateTime.UtcNow, eventType, message, _redactor, data);
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A logging failure must NEVER abort or change the status of a destructive action (spec §9, L11).
        }
    }

    public void LogVerdict(PlannedAction action, SafetyVerdict verdict)
        => Append(
            verdict.Allowed ? "action.allowed" : "action.blocked",
            $"{action.Kind}: {action.Description}",
            new Dictionary<string, string?>
            {
                ["target"] = action.TargetSignature(),
                ["risk"] = action.Risk.ToString(),
                ["undo"] = action.Undo.ToString(),
                ["reason"] = verdict.Reason,
            });

    /// <summary>Pure formatter (no IO): builds one redacted JSONL line. Exposed for unit testing.</summary>
    public static string FormatEntry(
        DateTime utc,
        string eventType,
        string message,
        ILogRedactor redactor,
        IReadOnlyDictionary<string, string?>? data)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", utc.ToString("o"));
            writer.WriteString("evt", redactor.Redact(eventType));
            writer.WriteString("msg", redactor.Redact(message));
            if (data is { Count: > 0 })
            {
                writer.WriteStartObject("data");
                foreach (var kv in data)
                    writer.WriteString(redactor.Redact(kv.Key), redactor.Redact(kv.Value));
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
