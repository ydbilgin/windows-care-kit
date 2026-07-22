using System.Text.Json;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Logging;

/// <summary>Whether the append-only audit sink is writable. Latches to <see cref="Unavailable"/> on first failure.</summary>
public enum LogHealth
{
    /// <summary>Every log write so far has succeeded (or none has been attempted).</summary>
    Healthy,

    /// <summary>At least one directory-create/append write failed; the audit trail may be incomplete.</summary>
    Unavailable,
}

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

    private LogHealth _health = LogHealth.Healthy;
    private string? _healthCategory;
    private int _failureCount;

    /// <summary>The audit sink's latched health. Reads are synchronized and side-effect-free.</summary>
    public LogHealth Health { get { lock (_lock) { return _health; } } }

    /// <summary>A SAFE first-failure category (the exception type name only — no path, message, or payload). Null while Healthy.</summary>
    public string? HealthCategory { get { lock (_lock) { return _healthCategory; } } }

    /// <summary>How many log writes have failed (directory-create + append). Zero while Healthy.</summary>
    public int FailureCount { get { lock (_lock) { return _failureCount; } } }

    /// <summary>
    /// Latch the sink to <see cref="LogHealth.Unavailable"/> and record a SAFE category (exception type name
    /// only). The FIRST failure's category is retained; the counter increments on every failure. A logging
    /// failure must never change an action outcome, so this only records — it never throws or rethrows.
    /// </summary>
    private void MarkUnavailable(string safeCategory)
    {
        lock (_lock)
        {
            _failureCount++;
            if (_health == LogHealth.Unavailable)
                return;
            _health = LogHealth.Unavailable;
            _healthCategory = safeCategory;
        }
    }

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
        catch (Exception ex)
        {
            // logging is best-effort — a directory that cannot be created must not break the app, but the
            // degraded audit channel is now observable (NEW-08). Category is the type name only (safe).
            MarkUnavailable(ex.GetType().Name);
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
            // The lost/degraded audit channel is now latched + observable (NEW-08); no path/message is recorded.
            MarkUnavailable(ex.GetType().Name);
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
