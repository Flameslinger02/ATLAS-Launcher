using System.Reactive.Subjects;
using Atlas.Core.Models;
using Serilog.Core;
using Serilog.Events;

namespace Atlas.Core.Services;

/// <summary>
/// A Serilog sink that captures ATLAS's own log events into a bounded ring buffer and republishes each one,
/// powering the in-app log viewer. Registered as a sink at startup AND as the <see cref="IAppLogService"/>
/// singleton (the same instance), so log config and DI share one buffer.
/// </summary>
public sealed class AppLogService : IAppLogService, ILogEventSink, IDisposable
{
    private const int MaxBuffer = 2000;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _buffer = new();
    private readonly Subject<LogEntry> _logged = new();

    public IObservable<LogEntry> Logged => _logged;

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _buffer.ToArray();
    }

    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage();
        if (logEvent.Exception is not null) message += " | " + logEvent.Exception.Message;

        var entry = new LogEntry
        {
            Timestamp = logEvent.Timestamp.LocalDateTime,
            Severity = (int)logEvent.Level,
            Level = Abbreviate(logEvent.Level),
            Message = message,
        };

        lock (_gate)
        {
            _buffer.Enqueue(entry);
            while (_buffer.Count > MaxBuffer) _buffer.Dequeue();
            // Publish under the lock: OnNext is serialized (Serilog may emit from many threads) and
            // subscribers only marshal to the dispatcher (non-blocking), so the lock is held briefly.
            try { _logged.OnNext(entry); } catch { /* subject may be disposed at shutdown */ }
        }
    }

    private static string Abbreviate(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "INF",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            try { _logged.OnCompleted(); } catch { /* ignore */ }
            _logged.Dispose();
        }
    }
}
