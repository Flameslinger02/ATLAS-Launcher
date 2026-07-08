using System.Diagnostics;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using Atlas.Core.Models;
using Atlas.Data;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IServerProcessService"/>
public sealed class ServerProcessService : IServerProcessService, IDisposable
{
    private readonly IConfigGeneratorService _config;
    private readonly IModDeploymentService _deploy;
    private readonly IModPresetService _presets;
    private readonly AtlasDatabase _db;

    private readonly BehaviorSubject<ServerState> _state = new(ServerState.Stopped);
    private readonly Subject<string> _log = new();

    /// <summary>Guards process/state field mutations (and serializes state transitions for correct ordering).</summary>
    private readonly object _gate = new();

    /// <summary>Single-flight gate: only one launch/stop/restart (incl. auto-restart) may run at a time.</summary>
    private readonly SemaphoreSlim _launchLock = new(1, 1);

    private Process? _process;
    private ServerProfile? _launchProfile;   // clone used for the current/last launch
    private DateTime _startedAtUtc;
    private volatile int _crashCount;
    private volatile bool _stopRequested;
    private CancellationTokenSource? _tailCts;

    /// <summary>The active launch's CTS (guarded by _gate). StopAsync cancels it so an in-progress
    /// launch (config write / mod deploy) aborts instead of making the stop queue behind it.</summary>
    private CancellationTokenSource? _launchCts;

    // CPU sampling state (delta-based; meaningful only when polled periodically).
    private TimeSpan _lastCpu;
    private DateTime _lastCpuSampleUtc;

    public ServerProcessService(
        IConfigGeneratorService config, IModDeploymentService deploy, IModPresetService presets, AtlasDatabase db)
    {
        _config = config;
        _deploy = deploy;
        _presets = presets;
        _db = db;
    }

    public ServerState CurrentState => _state.Value;
    public IObservable<ServerState> StateChanged => _state;
    public IObservable<string> LogOutput => _log;
    public Process? ServerProcess => _process;
    public int CrashCount => _crashCount;
    public string? ActiveProfileName => _launchProfile?.Name;

    public TimeSpan Uptime =>
        CurrentState == ServerState.Running && _startedAtUtc != default
            ? DateTime.UtcNow - _startedAtUtc
            : TimeSpan.Zero;

    // ----- Launch / stop / restart (each serialized through _launchLock) -----

