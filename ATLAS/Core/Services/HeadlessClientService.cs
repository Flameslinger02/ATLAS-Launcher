using System.Diagnostics;
using System.Reactive.Subjects;
using System.Text;
using Atlas.Core.Models;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IHeadlessClientService"/>
public sealed class HeadlessClientService : IHeadlessClientService, IDisposable
{
    private readonly List<HeadlessClientInstance> _instances = new();
    private readonly Dictionary<int, bool> _stopRequested = new();
    private readonly Subject<HeadlessClientInstance> _changed = new();

    /// <summary>Guards all mutable instance state (_instances, inst.Process/State/CrashCount, _stopRequested, _lastProfile).</summary>
    private readonly object _gate = new();

    private ServerProfile? _lastProfile;

    public IReadOnlyList<HeadlessClientInstance> Instances
    {
        get { lock (_gate) return _instances.OrderBy(i => i.Index).ToList(); }
    }

    public IObservable<HeadlessClientInstance> InstanceChanged => _changed;

    public void ConfigureInstances(int count)
    {
        count = Math.Max(0, count);
        var added = new List<HeadlessClientInstance>();
        lock (_gate)
        {
            for (var i = 0; i < count; i++)
            {
                if (_instances.All(x => x.Index != i))
                {
                    var inst = new HeadlessClientInstance { Index = i, Name = $"HC{i}" };
                    _instances.Add(inst);
                    added.Add(inst);
                }
            }
        }
        foreach (var inst in added) Notify(inst);   // notify outside the lock
    }

