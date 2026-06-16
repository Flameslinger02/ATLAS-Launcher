using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// In-memory view over ATLAS's own Serilog output for the in-app log viewer. Backed by a custom Serilog
/// sink (registered at startup) that keeps a bounded ring buffer and publishes each entry as it is logged.
/// </summary>
public interface IAppLogService
{
    /// <summary>The most recent buffered entries (oldest first), for seeding a freshly-opened viewer.</summary>
    IReadOnlyList<LogEntry> Snapshot();

    /// <summary>Fires for every log entry as it is written (on the logging thread; subscribers must marshal).</summary>
    IObservable<LogEntry> Logged { get; }
}
