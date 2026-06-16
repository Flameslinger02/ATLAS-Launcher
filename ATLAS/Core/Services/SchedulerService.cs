using System.Text.Json;
using Atlas.Core.Models;
using Atlas.Data;
using Cronos;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="ISchedulerService"/>
/// <remarks>
/// Runs as an <see cref="IHostedService"/>: a 20s polling loop fires due, enabled tasks for the ACTIVE
/// profile only (single managed server). Cron is interpreted in local time; stored times are UTC. On
/// startup every enabled task's NextRunAt is recomputed to the next FUTURE occurrence so missed runs
/// (app was closed) do not all fire at once. Each task is single-flighted, so a long restart countdown
/// can't overlap itself.
/// </remarks>
public sealed class SchedulerService : ISchedulerService, IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly AtlasDatabase _db;
    private readonly IProfileService _profiles;
    private readonly IServerProcessService _server;
    private readonly ISteamCmdService _steamCmd;
    private readonly IModDeploymentService _deploy;
    private readonly ISettingsService _settings;

    private readonly object _runLock = new();
    private readonly HashSet<int> _running = new();   // single-flight by task id
    private readonly List<Task> _inflight = new();     // executions awaited on shutdown
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event EventHandler? TasksChanged;

    public SchedulerService(
        AtlasDatabase db, IProfileService profiles, IServerProcessService server,
        ISteamCmdService steamCmd, IModDeploymentService deploy, ISettingsService settings)
    {
        _db = db;
        _profiles = profiles;
        _server = server;
        _steamCmd = steamCmd;
        _deploy = deploy;
        _settings = settings;
    }

    // ----- Hosted lifecycle -----

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loop is not null) return;   // idempotent: never start a second poll loop / orphan a _cts

        // Advance the active profile's enabled tasks to their next FUTURE run so closing the app doesn't
        // queue a backlog. Scoped to the active profile to match what the poll loop actually executes.
        try
        {
            var p = _profiles.ActiveProfile;
            if (p is not null)
            {
                var tasks = await GetTasksForProfileAsync(p.Id).ConfigureAwait(false);
                foreach (var t in tasks.Where(t => t.IsEnabled))
                    await PersistNextRunAsync(t.Id, GetNextRunTime(t.CronExpression)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Log.Error(ex, "Scheduler startup recompute failed."); }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
        Log.Information("Scheduler started.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch { /* best-effort drain */ }
        }

        // Await in-flight executions so a restart/update doesn't issue a server action after teardown begins.
        // _cts.Cancel above already unblocks any countdown delay, so cancelled runs unwind quickly.
        Task[] pending;
        lock (_runLock) pending = _inflight.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch { /* timed out or cancelled mid-run — best effort */ }
        }
        Log.Information("Scheduler stopped.");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try
            {
                var profile = _profiles.ActiveProfile;
                if (profile is null) continue;

                var now = DateTime.UtcNow;
                var tasks = await GetTasksForProfileAsync(profile.Id).ConfigureAwait(false);
                foreach (var task in tasks)
                {
                    if (!task.IsEnabled || task.NextRunAt is not { } next || next > now) continue;
                    if (!TryClaim(task.Id)) continue;   // already running

                    // Advance the schedule up front so the UI shows the next slot, not now+countdown.
                    await PersistNextRunAsync(task.Id, GetNextRunTime(task.CronExpression)).ConfigureAwait(false);
                    StartTracked(task, profile, ct);
                }
            }
            catch (Exception ex) { Log.Error(ex, "Scheduler poll iteration failed."); }
        }
    }

    // ----- Execution -----

    public async Task TriggerNowAsync(int id)
    {
        var task = await GetByIdAsync(id).ConfigureAwait(false);
        if (task is null) return;
        var profile = _profiles.ActiveProfile?.Id == task.ProfileId
            ? _profiles.ActiveProfile
            : await _profiles.GetProfileByIdAsync(task.ProfileId).ConfigureAwait(false);
        if (profile is null) return;
        if (!TryClaim(task.Id)) return;

        // Advance the schedule for enabled tasks so the poll loop doesn't re-fire a still-past NextRunAt
        // right after this manual run (a disabled task is left un-armed).
        if (task.IsEnabled)
            await PersistNextRunAsync(task.Id, GetNextRunTime(task.CronExpression)).ConfigureAwait(false);

        await RunTaskAsync(task, profile, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
    }

    public async Task SkipNextAsync(int id)
    {
        var task = await GetByIdAsync(id).ConfigureAwait(false);
        if (task is null || !task.IsEnabled) return;
        // GetNextOccurrence is exclusive of 'from', so passing the current NextRunAt yields the FOLLOWING one.
        var from = task.NextRunAt ?? DateTime.UtcNow;
        var next = TryParse(task.CronExpression)?.GetNextOccurrence(from, TimeZoneInfo.Local);
        await PersistNextRunAsync(id, next).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>Launches a fire-and-forget execution and records it in <see cref="_inflight"/> so StopAsync can await it.</summary>
    private void StartTracked(ScheduledTask task, ServerProfile profile, CancellationToken ct)
    {
        var run = RunTaskAsync(task, profile, ct);
        if (run.IsCompleted) return;   // ran synchronously to completion — nothing to track
        lock (_runLock) _inflight.Add(run);
        _ = run.ContinueWith(_ => { lock (_runLock) _inflight.Remove(run); }, TaskScheduler.Default);
    }

    private async Task RunTaskAsync(ScheduledTask task, ServerProfile profile, CancellationToken ct)
    {
        try
        {
            var result = await ExecuteAsync(task, profile, ct).ConfigureAwait(false);
            await RecordRunAsync(task.Id, result).ConfigureAwait(false);
            Log.Information("Scheduled task '{Name}' ran: {Result}", task.Name, result);
        }
        catch (OperationCanceledException)
        {
            // App shutting down mid-task — leave LastRun untouched.
        }
        catch (Exception ex)
        {
            await RecordRunAsync(task.Id, $"Error: {ex.Message}").ConfigureAwait(false);
            Log.Error(ex, "Scheduled task '{Name}' failed.", task.Name);
        }
        finally
        {
            Release(task.Id);
            RaiseChanged();
        }
    }

    private async Task<string> ExecuteAsync(ScheduledTask task, ServerProfile profile, CancellationToken ct)
    {
        var progress = new Progress<string>(m => Log.Debug("[sched:{Name}] {Msg}", task.Name, m));
        switch (task.TaskType)
        {
            case ScheduledTaskType.Restart:
            {
                var p = Payload<RestartPayload>(task) ?? new RestartPayload();
                if (_server.CurrentState != ServerState.Running)
                {
                    await _server.LaunchAsync(profile, ct).ConfigureAwait(false);
                    return "Server was stopped; launched.";
                }
                if (p.CountdownEnabled && RconConfigured(profile) && p.MinutesWarnings.Any(m => m > 0))
                    return await RestartWithCountdownAsync(profile, p, ct).ConfigureAwait(false);
                await _server.RestartAsync(profile, ct).ConfigureAwait(false);
                return "Server restarted.";
            }
            case ScheduledTaskType.Message:
            {
                var p = Payload<MessagePayload>(task) ?? new MessagePayload();
                if (string.IsNullOrWhiteSpace(p.Message)) return "No message configured; skipped.";
                if (!RconConfigured(profile)) return "RCON not configured; message skipped.";
                await BroadcastAsync(profile, new[] { p.Message }, ct).ConfigureAwait(false);
                return "Broadcast sent.";
            }
            case ScheduledTaskType.RconCommand:
            {
                var p = Payload<RconCommandPayload>(task) ?? new RconCommandPayload();
                if (string.IsNullOrWhiteSpace(p.Command)) return "No command configured; skipped.";
                if (!RconConfigured(profile)) return "RCON not configured; command skipped.";
                var response = await SendCommandAsync(profile, p.Command, ct).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(response) ? "Command sent." : $"Command sent: {Trim(response, 120)}";
            }
            case ScheduledTaskType.ModUpdate:
            {
                var p = Payload<UpdatePayload>(task) ?? new UpdatePayload();
                var ids = profile.Mods.Where(m => m.WorkshopId > 0).Select(m => m.WorkshopId).Distinct().ToList();
                if (ids.Count == 0) return "No Workshop mods to update.";
                var login = _steamCmd.GetSavedUsername();
                if (string.IsNullOrWhiteSpace(login)) return "No saved Steam login; mod update skipped.";
                await _steamCmd.UpdateModsAsync(ids, StagingPath(), login, progress, ct).ConfigureAwait(false);
                await _deploy.DeployModsAsync(profile, progress, ct).ConfigureAwait(false);
                await MaybeRestartAsync(p.AutoRestartAfterUpdate, profile, ct).ConfigureAwait(false);
                return $"Updated {ids.Count} mod(s).{(p.AutoRestartAfterUpdate ? " Restarted." : "")}";
            }
            case ScheduledTaskType.ServerUpdate:
            {
                var p = Payload<UpdatePayload>(task) ?? new UpdatePayload();
                if (string.IsNullOrWhiteSpace(profile.ServerDirectory))
                    return "Server directory not set; update skipped.";
                await _steamCmd.UpdateServerAsync(profile.ServerDirectory, profile.UseProfilingBranch, progress, ct)
                    .ConfigureAwait(false);
                await MaybeRestartAsync(p.AutoRestartAfterUpdate, profile, ct).ConfigureAwait(false);
                return $"Server updated.{(p.AutoRestartAfterUpdate ? " Restarted." : "")}";
            }
            default:
                return "Unknown task type.";
        }
    }

    private async Task MaybeRestartAsync(bool autoRestart, ServerProfile profile, CancellationToken ct)
    {
        if (autoRestart && _server.CurrentState == ServerState.Running)
            await _server.RestartAsync(profile, ct).ConfigureAwait(false);
    }

    /// <summary>Broadcasts the warning countdown over a short-lived RCON client, then restarts the task's
    /// profile. If RCON can't be reached the configured lead time is still honored (silent), so a transient
    /// RCON hiccup doesn't degrade the safest restart into an instant, zero-warning kick.</summary>
    private async Task<string> RestartWithCountdownAsync(ServerProfile profile, RestartPayload p, CancellationToken ct)
    {
        var warnings = p.MinutesWarnings.Where(m => m > 0).Distinct().OrderByDescending(m => m).ToList();
        var leadMinutes = warnings.First();   // max warning = total lead time

        using var rcon = new BattlEyeRconClient();
        if (await rcon.ConnectAsync("127.0.0.1", profile.RconPort, profile.RconPassword, ct).ConfigureAwait(false))
        {
            int? prev = null;
            foreach (var m in warnings)
            {
                if (prev is { } pmins) await Task.Delay(TimeSpan.FromMinutes(pmins - m), ct).ConfigureAwait(false);
                var msg = p.WarningMessage.Replace("{minutes}", m.ToString());
                try { await rcon.SayGlobalAsync(msg).ConfigureAwait(false); } catch { /* keep counting down */ }
                prev = m;
            }
            if (prev is { } last) await Task.Delay(TimeSpan.FromMinutes(last), ct).ConfigureAwait(false);
            rcon.Disconnect();
            await _server.RestartAsync(profile, ct).ConfigureAwait(false);
            return "Server restarted (with countdown).";
        }

        // Could not connect: honor the intended downtime grace, then restart without warnings.
        await Task.Delay(TimeSpan.FromMinutes(leadMinutes), ct).ConfigureAwait(false);
        await _server.RestartAsync(profile, ct).ConfigureAwait(false);
        return $"RCON unreachable; restarted without countdown after {leadMinutes}m.";
    }

    private static async Task BroadcastAsync(ServerProfile profile, IEnumerable<string> messages, CancellationToken ct)
    {
        using var rcon = new BattlEyeRconClient();
        if (!await rcon.ConnectAsync("127.0.0.1", profile.RconPort, profile.RconPassword, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Could not connect to RCON.");
        foreach (var m in messages) await rcon.SayGlobalAsync(m).ConfigureAwait(false);
        rcon.Disconnect();
    }

    private static async Task<string> SendCommandAsync(ServerProfile profile, string command, CancellationToken ct)
    {
        using var rcon = new BattlEyeRconClient();
        if (!await rcon.ConnectAsync("127.0.0.1", profile.RconPort, profile.RconPassword, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Could not connect to RCON.");
        try { return await rcon.SendCommandAsync(command, ct).ConfigureAwait(false); }
        finally { rcon.Disconnect(); }
    }

    private static bool RconConfigured(ServerProfile p) =>
        p.EnableBattlEye && !string.IsNullOrWhiteSpace(p.RconPassword);

    private string StagingPath()
    {
        var configured = _settings.Settings.ModStagingDirectory;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppConstants.AppDataRoot, "Mods")
            : configured;
    }

    // ----- Cron helpers -----

    public DateTime? GetNextRunTime(string cronExpression)
    {
        var expr = TryParse(cronExpression);
        return expr?.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local);
    }

    private static CronExpression? TryParse(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return null;
        cron = cron.Trim();
        try { return CronExpression.Parse(cron); }
        catch { /* try 6-field */ }
        try { return CronExpression.Parse(cron, CronFormat.IncludeSeconds); }
        catch { return null; }
    }

    private static readonly string[] DayNames =
        { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    public string GetHumanReadableCron(string cronExpression)
    {
        if (TryParse(cronExpression) is null) return "Invalid cron expression";
        var f = cronExpression.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5) return "Custom schedule";

        var (min, hour, dom, mon, dow) = (f[0], f[1], f[2], f[3], f[4]);

        if (min == "*" && hour == "*" && dom == "*" && mon == "*" && dow == "*") return "Every minute";
        if (min.StartsWith("*/") && hour == "*" && dom == "*" && mon == "*" && dow == "*")
            return $"Every {min[2..]} minute(s)";
        if (int.TryParse(min, out _) && hour == "*" && dom == "*" && mon == "*" && dow == "*")
            return $"Hourly at :{min.PadLeft(2, '0')}";
        if (int.TryParse(min, out var m2) && int.TryParse(hour, out var h2) && mon == "*")
        {
            var at = $"{h2:00}:{m2:00}";
            if (dom == "*" && dow == "*") return $"Daily at {at}";
            // Cron accepts both 0 and 7 for Sunday.
            if (dom == "*" && int.TryParse(dow, out var d) && d is >= 0 and <= 7)
                return $"Weekly on {DayNames[d == 7 ? 0 : d]} at {at}";
            if (dow == "*" && int.TryParse(dom, out var dm)) return $"Monthly on day {dm} at {at}";
        }
        return "Custom schedule";
    }

    // ----- CRUD -----

    public async Task<List<ScheduledTask>> GetTasksForProfileAsync(int profileId)
    {
        var list = new List<ScheduledTask>();
        await using var conn = _db.CreateOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE ProfileId = $pid ORDER BY Name;";
        cmd.Parameters.AddWithValue("$pid", profileId);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false)) list.Add(Map(reader));
        return list;
    }

    private async Task<ScheduledTask?> GetByIdAsync(int id)
    {
        await using var conn = _db.CreateOpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        return await reader.ReadAsync().ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<ScheduledTask> CreateTaskAsync(ScheduledTask task)
    {
        task.NextRunAt = task.IsEnabled ? GetNextRunTime(task.CronExpression) : null;
        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO ScheduledTasks (ProfileId, Name, TaskType, CronExpression, NextRunAt, LastRunAt, " +
                "LastRunResult, IsEnabled, PayloadJson) VALUES ($pid,$name,$type,$cron,$next,$last,$res,$en,$pay); " +
                "SELECT last_insert_rowid();";
            Bind(cmd, task);
            task.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        }
        finally { _db.WriteLock.Release(); }
        RaiseChanged();
        return task;
    }

    public async Task UpdateTaskAsync(ScheduledTask task)
    {
        task.NextRunAt = task.IsEnabled ? GetNextRunTime(task.CronExpression) : null;
        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var cmd = conn.CreateCommand();
            // Deliberately does NOT write LastRunAt/LastRunResult: those are owned by RecordRunAsync, and the
            // editor edits a stale snapshot — writing them back would clobber a run recorded while the dialog
            // was open. ($last/$res from Bind are simply unreferenced here.)
            cmd.CommandText =
                "UPDATE ScheduledTasks SET Name=$name, TaskType=$type, CronExpression=$cron, NextRunAt=$next, " +
                "IsEnabled=$en, PayloadJson=$pay WHERE Id=$id;";
            Bind(cmd, task);
            cmd.Parameters.AddWithValue("$id", task.Id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _db.WriteLock.Release(); }
        RaiseChanged();
    }

    public async Task DeleteTaskAsync(int id)
    {
        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ScheduledTasks WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _db.WriteLock.Release(); }
        RaiseChanged();
    }

    public async Task<ScheduledTask> CloneTaskAsync(int id)
    {
        var src = await GetByIdAsync(id).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"Task {id} not found.");
        var copy = new ScheduledTask
        {
            ProfileId = src.ProfileId,
            Name = src.Name + " (copy)",
            TaskType = src.TaskType,
            CronExpression = src.CronExpression,
            IsEnabled = false,           // clones start disabled so they don't fire unexpectedly
            PayloadJson = src.PayloadJson,
        };
        return await CreateTaskAsync(copy).ConfigureAwait(false);
    }

    public Task EnableTaskAsync(int id) => SetEnabledAsync(id, true);
    public Task DisableTaskAsync(int id) => SetEnabledAsync(id, false);

    private async Task SetEnabledAsync(int id, bool enabled)
    {
        var task = await GetByIdAsync(id).ConfigureAwait(false);
        if (task is null) return;
        task.IsEnabled = enabled;
        await UpdateTaskAsync(task).ConfigureAwait(false);
    }

    private async Task PersistNextRunAsync(int id, DateTime? next)
    {
        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE ScheduledTasks SET NextRunAt = $next WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$next", (object?)next?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _db.WriteLock.Release(); }
    }

    private async Task RecordRunAsync(int id, string result)
    {
        await _db.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE ScheduledTasks SET LastRunAt = $at, LastRunResult = $res WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$res", Trim(result, 400));
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _db.WriteLock.Release(); }
    }

    // ----- Mapping / helpers -----

    private const string SelectColumns =
        "SELECT Id, ProfileId, Name, TaskType, CronExpression, NextRunAt, LastRunAt, LastRunResult, " +
        "IsEnabled, PayloadJson FROM ScheduledTasks";

    private static ScheduledTask Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        ProfileId = r.GetInt32(1),
        Name = r.GetString(2),
        TaskType = Enum.TryParse<ScheduledTaskType>(r.GetString(3), out var t) ? t : ScheduledTaskType.Message,
        CronExpression = r.GetString(4),
        NextRunAt = ParseDate(r, 5),
        LastRunAt = ParseDate(r, 6),
        LastRunResult = r.GetString(7),
        IsEnabled = r.GetInt32(8) != 0,
        PayloadJson = r.GetString(9),
    };

    private static DateTime? ParseDate(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null
            : DateTime.Parse(r.GetString(i), null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static void Bind(SqliteCommand cmd, ScheduledTask t)
    {
        cmd.Parameters.AddWithValue("$pid", t.ProfileId);
        cmd.Parameters.AddWithValue("$name", t.Name);
        cmd.Parameters.AddWithValue("$type", t.TaskType.ToString());
        cmd.Parameters.AddWithValue("$cron", t.CronExpression);
        cmd.Parameters.AddWithValue("$next", (object?)t.NextRunAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last", (object?)t.LastRunAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$res", t.LastRunResult);
        cmd.Parameters.AddWithValue("$en", t.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$pay", string.IsNullOrWhiteSpace(t.PayloadJson) ? "{}" : t.PayloadJson);
    }

    private static T? Payload<T>(ScheduledTask task) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(task.PayloadJson, Json); }
        catch { return null; }
    }

    private static string Trim(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = max;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;   // never split a UTF-16 surrogate pair
        return s[..cut] + "…";
    }

    private bool TryClaim(int id) { lock (_runLock) return _running.Add(id); }
    private void Release(int id) { lock (_runLock) _running.Remove(id); }

    private void RaiseChanged()
    {
        try { TasksChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error(ex, "A TasksChanged handler threw."); }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); _cts?.Dispose(); } catch { /* ignore */ }
    }
}
