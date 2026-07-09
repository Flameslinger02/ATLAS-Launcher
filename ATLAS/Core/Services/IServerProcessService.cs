using System.Diagnostics;
using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Owns the lifecycle of the single managed Arma 3 dedicated-server process: launch (write configs,
/// deploy mods, start the process with the canonical arguments), graceful/forced stop, crash detection
/// with optional auto-restart, RPT log streaming and live resource sampling.
/// State and log output are exposed as observables so view models can subscribe and marshal to the UI.
/// </summary>
public interface IServerProcessService
{
    /// <summary>Current lifecycle state (mirrors the latest value pushed to <see cref="StateChanged"/>).</summary>
    ServerState CurrentState { get; }

    /// <summary>Pushes the current state on subscribe (BehaviorSubject) and every transition thereafter.</summary>
    IObservable<ServerState> StateChanged { get; }

    /// <summary>Streams server progress messages and tailed RPT log lines (hot; no replay).</summary>
    IObservable<string> LogOutput { get; }

    /// <summary>The live server process, or null when not running.</summary>
    Process? ServerProcess { get; }

    /// <summary>Wall-clock time since the running process started; <see cref="TimeSpan.Zero"/> if not running.</summary>
    TimeSpan Uptime { get; }

    /// <summary>Number of unexpected exits since the last manual launch.</summary>
    int CrashCount { get; }

    /// <summary>Name of the profile used for the current/last launch.</summary>
    string? ActiveProfileName { get; }

    /// <summary>
    /// Launches the server for <paramref name="profile"/>: re-resolves preset-linked mods, writes the
    /// config files, deploys mods, then starts the process with <c>WorkingDirectory = ServerDirectory</c>.
    /// Resets the crash counter. Throws on validation/start failure.
    /// </summary>
    Task LaunchAsync(ServerProfile profile, CancellationToken ct = default);

    /// <summary>Stops the running server. Graceful (close + 30s grace) unless <paramref name="force"/>.</summary>
    Task StopAsync(bool force = false, CancellationToken ct = default);

    /// <summary>Stops then relaunches the current/last profile.</summary>
    Task RestartAsync(CancellationToken ct = default);

    /// <summary>Stops then relaunches the given <paramref name="profile"/> (used by scheduled restarts so
    /// the relaunch targets the task's profile, not whatever was last launched).</summary>
    Task RestartAsync(ServerProfile profile, CancellationToken ct = default);

    /// <summary>Samples CPU usage (% across all cores, since the previous call) and working-set bytes.</summary>
    (double CpuPercent, long MemoryBytes) GetResourceUsage();

    /// <summary>
    /// If ATLAS was closed while a server was still running, re-attaches to that live process on the next
    /// launch: finds a running <c>arma3server</c> whose executable belongs to <paramref name="profile"/>'s
    /// server directory and adopts it as the managed process — restoring Stop/Force-kill, uptime (from the
    /// process start time), the RPT tail and crash detection. Performance history starts fresh. No-ops and
    /// returns false if a process is already tracked or no matching server is found.
    /// </summary>
    bool TryAdoptRunningServer(ServerProfile profile);
}
