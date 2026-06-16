using System.Reactive.Subjects;
using Atlas.Core.Models;
using Atlas.Pages.DiscordBot;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IDiscordBotService"/>
public sealed class DiscordBotService : IDiscordBotService, IHostedService, IDisposable
{
    public static readonly Color AtlasColor = new(0xCC2200);

    private readonly ISettingsService _settings;
    private readonly ISecretProtector _secrets;
    private readonly IServerProcessService _server;
    private readonly IBattlEyeRconClient _rcon;
    private readonly IProfileService _profiles;
    private readonly IServiceProvider _services;

    private readonly BehaviorSubject<DiscordBotState> _state = new(DiscordBotState.Offline);
    private readonly List<IDisposable> _subs = new();
    private readonly object _gate = new();                       // guards _client / _interactions
    private readonly SemaphoreSlim _connectGate = new(1, 1);     // serializes Start/StopBotAsync
    private readonly SemaphoreSlim _notifyGate = new(1, 1);      // serializes the two notification handlers
    private readonly SemaphoreSlim _statusGate = new(1, 1);      // serializes status-message get-or-create
    private readonly Dictionary<string, string> _lastPlayers = new(StringComparer.Ordinal);  // guid -> name
    private bool _playerBaseline;
    private bool _commandsRegistered;
    private ServerState _lastServerState = ServerState.Stopped;

    private DiscordSocketClient? _client;
    private InteractionService? _interactions;

    public DiscordBotState BotState => _state.Value;
    public IObservable<DiscordBotState> StateChanged => _state;

    private DiscordBotConfig Config => _settings.Settings.DiscordBot;

    public static string Version =>
        typeof(DiscordBotService).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";

    public DiscordBotService(
        ISettingsService settings, ISecretProtector secrets, IServerProcessService server,
        IBattlEyeRconClient rcon, IProfileService profiles, IServiceProvider services)
    {
        _settings = settings;
        _secrets = secrets;
        _server = server;
        _rcon = rcon;
        _profiles = profiles;
        _services = services;

        // Notifications are driven by service events (no circular dependency on those services).
        _subs.Add(_server.StateChanged.Subscribe(s => _ = OnServerStateAsync(s)));
        _subs.Add(_rcon.PlayersUpdated.Subscribe(p => _ = OnPlayersUpdatedAsync(p)));
    }

    // ----- Hosted lifecycle -----

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var cfg = Config;
        if (!cfg.IsEnabled || string.IsNullOrWhiteSpace(cfg.TokenEncrypted)) return;
        var token = _secrets.Decrypt(cfg.TokenEncrypted);
        if (string.IsNullOrWhiteSpace(token)) { Log.Warning("Discord bot enabled but the token could not be decrypted."); return; }
        try { await StartBotAsync(token, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { Log.Error(ex, "Discord bot auto-start failed."); }
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await StopBotAsync().ConfigureAwait(false);

    // ----- Connect / disconnect -----

    public async Task StartBotAsync(string token, CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopBotCoreAsync().ConfigureAwait(false);
            _commandsRegistered = false;
            await StartBotCoreAsync(token).ConfigureAwait(false);
        }
        finally { _connectGate.Release(); }
    }

    private async Task StartBotCoreAsync(string token)
    {
        SetState(DiscordBotState.Connecting);
        try
        {
            var client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds,
                LogLevel = LogSeverity.Warning,
                AlwaysDownloadUsers = false,
            });
            var interactions = new InteractionService(client.Rest, new InteractionServiceConfig
            {
                DefaultRunMode = RunMode.Async,
                LogLevel = LogSeverity.Warning,
            });
            await interactions.AddModuleAsync<AtlasCommandModule>(_services).ConfigureAwait(false);

            client.Log += OnLog;
            client.Ready += OnReady;
            client.Disconnected += OnDisconnected;
            client.InteractionCreated += OnInteraction;

            lock (_gate) { _client = client; _interactions = interactions; }

            await client.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
            await client.StartAsync().ConfigureAwait(false);
            Log.Information("Discord bot connecting...");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Discord bot login failed.");
            SetState(DiscordBotState.Error);
            await StopBotCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopBotAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try { await StopBotCoreAsync().ConfigureAwait(false); }
        finally { _connectGate.Release(); }
    }

    private async Task StopBotCoreAsync()
    {
        DiscordSocketClient? client;
        InteractionService? interactions;
        lock (_gate) { client = _client; _client = null; interactions = _interactions; _interactions = null; }
        if (interactions is not null) { try { interactions.Dispose(); } catch { /* ignore */ } }
        if (client is not null)
        {
            try { await client.LogoutAsync().ConfigureAwait(false); } catch { /* ignore */ }
            try { await client.StopAsync().ConfigureAwait(false); } catch { /* ignore */ }
            try { client.Dispose(); } catch { /* ignore */ }
        }
        SetState(DiscordBotState.Offline);
    }