    public async Task LaunchAsync(ServerProfile profile, CancellationToken ct = default)
    {
        var launchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _launchCts = launchCts;
        try
        {
            await _launchLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _crashCount = 0;             // a manual launch resets the crash budget
                await LaunchInternalAsync(profile, launchCts.Token).ConfigureAwait(false);
            }
            finally { _launchLock.Release(); }
        }
        finally { ClearLaunchCts(launchCts); }
    }

    public async Task StopAsync(bool force = false, CancellationToken ct = default)
    {
        // A stop must interrupt an in-progress launch rather than queue behind it — otherwise the Stop /
        // force-kill buttons appear dead for the whole startup window. _stopRequested is set first so a
        // process the launch already started resolves to Stopped (not Crashed → auto-restart) when killed.
        _stopRequested = true;
        lock (_gate) _launchCts?.Cancel();

        await _launchLock.WaitAsync(ct).ConfigureAwait(false);
        try { await StopInternalAsync(force, ct).ConfigureAwait(false); }
        finally { _launchLock.Release(); }
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        var launchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _launchCts = launchCts;
        try
        {
            await _launchLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var p = _launchProfile;
                await StopInternalAsync(force: false, ct).ConfigureAwait(false);
                if (p is not null)
                {
                    _crashCount = 0;
                    await LaunchInternalAsync(p, launchCts.Token).ConfigureAwait(false);
                }
            }
            finally { _launchLock.Release(); }
        }
        finally { ClearLaunchCts(launchCts); }
    }

    public async Task RestartAsync(ServerProfile profile, CancellationToken ct = default)
    {
        var launchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _launchCts = launchCts;
        try
        {
            await _launchLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await StopInternalAsync(force: false, ct).ConfigureAwait(false);
                _crashCount = 0;
                await LaunchInternalAsync(profile, launchCts.Token).ConfigureAwait(false);
            }
            finally { _launchLock.Release(); }
        }
        finally { ClearLaunchCts(launchCts); }
    }

    /// <summary>Unpublishes and disposes a launch CTS. Cancel() always runs under _gate, and the field is
    /// cleared under _gate before disposal, so a concurrent StopAsync can never cancel a disposed CTS.</summary>
    private void ClearLaunchCts(CancellationTokenSource launchCts)
    {
        lock (_gate) { if (ReferenceEquals(_launchCts, launchCts)) _launchCts = null; }
        launchCts.Dispose();
    }

    private async Task LaunchInternalAsync(ServerProfile profile, CancellationToken ct)
    {
        if (CurrentState is ServerState.Starting or ServerState.Running)
        {
            EmitLog("Launch ignored: the server is already starting or running.");
            return;
        }

        SetState(ServerState.Starting);
        try
        {
            ct.ThrowIfCancellationRequested();
            var p = Clone(profile);

            // Deferred preset propagation (Phase 5/7): re-resolve preset-linked mods into the launch copy.
            if (p.ActiveModPresetId is int presetId)
            {
                try
                {
                    await _presets.ApplyPresetToProfileAsync(presetId, p);
                    EmitLog($"Re-resolved mods from preset #{presetId}.");
                }
                catch (Exception ex)
                {
                    EmitLog($"Warning: preset re-resolution failed ({ex.Message}); using the profile's stored mods.");
                }
            }

            if (string.IsNullOrWhiteSpace(p.ServerDirectory) || !Directory.Exists(p.ServerDirectory))
                throw new InvalidOperationException(
                    "The profile's server directory is not set or does not exist. Set it on the Server Config / Performance tab.");

            var exe = ResolveExe(p);
            if (!File.Exists(exe))
                throw new InvalidOperationException($"Server executable not found: {exe}");

            await _config.WriteAllConfigFilesAsync(p, ct).ConfigureAwait(false);
            EmitLog("Wrote server.cfg, basic.cfg and beserver_x64.cfg.");

            var progress = new Progress<string>(EmitLog);
            await _deploy.DeployModsAsync(p, progress, ct).ConfigureAwait(false);

            // Last checkpoint before the process exists — a stop request past this point is handled by
            // StopInternalAsync killing the started process instead.
            ct.ThrowIfCancellationRequested();

            var profilesDir = Path.Combine(p.ServerDirectory, "profiles");
            Directory.CreateDirectory(profilesDir);

            var args = StripExecutable(_config.BuildLaunchArguments(p))
                       + $" -profiles={MaybeQuote(profilesDir)}";

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = p.ServerDirectory,   // relative -config/-cfg resolve against this
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (_, _) => OnProcessExited(proc);

            _stopRequested = false;
            EmitLog($"Launching: {Path.GetFileName(exe)} {args}");

            // Publish the process reference BEFORE Start so the Exited handler (which can only fire after
            // Start) always sees the correct process — important for a launch that exits almost instantly.
            lock (_gate)
            {
                _process = proc;
                _launchProfile = p;
                _startedAtUtc = DateTime.UtcNow;
                _lastCpuSampleUtc = default;
                _lastCpu = default;
            }

            try
            {
                if (!proc.Start())
                    throw new InvalidOperationException("The operating system did not start the server process.");
            }
            catch
            {
                lock (_gate) { if (ReferenceEquals(_process, proc)) _process = null; }
                proc.Dispose();
                throw;
            }

            EmitLog($"Server process started (PID {proc.Id}).");

            // Transition to Running only if the process is still alive and the Exited handler hasn't already
            // moved us to Crashed (instant exit). The lock serializes against OnProcessExited.
            bool running;
            lock (_gate)
            {
                running = ReferenceEquals(_process, proc) && !proc.HasExited && CurrentState == ServerState.Starting;
                if (running) SetState(ServerState.Running);
            }
            if (running) StartLogTail(profilesDir, _startedAtUtc);
        }
        catch (OperationCanceledException)
        {
            // A stop request (or caller cancellation) aborted the launch mid-flight. This is a user
            // action, not a failure — resolve to Stopped quietly and let the pending StopAsync finish.
            EmitLog("Launch aborted by stop request.");
            Process? proc;
            lock (_gate) proc = _process;
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
            SetState(ServerState.Stopped);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Server launch failed.");
            EmitLog($"Launch failed: {ex.Message}");
            SetState(ServerState.Stopped);
            throw;
        }
    }

    private async Task StopInternalAsync(bool force, CancellationToken ct)
    {
        Process? proc;
        lock (_gate) proc = _process;

        if (proc is null || proc.HasExited)
        {
            _stopRequested = false;
            SetState(ServerState.Stopped);
            return;
        }

        _stopRequested = true;
        SetState(ServerState.Stopping);
        EmitLog(force ? "Force-killing the server..." : "Stopping the server...");

        try
        {
            if (force)
            {
                proc.Kill(entireProcessTree: true);
            }
            else
            {
                // Clean stop: try an RCON "#shutdown" (short-lived dedicated client so it never disrupts the
                // Console page's shared RCON session) AND a window close, then fall back to Kill on timeout.
                // Both signals are sent because a server that connected to RCON may still ignore #shutdown.
                var rconSent = await TryRconShutdownAsync(ct).ConfigureAwait(false);
                var windowClosed = false;
                try { windowClosed = proc.CloseMainWindow(); } catch { /* console app may ignore */ }

                // If neither graceful signal landed (typical while the server is still booting — RCON is
                // down and a console process has no main window), there is nothing to wait for: use a short
                // grace instead of the full 30s so Stop stays responsive.
                var grace = TimeSpan.FromSeconds(rconSent || windowClosed ? 30 : 5);
                var exited = await WaitForExitAsync(proc, grace, ct).ConfigureAwait(false);
                if (!exited)
                {
                    EmitLog($"Graceful stop timed out after {grace.TotalSeconds:0}s; killing the process.");
                    proc.Kill(entireProcessTree: true);
                }
            }

            await WaitForExitAsync(proc, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while stopping the server.");
            EmitLog($"Error while stopping: {ex.Message}");
        }

        // OnProcessExited (fired by the kill) owns the canonical Stopped transition, the _process null+dispose
        // and the _stopRequested reset. This is a safety net if that handler ran stale or didn't fire.
        StopLogTail();
        SetState(ServerState.Stopped);
    }

    /// <summary>Attempts a clean RCON <c>#shutdown</c> via a short-lived client. Returns true if the
    /// command was issued (server may die before acking — that is fine; the kill fallback still runs).</summary>
    private async Task<bool> TryRconShutdownAsync(CancellationToken ct)
    {
        var p = _launchProfile;
        if (p is not { EnableBattlEye: true } || string.IsNullOrWhiteSpace(p.RconPassword)) return false;

        try
        {
            using var rcon = new BattlEyeRconClient();
            if (!await rcon.ConnectAsync("127.0.0.1", p.RconPort, p.RconPassword, ct).ConfigureAwait(false))
                return false;

            EmitLog("Sending RCON #shutdown...");
            try { await rcon.ShutdownServerAsync().ConfigureAwait(false); }
            catch { /* the server commonly dies before acking #shutdown */ }
            return true;
        }
        catch (Exception ex)
        {
            EmitLog($"RCON #shutdown unavailable ({ex.Message}); falling back to close/kill.");
            return false;
        }
    }

    // ----- Crash detection / auto-restart -----

    private void OnProcessExited(Process proc)
    {
        var exitCode = -1;
        try { exitCode = proc.ExitCode; } catch { /* may be unavailable */ }

        bool stopRequested;
        var crashes = 0;
        lock (_gate)
        {
            if (!ReferenceEquals(_process, proc)) return;   // stale handler from a prior launch
            stopRequested = _stopRequested;
            _process = null;                                 // clear the tracked handle on exit
            if (stopRequested)
            {
                _stopRequested = false;
                SetState(ServerState.Stopped);
            }
            else
            {
                _crashCount++;
                crashes = _crashCount;
                SetState(ServerState.Crashed);
            }
        }

        StopLogTail();
        try { proc.Dispose(); } catch { /* idempotent */ }

        if (stopRequested)
        {
            EmitLog($"Server stopped (exit code {exitCode}).");
            return;
        }

        EmitLog($"Server exited unexpectedly (exit code {exitCode}). Crash #{crashes}.");

        var p = _launchProfile;
        var willRestart = p is { AutoRestartOnCrash: true }
            && (p.MaxCrashesBeforeGiveUp == 0 || crashes < p.MaxCrashesBeforeGiveUp);

        _ = WriteCrashLogAsync(p, exitCode, willRestart);

        if (willRestart)
        {
            _ = AutoRestartAsync(p!);
        }
        else
        {
            EmitLog("Auto-restart is disabled or the crash limit was reached; leaving the server stopped.");
        }
    }

    private async Task WriteCrashLogAsync(ServerProfile? p, int exitCode, bool autoRestarted)
    {
        try
        {
            await _db.WriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var conn = _db.CreateOpenConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO CrashLog (ProfileId, CrashedAt, ExitCode, AutoRestarted, Notes) " +
                    "VALUES ($pid, $at, $code, $ar, $notes);";
                cmd.Parameters.AddWithValue("$pid", p is { Id: > 0 } ? p.Id : DBNull.Value);
                cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("$code", exitCode);
                cmd.Parameters.AddWithValue("$ar", autoRestarted ? 1 : 0);
                cmd.Parameters.AddWithValue("$notes", p?.Name ?? string.Empty);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                _db.WriteLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write CrashLog entry.");
        }
    }

    private async Task AutoRestartAsync(ServerProfile p)
    {
        var delay = Math.Max(0, p.AutoRestartDelaySeconds);
        EmitLog($"Auto-restarting in {delay}s...");
        try { await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false); }
        catch { /* ignore */ }

        if (_stopRequested) return;     // a manual stop arrived during the delay

        var launchCts = new CancellationTokenSource();
        lock (_gate) _launchCts = launchCts;
        try
        {
            await _launchLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_stopRequested) return; // re-check after acquiring the gate
                await LaunchInternalAsync(Clone(p), launchCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Auto-restart failed.");
                EmitLog($"Auto-restart failed: {ex.Message}");
                SetState(ServerState.Crashed);
            }
            finally { _launchLock.Release(); }
        }
        finally { ClearLaunchCts(launchCts); }
    }

    // ----- Resource sampling -----

    public (double CpuPercent, long MemoryBytes) GetResourceUsage()
    {
        Process? proc;
        lock (_gate) proc = _process;
        if (proc is null) return (0, 0);

        try
        {
            if (proc.HasExited) return (0, 0);
            proc.Refresh();

            var now = DateTime.UtcNow;
            var cpu = proc.TotalProcessorTime;
            double pct = 0;
            if (_lastCpuSampleUtc != default)
            {
                var wallMs = (now - _lastCpuSampleUtc).TotalMilliseconds;
                var cpuMs = (cpu - _lastCpu).TotalMilliseconds;
                if (wallMs > 0) pct = cpuMs / (wallMs * Environment.ProcessorCount) * 100.0;
            }
            _lastCpu = cpu;
            _lastCpuSampleUtc = now;
            return (Math.Clamp(pct, 0, 100), proc.WorkingSet64);
        }
        catch
        {
            return (0, 0);
        }
    }

    // ----- RPT log tail -----

    private void StartLogTail(string profilesDir, DateTime startUtc)
    {
        var cts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _tailCts, cts);
        try { old?.Cancel(); old?.Dispose(); } catch { /* ignore */ }
        _ = Task.Run(() => TailRptAsync(profilesDir, startUtc, cts.Token));
    }

    private void StopLogTail()
    {
        var old = Interlocked.Exchange(ref _tailCts, null);
        try { old?.Cancel(); old?.Dispose(); } catch { /* ignore */ }
    }

    private async Task TailRptAsync(string dir, DateTime startUtc, CancellationToken ct)
    {
        string? rpt = null;
        for (var i = 0; i < 60 && !ct.IsCancellationRequested && rpt is null; i++)
        {
            try
            {
                rpt = new DirectoryInfo(dir).GetFiles("*.rpt")
                    .Where(f => f.LastWriteTimeUtc >= startUtc.AddSeconds(-5))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.FullName;
            }
            catch { /* directory may be momentarily inaccessible */ }

            if (rpt is null)
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch { return; }
            }
        }

        if (rpt is null)
        {
            EmitLog("(No RPT log file appeared to tail.)");
            return;
        }

        EmitLog($"Tailing RPT log: {Path.GetFileName(rpt)}");
        try
        {
            await using var fs = new FileStream(rpt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            // Buffer any trailing partial line (no newline yet, because the server is mid-write) and only
            // emit complete, newline-terminated lines so a single log line is never split across entries.
            var pending = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                var chunk = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                if (chunk.Length > 0)
                {
                    pending.Append(chunk);
                    var text = pending.ToString();
                    var lastNl = text.LastIndexOf('\n');
                    if (lastNl >= 0)
                    {
                        foreach (var line in text[..lastNl].Split('\n'))
                            EmitLog(line.TrimEnd('\r'));
                        pending.Clear();
                        pending.Append(text[(lastNl + 1)..]);
                    }
                }

                try { await Task.Delay(750, ct).ConfigureAwait(false); }
                catch { break; }
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            EmitLog($"RPT tail error: {ex.Message}");
        }
    }

    // ----- Helpers -----

    // State transitions are emitted under _gate to keep them correctly ordered against OnProcessExited.
    // All current subscribers marshal onto the dispatcher (Application.Current.Dispatcher.InvokeAsync) and
    // never re-enter this service synchronously, so emitting under the lock cannot deadlock or re-enter.
    private void SetState(ServerState s) => _state.OnNext(s);

    private void EmitLog(string message) => _log.OnNext(message);

    private static string ResolveExe(ServerProfile p)
    {
        var name = !string.IsNullOrWhiteSpace(p.ServerExecutablePath)
            ? p.ServerExecutablePath
            : (p.UseProfilingBranch ? "arma3server_profiling_x64.exe" : "arma3server_x64.exe");
        return Path.IsPathRooted(name) ? name : Path.Combine(p.ServerDirectory, name);
    }

    /// <summary>Removes the leading quoted/bare executable token from a BuildLaunchArguments string.</summary>
    private static string StripExecutable(string full)
    {
        if (full.StartsWith('"'))
        {
            var close = full.IndexOf('"', 1);
            return close >= 0 ? full[(close + 1)..].TrimStart() : full;
        }
        var space = full.IndexOf(' ');
        return space >= 0 ? full[(space + 1)..] : string.Empty;
    }

    private static string MaybeQuote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    private static ServerProfile Clone(ServerProfile p) =>
        JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(p))!;

    private static async Task<bool> WaitForExitAsync(Process proc, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return proc.HasExited;
        }
    }

    public void Dispose()
    {
        StopLogTail();
        try { _state.OnCompleted(); } catch { /* ignore */ }
        try { _log.OnCompleted(); } catch { /* ignore */ }
        _state.Dispose();
        _log.Dispose();
        _launchLock.Dispose();
        // The server process is intentionally left running if ATLAS exits.
    }
}
