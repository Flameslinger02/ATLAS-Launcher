using System.Text;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Data;
using Discord;
using Discord.Interactions;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Atlas.Pages.DiscordBot;

/// <summary>
/// ATLAS Discord slash commands (<c>/atlas …</c>). Instantiated per-interaction by the InteractionService
/// from the app DI container. Every command runs a channel/role permission check first and is logged to
/// <c>DiscordCommandLog</c>. RCON actions use a short-lived <see cref="BattlEyeRconClient"/> (like the
/// scheduler) so they never disrupt the Console page's shared session.
/// </summary>
[Group("atlas", "ATLAS server administration")]
public sealed class AtlasCommandModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly ISettingsService _settings;
    private readonly IProfileService _profiles;
    private readonly IServerProcessService _server;
    private readonly IHeadlessClientService _hc;
    private readonly ISchedulerService _scheduler;
    private readonly ISteamCmdService _steam;
    private readonly IModDeploymentService _deploy;
    private readonly AtlasDatabase _db;

    public AtlasCommandModule(
        ISettingsService settings, IProfileService profiles, IServerProcessService server,
        IHeadlessClientService hc, ISchedulerService scheduler, ISteamCmdService steam,
        IModDeploymentService deploy, AtlasDatabase db)
    {
        _settings = settings;
        _profiles = profiles;
        _server = server;
        _hc = hc;
        _scheduler = scheduler;
        _steam = steam;
        _deploy = deploy;
        _db = db;
    }

    private DiscordBotConfig Cfg => _settings.Settings.DiscordBot;

    // ---------- Info commands ----------

    [SlashCommand("status", "Show the current server status.")]
    public async Task Status()
    {
        if (!await CheckAsync()) return;
        var (cpu, mem) = _server.GetResourceUsage();
        var p = _profiles.ActiveProfile;
        var embed = DiscordBotService.Embed($"ATLAS — {p?.ServerName ?? "No active profile"}")
            .AddField("State", _server.CurrentState.ToString(), true)
            .AddField("Uptime", DiscordBotService.FormatUptime(_server.Uptime), true)
            .AddField("Crashes", _server.CrashCount.ToString(), true)
            .AddField("CPU", $"{cpu:0} %", true)
            .AddField("RAM", $"{mem / (1024.0 * 1024.0):0} MB", true)
            .Build();
        await RespondAsync(embed: embed);
        await LogAsync("status", "", "ok");
    }

    [SlashCommand("performance", "CPU / RAM / uptime / players.")]
    public async Task Performance()
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        var (cpu, mem) = _server.GetResourceUsage();
        var count = await PlayerCountAsync();
        var embed = DiscordBotService.Embed("Performance")
            .AddField("CPU", $"{cpu:0} %", true)
            .AddField("RAM", $"{mem / (1024.0 * 1024.0):0} MB", true)
            .AddField("Uptime", DiscordBotService.FormatUptime(_server.Uptime), true)
            .AddField("Players", count?.ToString() ?? "—", true)
            .Build();
        await FollowupAsync(embed: embed);
        await LogAsync("performance", "", "ok");
    }

    [SlashCommand("players", "List online players.")]
    public async Task Players()
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        var (ok, players, err) = await RconQueryAsync(r => r.GetPlayersAsync());
        if (!ok) { await FollowupAsync(err); return; }
        var embed = DiscordBotService.Embed($"Players ({players!.Count})");
        embed.WithDescription(Clamp(players.Count == 0
            ? "No players online."
            : string.Join("\n", players.Take(25).Select(pl =>
                $"`{pl.Id}` {pl.Name} — {pl.Ping}ms{(pl.InLobby ? " *(lobby)*" : "")}"))));
        if (players.Count > 25) embed.WithFooter($"Showing 25 of {players.Count}");
        await FollowupAsync(embed: embed.Build());
        await LogAsync("players", "", $"{players.Count} online");
    }

    [SlashCommand("mods", "Show the active profile's mod list.")]
    public async Task Mods()
    {
        if (!await CheckAsync()) return;
        var p = _profiles.ActiveProfile;
        var mods = p?.Mods ?? new List<ArmaModEntry>();
        var embed = DiscordBotService.Embed($"Mods ({mods.Count})");
        embed.WithDescription(Clamp(mods.Count == 0
            ? "No mods configured."
            : string.Join("\n", mods.OrderBy(m => m.LoadOrder).Take(40).Select(m => $"• {m.Name}"))));
        if (mods.Count > 40) embed.WithFooter($"Showing 40 of {mods.Count}");
        await RespondAsync(embed: embed.Build());
        await LogAsync("mods", "", $"{mods.Count} mods");
    }

    [SlashCommand("hc-status", "Show headless-client instance status.")]
    public async Task HcStatus()
    {
        if (!await CheckAsync()) return;
        var instances = _hc.Instances;
        var embed = DiscordBotService.Embed($"Headless Clients ({instances.Count})");
        embed.WithDescription(Clamp(instances.Count == 0
            ? "No headless clients configured."
            : string.Join("\n", instances.Select(i => $"**{i.Name}** — {i.State} (crashes: {i.CrashCount})"))));
        await RespondAsync(embed: embed.Build());
        await LogAsync("hc-status", "", "ok");
    }

    [SlashCommand("schedule-list", "Show upcoming scheduled tasks.")]
    public async Task ScheduleList()
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        var p = _profiles.ActiveProfile;
        if (p is null) { await FollowupAsync("No active profile."); return; }
        var tasks = await _scheduler.GetTasksForProfileAsync(p.Id);
        var embed = DiscordBotService.Embed($"Scheduled Tasks ({tasks.Count})");
        embed.WithDescription(Clamp(tasks.Count == 0
            ? "No scheduled tasks."
            : string.Join("\n", tasks.Take(20).Select(t =>
                $"`{t.Id}` **{t.Name}** [{t.TaskType}] {(t.IsEnabled ? "" : "(disabled) ")}— next: " +
                (t.NextRunAt is { } n ? n.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—")))));
        await FollowupAsync(embed: embed.Build());
        await LogAsync("schedule-list", "", $"{tasks.Count} tasks");
    }

    [SlashCommand("crash-history", "Show the last 10 crashes.")]
    public async Task CrashHistory()
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        var lines = new List<string>();
        try
        {
            await using var conn = _db.CreateOpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CrashedAt, ExitCode, AutoRestarted, Notes FROM CrashLog ORDER BY Id DESC LIMIT 10;";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var when = DateTime.TryParse(r.GetString(0), out var dt) ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : r.GetString(0);
                lines.Add($"`{when}` exit {r.GetInt32(1)}{(r.GetInt32(2) != 0 ? " (auto-restarted)" : "")} {r.GetString(3)}");
            }
        }
        catch (Exception ex) { Log.Debug(ex, "crash-history query failed."); }
        var embed = DiscordBotService.Embed("Crash History")
            .WithDescription(Clamp(lines.Count == 0 ? "No crashes recorded." : string.Join("\n", lines))).Build();
        await FollowupAsync(embed: embed);
        await LogAsync("crash-history", "", $"{lines.Count} entries");
    }

    [SlashCommand("logs", "Download the last N lines of the server RPT.")]
    public async Task Logs([Summary("lines", "How many lines (1-500).")] int lines = 50)
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        var p = _profiles.ActiveProfile;
        var dir = p is null ? null : Path.Combine(p.ServerDirectory, "profiles");
        var rpt = dir is not null && Directory.Exists(dir)
            ? new DirectoryInfo(dir).GetFiles("*.rpt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (rpt is null) { await FollowupAsync("No RPT log file found."); return; }
        lines = Math.Clamp(lines, 1, 500);
        string content;
        try
        {
            await using var fs = new FileStream(rpt.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            content = await sr.ReadToEndAsync();
        }
        catch (Exception ex) { await FollowupAsync($"Could not read the RPT: {ex.Message}"); return; }
        var tail = string.Join("\n", content.Replace("\r", "").Split('\n').TakeLast(lines));
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(tail));
        await FollowupWithFileAsync(ms, "atlas-rpt.txt", $"Last {lines} line(s) of {rpt.Name}");
        await LogAsync("logs", lines.ToString(), "ok");
    }

    [SlashCommand("help", "List the ATLAS commands.")]
    public async Task Help()
    {
        if (!await CheckAsync()) return;
        var embed = DiscordBotService.Embed("ATLAS Commands").WithDescription(
            "**Info:** `/atlas status` `/atlas performance` `/atlas players` `/atlas mods` `/atlas hc-status` " +
            "`/atlas schedule-list` `/atlas crash-history` `/atlas logs [lines]`\n" +
            "**Control:** `/atlas start` `/atlas stop` `/atlas restart [minutes]` `/atlas lock` `/atlas unlock` " +
            "`/atlas mission <name>` `/atlas hc-restart <index>` `/atlas schedule-skip <id>` `/atlas update-mods`\n" +
            "**Players:** `/atlas kick <id> [reason]` `/atlas ban <id> <duration> [reason]` `/atlas unban <guid>` " +
            "`/atlas say <message>`\n" +
            "**Owner:** `/atlas rcon <command>`").Build();
        await RespondAsync(embed: embed, ephemeral: true);
        await LogAsync("help", "", "ok");
    }

    // ---------- RCON action commands ----------

    [SlashCommand("say", "Broadcast a global message via RCON.")]
    public async Task Say([Summary("message")] string message)
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        await FollowupAsync(await RconDoAsync(r => r.SayGlobalAsync(message), "Broadcast sent."));
        await LogAsync("say", message, "ok");
    }

    [SlashCommand("lock", "Lock the server (no new joins).")]
    public async Task Lock()
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        await FollowupAsync(await RconDoAsync(r => r.LockServerAsync(), "Server locked."));
        await LogAsync("lock", "", "ok");
    }

    [SlashCommand("unlock", "Unlock the server.")]
    public async Task Unlock()
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        await FollowupAsync(await RconDoAsync(r => r.UnlockServerAsync(), "Server unlocked."));
        await LogAsync("unlock", "", "ok");
    }

    [SlashCommand("mission", "Change the mission (#mission <name>).")]
    public async Task Mission([Summary("name", "Mission template name.")] string name)
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        await FollowupAsync(await RconDoAsync(r => r.SendCommandAsync($"#mission {name}"), $"Mission set: {name}"));
        await LogAsync("mission", name, "ok");
    }

    [SlashCommand("kick", "Kick a player by id.")]
    public async Task Kick([Summary("player_id")] int playerId, [Summary("reason")] string reason = "Kicked by admin")
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        await FollowupAsync(await RconDoAsync(r => r.KickPlayerAsync(playerId, reason), $"Kicked #{playerId}."));
        await LogAsync("kick", $"{playerId} {reason}", "ok");
    }

    [SlashCommand("ban", "Ban a player by id.")]
    public async Task Ban(
        [Summary("player_id")] int playerId,
        [Summary("duration")]
        [Choice("1 hour", "1h")] [Choice("6 hours", "6h")] [Choice("24 hours", "24h")]
        [Choice("7 days", "7d")] [Choice("30 days", "30d")] [Choice("permanent", "permanent")] string duration,
        [Summary("reason")] string reason = "Banned by admin")
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        // BattlEye: -1 = permanent, finite minutes otherwise. 0 would be an immediate-expiry (no-op) ban, so
        // map "permanent" (and any unmatched value) to -1.
        var minutes = duration switch { "1h" => 60, "6h" => 360, "24h" => 1440, "7d" => 10080, "30d" => 43200, _ => -1 };
        await FollowupAsync(await RconDoAsync(r => r.BanPlayerAsync(playerId, minutes, reason),
            $"Banned #{playerId} ({duration})."));
        await LogAsync("ban", $"{playerId} {duration} {reason}", "ok");
    }

    [SlashCommand("unban", "Remove a ban by GUID.")]
    public async Task Unban([Summary("guid")] string guid)
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        // Resolve the ban index AND remove it on a SINGLE RCON session — BattlEye indices are positional, so
        // resolving on one connection and removing on another risks unbanning the wrong entry if the list shifts.
        var (ok, result, err) = await RconQueryAsync(async r =>
        {
            var bans = await r.GetBansAsync();
            var match = bans.FirstOrDefault(b => string.Equals(b.GuidOrIp, guid, StringComparison.OrdinalIgnoreCase));
            if (match is null) return $"No ban found for `{guid}`.";
            await r.UnbanAsync(match.Index);
            return $"Unbanned `{guid}`.";
        });
        await FollowupAsync(ok ? result! : err);
        await LogAsync("unban", guid, ok ? "ok" : "error");
    }

    [SlashCommand("rcon", "Run a raw RCON command (owner only).")]
    public async Task Rcon([Summary("command")] string command)
    {
        if (!await CheckAsync(ownerOnly: true)) return;
        await DeferAsync();
        var (ok, resp, err) = await RconQueryAsync(r => r.SendCommandAsync(command));
        var text = ok ? (string.IsNullOrWhiteSpace(resp) ? "Command sent." : Truncate(resp!, 1800)) : err;
        await FollowupAsync($"```\n{text}\n```");
        await LogAsync("rcon", command, ok ? "ok" : "error");
    }

    [SlashCommand("hc-restart", "Restart a headless client by index.")]
    public async Task HcRestart([Summary("index")] int index)
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        var p = _profiles.ActiveProfile;
        if (p is null) { await FollowupAsync("No active profile."); return; }
        try { await _hc.RestartSingleAsync(p, index); await FollowupAsync($"Restarted HC{index}."); }
        catch (Exception ex) { await FollowupAsync($"Failed: {ex.Message}"); }
        await LogAsync("hc-restart", index.ToString(), "ok");
    }

    [SlashCommand("schedule-skip", "Skip the next run of a scheduled task.")]
    public async Task ScheduleSkip([Summary("id")] int id)
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        await _scheduler.SkipNextAsync(id);
        await FollowupAsync($"Skipped the next run of task #{id}.");
        await LogAsync("schedule-skip", id.ToString(), "ok");
    }

    [SlashCommand("update-mods", "Update Workshop mods via SteamCMD, then redeploy.")]
    public async Task UpdateMods()
    {
        if (!await CheckAsync()) return;
        await DeferAsync();
        var p = _profiles.ActiveProfile;
        if (p is null) { await FollowupAsync("No active profile."); return; }
        var ids = p.Mods.Where(m => m.WorkshopId > 0).Select(m => m.WorkshopId).Distinct().ToList();
        if (ids.Count == 0) { await FollowupAsync("No Workshop mods to update."); return; }
        var login = _steam.GetSavedUsername();
        if (string.IsNullOrWhiteSpace(login)) { await FollowupAsync("No saved Steam login; update skipped."); return; }

        // A full SteamCMD update + redeploy can exceed Discord's ~15-min interaction-token lifetime, after which
        // FollowupAsync would throw. Acknowledge now while the token is fresh, then report completion via a plain
        // channel message (no token needed).
        await FollowupAsync($"Updating {ids.Count} mod(s) — this can take several minutes...");
        var channel = Context.Channel;
        string result;
        try
        {
            var staging = string.IsNullOrWhiteSpace(_settings.Settings.ModStagingDirectory)
                ? Path.Combine(AppConstants.AppDataRoot, "Mods") : _settings.Settings.ModStagingDirectory;
            var noop = new Progress<string>(_ => { });
            await _steam.UpdateModsAsync(ids, staging, login, noop, CancellationToken.None);
            await _deploy.DeployModsAsync(p, noop, CancellationToken.None);
            result = $"✅ Updated {ids.Count} mod(s).";
        }
        catch (Exception ex) { result = $"⚠ Mod update failed: {ex.Message}"; }
        try { await channel.SendMessageAsync(result); } catch (Exception ex) { Log.Warning(ex, "update-mods completion post failed."); }
        await LogAsync("update-mods", $"{ids.Count} mods", result);
    }

    // ---------- Control commands (confirmation buttons) ----------

    [SlashCommand("start", "Start the server (with confirmation).")]
    public async Task Start()
    {
        if (!await CheckAsync()) return;
        await RespondAsync("Start the server?", components: ConfirmButtons("start", 0), ephemeral: true);
    }

    [SlashCommand("stop", "Stop the server (with confirmation).")]
    public async Task Stop([Summary("reason")] string reason = "")
    {
        if (!await CheckAsync()) return;
        await RespondAsync("Stop the server?", components: ConfirmButtons("stop", 0), ephemeral: true);
    }

    [SlashCommand("restart", "Restart the server (with confirmation).")]
    public async Task Restart([Summary("minutes", "Warning countdown minutes (0 = now).")] int minutes = 0)
    {
        if (!await CheckAsync()) return;
        await RespondAsync($"Restart the server{(minutes > 0 ? $" in {minutes} minute(s)" : " now")}?",
            components: ConfirmButtons("restart", minutes), ephemeral: true);
    }

    private static MessageComponent ConfirmButtons(string action, int arg) => new ComponentBuilder()
        .WithButton("Confirm", $"atlas_do:{action}:{arg}:{DateTime.UtcNow.Ticks}", ButtonStyle.Danger)
        .WithButton("Cancel", "atlas_cancel", ButtonStyle.Secondary)
        .Build();

    [ComponentInteraction("atlas_do:*:*:*", ignoreGroupNames: true)]
    public async Task ConfirmDo(string action, string argStr, string ticksStr)
    {
        if (!await CheckAsync()) return;
        if (long.TryParse(ticksStr, out var ticks) &&
            (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds > 30)
        {
            await RespondAsync("⌛ Confirmation expired.", ephemeral: true);
            return;
        }
        var p = _profiles.ActiveProfile;
        if (p is null) { await RespondAsync("No active profile.", ephemeral: true); return; }
        int.TryParse(argStr, out var arg);
        await DeferAsync();
        string result;
        try
        {
            switch (action)
            {
                case "start": await _server.LaunchAsync(p); result = "Server starting."; break;
                case "stop": await _server.StopAsync(false); result = "Server stopping."; break;
                case "restart" when arg > 0: _ = RestartWithCountdownAsync(p, arg); result = $"Restart scheduled in {arg} minute(s)."; break;
                case "restart": await _server.RestartAsync(p); result = "Server restarting."; break;
                default: result = "Unknown action."; break;
            }
        }
        catch (Exception ex) { result = $"Failed: {ex.Message}"; }
        await ModifyOriginalResponseAsync(m => { m.Content = $"✅ {result}"; m.Components = new ComponentBuilder().Build(); });
        await LogAsync(action, argStr, result);
    }

    [ComponentInteraction("atlas_cancel", ignoreGroupNames: true)]
    public async Task ConfirmCancel() => await RespondAsync("Cancelled.", ephemeral: true);

    private async Task RestartWithCountdownAsync(ServerProfile p, int minutes)
    {
        try
        {
            await RconDoAsync(r => r.SayGlobalAsync($"Server restarting in {minutes} minute(s)!"), "");
            await Task.Delay(TimeSpan.FromMinutes(minutes));
            await _server.RestartAsync(p);
        }
        catch (Exception ex) { Log.Warning(ex, "Discord restart countdown failed."); }
    }

    // ---------- Helpers ----------

    private async Task<bool> CheckAsync(bool ownerOnly = false)
    {
        var cfg = Cfg;
        // Commands must run in a guild channel. Guild roles (the only authorization signal we have) cannot be
        // evaluated in a DM and there is no per-user allow-list, so DM command execution is denied outright.
        // AllowDMs is retained in config but intentionally NOT honored for command execution — without this,
        // AllowDMs + an empty admin-role list would let anyone DM the bot privileged commands. See PHASE_12_COMPLETE.md.
        if (Context.Guild is null)
            return await Deny("Commands must be run in a server channel.");
        if (cfg.CommandChannelId is { } cc && cc != 0 && Context.Channel?.Id != cc)
            return await Deny("Wrong channel.");
        var guildUser = Context.User as IGuildUser;
        if (cfg.AdminRoleIds is { Length: > 0 } admin &&
            (guildUser is null || !admin.Any(r => guildUser.RoleIds.Contains(r))))
            return await Deny("Insufficient permissions.");
        if (ownerOnly)
        {
            // Fail closed: owner-only commands (e.g. /atlas rcon) require a CONFIGURED owner role AND membership.
            if (cfg.OwnerRoleId is not { } owner || owner == 0 || guildUser is null || !guildUser.RoleIds.Contains(owner))
                return await Deny("Owner role required (set the Owner Role ID in the bot config).");
        }
        return true;
    }

    private async Task<bool> Deny(string msg)
    {
        await RespondAsync($"❌ {msg}", ephemeral: true);
        return false;
    }

    /// <summary>Runs a fire-and-forget RCON action via a short-lived client; returns a user-facing result string.</summary>
    private async Task<string> RconDoAsync(Func<IBattlEyeRconClient, Task> action, string okMessage)
    {
        var (host, p) = ("127.0.0.1", _profiles.ActiveProfile);
        if (p is null) return "No active profile.";
        if (!p.EnableBattlEye || string.IsNullOrWhiteSpace(p.RconPassword)) return "RCON not configured.";
        using var rcon = new BattlEyeRconClient();
        if (!await rcon.ConnectAsync(host, p.RconPort, p.RconPassword)) return "RCON connect failed.";
        try { await action(rcon); return okMessage; }
        catch (Exception ex) { return $"RCON error: {ex.Message}"; }
        finally { rcon.Disconnect(); }
    }

    /// <summary>Runs an RCON query via a short-lived client; returns (ok, value, errorText).</summary>
    private async Task<(bool ok, T? value, string error)> RconQueryAsync<T>(Func<IBattlEyeRconClient, Task<T>> query)
    {
        var p = _profiles.ActiveProfile;
        if (p is null) return (false, default, "No active profile.");
        if (!p.EnableBattlEye || string.IsNullOrWhiteSpace(p.RconPassword)) return (false, default, "RCON not configured.");
        using var rcon = new BattlEyeRconClient();
        if (!await rcon.ConnectAsync("127.0.0.1", p.RconPort, p.RconPassword)) return (false, default, "RCON connect failed.");
        try { return (true, await query(rcon), ""); }
        catch (Exception ex) { return (false, default, $"RCON error: {ex.Message}"); }
        finally { rcon.Disconnect(); }
    }

    private async Task<int?> PlayerCountAsync()
    {
        var (ok, players, _) = await RconQueryAsync(r => r.GetPlayersAsync());
        return ok ? players!.Count : null;
    }

    private async Task LogAsync(string command, string args, string result)
    {
        try
        {
            var pid = _profiles.ActiveProfile?.Id;
            await _db.WriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var conn = _db.CreateOpenConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO DiscordCommandLog (ProfileId, DiscordUserId, DiscordUserName, Command, Arguments, Result, ExecutedAt) " +
                    "VALUES ($pid, $uid, $un, $c, $a, $r, $at);";
                cmd.Parameters.AddWithValue("$pid", pid is > 0 ? pid.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$uid", Context.User.Id.ToString());
                cmd.Parameters.AddWithValue("$un", Context.User.Username);
                cmd.Parameters.AddWithValue("$c", command);
                cmd.Parameters.AddWithValue("$a", args ?? string.Empty);
                cmd.Parameters.AddWithValue("$r", Truncate(result, 400));
                cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally { _db.WriteLock.Release(); }
        }
        catch (Exception ex) { Log.Debug(ex, "DiscordCommandLog write failed."); }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Clamps an embed description to Discord's 4096-char limit (with margin) so Build()/Send never throws.</summary>
    private static string Clamp(string s, int max = 4000) => s.Length <= max ? s : s[..max] + "\n…";
}