    // ----- Discord events -----

    private Task OnLog(LogMessage msg)
    {
        if (msg.Exception is not null) Log.Warning(msg.Exception, "[Discord] {Message}", msg.Message);
        else Log.Debug("[Discord] {Message}", msg.Message);
        return Task.CompletedTask;
    }

    private async Task OnReady()
    {
        SetState(DiscordBotState.Online);
        // Register once per connection — Ready fires again on every gateway reconnect; re-registering each
        // time burns the command-create rate budget.
        if (!_commandsRegistered)
        {
            try { await ReRegisterCommandsAsync().ConfigureAwait(false); _commandsRegistered = true; }
            catch (Exception ex) { Log.Error(ex, "Slash-command registration failed."); }
        }
        try { await UpdateStatusMessageAsync().ConfigureAwait(false); } catch { /* best effort */ }
        Log.Information("Discord bot online.");
    }

    private Task OnDisconnected(Exception? ex)
    {
        if (BotState == DiscordBotState.Online) SetState(DiscordBotState.Connecting);  // auto-reconnect handled by Discord.Net
        return Task.CompletedTask;
    }

    private async Task OnInteraction(SocketInteraction interaction)
    {
        DiscordSocketClient? client;
        InteractionService? interactions;
        lock (_gate) { client = _client; interactions = _interactions; }
        if (client is null || interactions is null) return;
        try
        {
            var ctx = new SocketInteractionContext(client, interaction);
            await interactions.ExecuteCommandAsync(ctx, _services).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Discord interaction failed.");
            try { if (!interaction.HasResponded) await interaction.RespondAsync("⚠ Command error.", ephemeral: true).ConfigureAwait(false); }
            catch { /* ignore */ }
        }
    }

    public async Task ReRegisterCommandsAsync()
    {
        DiscordSocketClient? client;
        InteractionService? interactions;
        lock (_gate) { client = _client; interactions = _interactions; }
        if (client is null || interactions is null) return;

        var guildId = Config.PrimaryGuildId;
        if (guildId is { } gid && gid != 0)
            await interactions.RegisterCommandsToGuildAsync(gid).ConfigureAwait(false);   // instant in one guild
        else
            await interactions.RegisterCommandsGloballyAsync().ConfigureAwait(false);      // ~1h propagation
    }

    public Task<IReadOnlyList<(ulong Id, string Name)>> GetGuildsAsync()
    {
        DiscordSocketClient? client;
        lock (_gate) client = _client;
        IReadOnlyList<(ulong, string)> guilds = client?.Guilds.Select(g => (g.Id, g.Name)).ToList()
                                                ?? new List<(ulong, string)>();
        return Task.FromResult(guilds);
    }

    // ----- Notifications (subscription-driven) -----

    private async Task OnServerStateAsync(ServerState s)
    {
        // Serialized so the read-then-write of _lastServerState and the posts can't interleave across
        // concurrent state events. The whole body is guarded so nothing escapes the fire-and-forget Task.
        await _notifyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var prev = _lastServerState;
            _lastServerState = s;
            if (BotState != DiscordBotState.Online) return;
            var profile = _profiles.ActiveProfile;
            if (s == ServerState.Running && prev is not ServerState.Running)
                await PostStatusAsync(Embed("🟢 Server Started").WithDescription(profile?.ServerName ?? "Server").Build());
            else if (s == ServerState.Stopped && prev is ServerState.Running or ServerState.Stopping)
                await PostStatusAsync(Embed("⏹ Server Stopped").WithDescription(profile?.ServerName ?? "Server").Build());
            else if (s == ServerState.Crashed && Config.NotifyCrash)
                await PostStatusAsync(Embed("🔴 Server Crashed")
                    .WithDescription($"{profile?.ServerName ?? "Server"} crashed (crash #{_server.CrashCount}).").Build());
        }
        catch (Exception ex) { Log.Debug(ex, "Discord server-state notification failed."); }
        finally { _notifyGate.Release(); }

