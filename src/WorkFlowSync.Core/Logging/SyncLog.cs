using System.Globalization;
using System.Text;

namespace WorkFlowSync.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public interface ISyncLog
{
    void Write(LogLevel level, string message);
}

public static class SyncLogExtensions
{
    public static void Info(this ISyncLog log, string message) => log.Write(LogLevel.Info, message);
    public static void Warn(this ISyncLog log, string message) => log.Write(LogLevel.Warn, message);
    public static void Error(this ISyncLog log, string message) => log.Write(LogLevel.Error, message);
    public static void Debug(this ISyncLog log, string message) => log.Write(LogLevel.Debug, message);
}

/// <summary>Daily log file `wfs-YYYY-MM-DD.log` (docs/product/features/logging-and-diagnostics.md) plus an optional sink (console, GUI).</summary>
public sealed class FileSyncLog : ISyncLog, IDisposable
{
    private readonly string _dir;
    private readonly Action<LogLevel, string>? _sink;
    private readonly bool _verbose;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _writerDate;

    public FileSyncLog(string directory, Action<LogLevel, string>? sink = null, bool verbose = false)
    {
        _dir = directory;
        _sink = sink;
        _verbose = verbose;
    }

    public void Write(LogLevel level, string message)
    {
        if (level == LogLevel.Debug && !_verbose) return;
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss} {level.ToString().ToUpperInvariant(),-5} {message}";
        lock (_gate)
        {
            try
            {
                var date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (_writer is null || _writerDate != date)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_dir);
                    _writer = new StreamWriter(Path.Combine(_dir, $"wfs-{date}.log"), append: true, new UTF8Encoding(false)) { AutoFlush = true };
                    _writerDate = date;
                }
                _writer.WriteLine(line);
            }
            catch
            {
                // A broken log must never stop a pass.
            }
        }
        _sink?.Invoke(level, line);
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }
}

/// <summary>Collects lines in memory (tests, dry-run summaries).</summary>
public sealed class MemorySyncLog : ISyncLog
{
    public List<(LogLevel Level, string Message)> Lines { get; } = new();
    public void Write(LogLevel level, string message) { lock (Lines) Lines.Add((level, message)); }
}
