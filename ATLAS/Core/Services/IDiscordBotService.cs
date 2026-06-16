using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>Connection state of the embedded Discord bot.</summary>
public enum DiscordBotState { Offline, Connecting, Online, Error }

/// <summary>
/// The embedded Discord bot (Discord.Net). Runs as an <see cref="Microsoft.Extensions.Hosting.IHostedService"/>:
/// auto-connects on startup when enabled, registers ATLAS slash commands, posts status/notification embeds,
/// and serves admin commands. Server start/stop/crash and player join/leave notifications are driven
/// internally by subscriptions to <c>IServerProcessService.StateChanged</c> and
/// <c>IBattlEyeRconClient.PlayersUpdated</c> (so there is no circular dependency on those services).
/// The bot token is DPAPI-encrypted in <c>AppSettings.DiscordBot.TokenEncrypted</c> and never stored plaintext.
/// </summary>
public interface IDiscordBotService
{
    DiscordBotState BotState { get; }
    IObservable<DiscordBotState> StateChanged { get; }

    /// <summary>Connects and logs in with <paramref name="token"/> (named *Bot* to avoid clashing with IHostedService.StartAsync).</summary>
    Task StartBotAsync(string token, CancellationToken ct = default);

    /// <summary>Disconnects the bot (no-op if offline).</summary>
    Task StopBotAsync();

    /// <summary>The guilds the connected bot is a member of (id, name); empty when offline.</summary>
    Task<IReadOnlyList<(ulong Id, string Name)>> GetGuildsAsync();

    /// <summary>(Re)registers the ATLAS slash commands with the configured primary guild (instant) or globally.</summary>
    Task ReRegisterCommandsAsync();

    /// <summary>Posts a test embed to the configured status channel. Returns false if not possible.</summary>
    Task<bool> SendTestEmbedAsync();

    /// <summary>Edits (or creates) the pinned status embed in the status channel.</summary>
    Task UpdateStatusMessageAsync();

    /// <summary>Posts a scheduled-restart warning embed (called by the scheduler when configured).</summary>
    Task NotifyScheduledRestart(int minutesRemaining);
}