        try { await UpdateStatusMessageAsync().ConfigureAwait(false); }   // uses _statusGate, outside _notifyGate
        catch (Exception ex) { Log.Debug(ex, "Discord status update failed."); }
    }

    private async Task OnPlayersUpdatedAsync(IReadOnlyList<RconPlayer> players)
    {
        await _notifyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (BotState != DiscordBotState.Online) return;
            var cfg = Config;
            // Indexer assignment (not ToDictionary) tolerates duplicate GUIDs without throwing.
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in players)
                if (!string.IsNullOrWhiteSpace(p.Guid)) current[p.Guid] = p.Name;

            if (_playerBaseline)
            {
                if (cfg.NotifyPlayerJoin)
                    foreach (var (_, name) in current.Where(kv => !_lastPlayers.ContainsKey(kv.Key)))
                        await PostPlayerLogAsync(Embed("➕ Player Joined").WithDescription(name).Build());
                if (cfg.NotifyPlayerLeave)
                    foreach (var (_, name) in _lastPlayers.Where(kv => !current.ContainsKey(kv.Key)))
                        await PostPlayerLogAsync(Embed("➖ Player Left").WithDescription(name).Build());
            }
            _lastPlayers.Clear();
            foreach (var (g, n) in current) _lastPlayers[g] = n;
            _playerBaseline = true;
        }
        catch (Exception ex) { Log.Debug(ex, "Discord player notification failed."); }
        finally { _notifyGate.Release(); }
    }

    public async Task NotifyScheduledRestart(int minutesRemaining)
    {
        if (BotState != DiscordBotState.Online) return;
        try { await PostStatusAsync(Embed("🔁 Scheduled Restart").WithDescription($"Server restarting in {minutesRemaining} minute(s).").Build()); }
        catch (Exception ex) { Log.Debug(ex, "Discord scheduled-restart notification failed."); }
    }

    // ----- Status message + posting helpers -----

    public async Task UpdateStatusMessageAsync()
    {
        await _statusGate.WaitAsync().ConfigureAwait(false);
        try { await UpdateStatusMessageCoreAsync().ConfigureAwait(false); }
        finally { _statusGate.Release(); }
    }

    private async Task UpdateStatusMessageCoreAsync()
    {
        DiscordSocketClient? client;
        lock (_gate) client = _client;
        var cfg = Config;
        if (client is null || cfg.StatusChannelId == 0) return;
        if (client.GetChannel(cfg.StatusChannelId) is not IMessageChannel channel) return;

        var profile = _profiles.ActiveProfile;
        var (cpu, mem) = _server.GetResourceUsage();
        var embed = Embed($"ATLAS — {profile?.ServerName ?? "No active profile"}")
            .AddField("State", _server.CurrentState.ToString(), true)
            .AddField("Uptime", FormatUptime(_server.Uptime), true)
            .AddField("Crashes", _server.CrashCount.ToString(), true)
            .AddField("CPU", $"{cpu:0} %", true)
            .AddField("RAM", $"{mem / (1024.0 * 1024.0):0} MB", true)
            .AddField("RCON", _rcon.State.ToString(), true)
            .Build();

        try
        {
            if (cfg.StatusMessageId is { } msgId && msgId != 0 &&
                await channel.GetMessageAsync(msgId).ConfigureAwait(false) is IUserMessage existing)
            {
                await existing.ModifyAsync(m => m.Embed = embed).ConfigureAwait(false);
                return;
            }
            var posted = await channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
            cfg.StatusMessageId = posted.Id;
            await _settings.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Debug(ex, "Discord status-message update failed."); }
    }

    public async Task<bool> SendTestEmbedAsync()
    {
        DiscordSocketClient? client;
        lock (_gate) client = _client;
        if (client is null || Config.StatusChannelId == 0) return false;
        if (client.GetChannel(Config.StatusChannelId) is not IMessageChannel channel) return false;
        await channel.SendMessageAsync(embed: Embed("✅ ATLAS Test")
            .WithDescription("The Discord bot can post to this channel.").Build()).ConfigureAwait(false);
        return true;
    }

    private async Task PostStatusAsync(Embed embed)
    {
        DiscordSocketClient? client;
        lock (_gate) client = _client;
        if (client?.GetChannel(Config.StatusChannelId) is IMessageChannel ch)
            await ch.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }

    private async Task PostPlayerLogAsync(Embed embed)
    {
        DiscordSocketClient? client;
        lock (_gate) client = _client;
        var id = Config.PlayerLogChannelId ?? Config.StatusChannelId;
        if (id != 0 && client?.GetChannel(id) is IMessageChannel ch)
            await ch.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }

    // ----- Shared embed factory (reused by the command module) -----

    public static EmbedBuilder Embed(string title) => new EmbedBuilder()
        .WithColor(AtlasColor)
        .WithTitle(title)
        .WithFooter($"ATLAS v{Version} | {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");

    public static string FormatUptime(TimeSpan up) => $"{(int)up.TotalHours:00}:{up.Minutes:00}:{up.Seconds:00}";

    private void SetState(DiscordBotState s)
    {
        try { _state.OnNext(s); } catch (ObjectDisposedException) { /* disposed */ }
    }

    public void Dispose()
    {
        foreach (var s in _subs) { try { s.Dispose(); } catch { /* ignore */ } }
        _subs.Clear();
        try { _client?.Dispose(); } catch { /* ignore */ }
        try { _interactions?.Dispose(); } catch { /* ignore */ }
        try { _state.OnCompleted(); _state.Dispose(); } catch { /* ignore */ }
        _connectGate.Dispose();
        _notifyGate.Dispose();
        _statusGate.Dispose();
    }
}