    public async Task LaunchAllAsync(ServerProfile profile, CancellationToken ct = default)
    {
        var count = Math.Max(0, profile.HeadlessClientCount);
        ConfigureInstances(count);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await LaunchSingleAsync(profile, i, ct).ConfigureAwait(false);
        }
    }

    public Task LaunchSingleAsync(ServerProfile profile, int index, CancellationToken ct = default)
        => LaunchCoreAsync(profile, index, resetCrashes: true);

    private Task LaunchCoreAsync(ServerProfile profile, int index, bool resetCrashes)
    {
        ConfigureInstances(index + 1);
        var inst = Get(index)!;

        lock (_gate)
        {
            _lastProfile = profile;
            if (inst.State is ServerState.Starting or ServerState.Running) return Task.CompletedTask;
            if (resetCrashes) inst.CrashCount = 0;
            inst.State = ServerState.Starting;
        }
        Notify(inst);

        try
        {
            var exe = ResolveHcExe(profile);
            if (!File.Exists(exe))
                throw new InvalidOperationException($"Headless-client executable not found: {exe}");

            Directory.CreateDirectory(InstanceProfileDir(index));   // per-index so each HC's .rpt is isolated

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = BuildHcArguments(profile, index),
                WorkingDirectory = profile.ServerDirectory,   // so -mod=@X resolves against deployed mods
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (_, _) => OnHcExited(index, proc);

            // Publish the process reference under the lock BEFORE Start so OnHcExited (which can only fire
            // after Start, possibly on an instant exit) always observes the correct, fully-published value.
            lock (_gate)
            {
                _stopRequested[index] = false;
                inst.Process = proc;
                inst.StartedAt = DateTime.UtcNow;
            }

            try
            {
                if (!proc.Start())
                    throw new InvalidOperationException("The operating system did not start the headless-client process.");
            }
            catch
            {
                lock (_gate) { if (ReferenceEquals(inst.Process, proc)) inst.Process = null; }
                throw;
            }

            // Claim Running only if still alive and OnHcExited hasn't already moved us to Crashed — under
            // the same lock so the Running/Crashed decision is serialized against the exit handler.
            bool running;
            lock (_gate)
            {
                running = ReferenceEquals(inst.Process, proc) && !proc.HasExited && inst.State == ServerState.Starting;
                if (running) inst.State = ServerState.Running;
            }
            Notify(inst);
            Log.Information("Headless client {Name} started (PID {Pid}).", inst.Name, proc.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch headless client {Index}.", index);
            lock (_gate) inst.State = ServerState.Crashed;
            Notify(inst);
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAllAsync(CancellationToken ct = default)
    {
        var indices = Instances.Select(i => i.Index).ToList();
        foreach (var i in indices)
            await StopSingleAsync(i, ct).ConfigureAwait(false);
    }

    public async Task StopSingleAsync(int index, CancellationToken ct = default)
    {
        HeadlessClientInstance? inst;
        Process? proc;
        lock (_gate)
        {
            inst = _instances.FirstOrDefault(i => i.Index == index);
            proc = inst?.Process;
            if (inst is not null) _stopRequested[index] = true;
        }
        if (inst is null) return;

        if (proc is null || proc.HasExited)
        {
            lock (_gate) { inst.Process = null; inst.State = ServerState.Stopped; }
            Notify(inst);
            return;
        }

        lock (_gate) inst.State = ServerState.Stopping;
        Notify(inst);

        try
        {
            proc.Kill(entireProcessTree: true);    // headless clients have no RCON; terminate directly
            await WaitForExitAsync(proc, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping headless client {Index}.", index);
        }

        lock (_gate) { if (ReferenceEquals(inst.Process, proc)) inst.Process = null; inst.State = ServerState.Stopped; }
        try { proc.Dispose(); } catch { /* idempotent */ }
        Notify(inst);
    }

    public async Task RestartSingleAsync(ServerProfile profile, int index, CancellationToken ct = default)
    {
        await StopSingleAsync(index, ct).ConfigureAwait(false);
        await LaunchSingleAsync(profile, index, ct).ConfigureAwait(false);
    }

    /// <summary>The per-instance <c>-profiles</c> directory where that HC writes its <c>.rpt</c> log.</summary>
    public string GetInstanceLogDirectory(int index) => InstanceProfileDir(index);

    private static string InstanceProfileDir(int index) =>
        Path.Combine(AppConstants.HeadlessClientProfilesDirectory, $"HC{index}");

    // ----- Crash handling -----

    private void OnHcExited(int index, Process proc)
    {
        var inst = Get(index);
        if (inst is null) return;

        bool stopRequested;
        int crashes;
        ServerProfile? p;
        lock (_gate)
        {
            if (!ReferenceEquals(inst.Process, proc)) return;   // stale handler from a prior launch
            stopRequested = _stopRequested.GetValueOrDefault(index);
            inst.Process = null;
            if (stopRequested)
            {
                inst.State = ServerState.Stopped;
            }
            else
            {
                inst.CrashCount++;
                inst.State = ServerState.Crashed;
            }
            crashes = inst.CrashCount;
            p = _lastProfile;
        }

        try { proc.Dispose(); } catch { /* idempotent */ }
        Notify(inst);

        if (stopRequested) return;

        Log.Warning("Headless client {Name} exited unexpectedly (crash #{Count}).", inst.Name, crashes);

        var cap = p?.MaxCrashesBeforeGiveUp ?? 0;     // 0 = unlimited; mirrors the server crash budget
        if (p is { HeadlessAutoRestart: true } && (cap == 0 || crashes < cap))
        {
            _ = AutoRestartAsync(p, index);
        }
        else
        {
            Log.Information("Headless client {Name} not auto-restarted (disabled or crash limit reached).", inst.Name);
        }
    }

    private async Task AutoRestartAsync(ServerProfile p, int index)
    {
        var delay = Math.Max(0, p.AutoRestartDelaySeconds);
        try { await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false); }
        catch { /* ignore */ }

        bool stopRequested;
        lock (_gate) stopRequested = _stopRequested.GetValueOrDefault(index);
        if (stopRequested) return;

        try { await LaunchCoreAsync(p, index, resetCrashes: false).ConfigureAwait(false); }
        catch (Exception ex) { Log.Error(ex, "Headless client {Index} auto-restart failed.", index); }
    }

    // ----- Helpers -----

    private HeadlessClientInstance? Get(int index)
    {
        lock (_gate) return _instances.FirstOrDefault(i => i.Index == index);
    }

    private void Notify(HeadlessClientInstance inst) => _changed.OnNext(inst);

    private static string ResolveHcExe(ServerProfile p)
    {
        var name = !string.IsNullOrWhiteSpace(p.HeadlessClientExecutablePath)
            ? p.HeadlessClientExecutablePath
            : (!string.IsNullOrWhiteSpace(p.ServerExecutablePath)
                ? p.ServerExecutablePath
                : (p.UseProfilingBranch ? "arma3server_profiling_x64.exe" : "arma3server_x64.exe"));
        return Path.IsPathRooted(name) ? name : Path.Combine(p.ServerDirectory, name);
    }

    private static string BuildHcArguments(ServerProfile p, int index)
    {
        var sb = new StringBuilder();
        void Flag(string f) => sb.Append(sb.Length == 0 ? f : " " + f);

        Flag("-client");
        Flag("-connect=127.0.0.1");
        Flag($"-port={p.Port}");
        if (!string.IsNullOrEmpty(p.ServerPassword)) Flag($"-password={p.ServerPassword}");
        Flag($"-profiles={MaybeQuote(InstanceProfileDir(index))}");
        Flag($"-name=HC{index}");
        Flag("-nosound");
        Flag("-nosplash");
        Flag("-skipIntro");
        Flag("-world=empty");

        // Client/headless mods (server-only mods never go on a client). Enabled creator/platform DLC folders
        // lead the list, matching the server's -mod= line so DLC-dependent missions load on the HC too.
        var mods = string.Join(";", ConfigGeneratorService.EnabledDlcFolders(p)
            .Concat(p.Mods
                .OrderBy(m => m.LoadOrder)
                .Where(m => m.EnabledForHeadless && !m.IsServerOnly)
                .Select(ModFolder))
            .Where(s => s.Length > 0));
        if (mods.Length > 0) Flag($"-mod={MaybeQuote(mods)}");

        return sb.ToString();
    }

    private static string ModFolder(ArmaModEntry m)
    {
        if (!string.IsNullOrWhiteSpace(m.FolderName)) return m.FolderName;
        if (!string.IsNullOrWhiteSpace(m.Name)) return "@" + m.Name;
        return string.Empty;
    }

    private static string MaybeQuote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

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
        try { _changed.OnCompleted(); } catch { /* ignore */ }
        _changed.Dispose();
        // Headless-client processes are intentionally left running if ATLAS exits.
    }
}
