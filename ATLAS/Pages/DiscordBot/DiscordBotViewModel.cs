using System.Collections.ObjectModel;
using System.Windows;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Atlas.Pages.DiscordBot;

/// <summary>Display option for a guild the bot is in (drives the primary-guild picker).</summary>
public sealed record GuildOption(ulong Id, string Name)
{
    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>
/// Configures the embedded Discord bot: token (entered via a PasswordBox, DPAPI-encrypted on connect),
/// channel/role IDs, notification toggles, connect/disconnect, test embed and command re-registration.
/// Singleton (subscribes to <see cref="IDiscordBotService.StateChanged"/> for the page's lifetime).
/// </summary>
public partial class DiscordBotViewModel : BaseViewModel, IDisposable
{
    private readonly IDiscordBotService _bot;
    private readonly ISettingsService _settings;
    private readonly ISecretProtector _secrets;
    private readonly IDialogService _dialogs;
    private readonly IDisposable _sub;

    [ObservableProperty] private DiscordBotState _botState;
    [ObservableProperty] private string _botStateText = "Offline";
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasSavedToken;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _statusChannelId = string.Empty;
    [ObservableProperty] private string _playerLogChannelId = string.Empty;
    [ObservableProperty] private string _consoleLogChannelId = string.Empty;
    [ObservableProperty] private string _commandChannelId = string.Empty;
    [ObservableProperty] private string _adminRoleIds = string.Empty;
    [ObservableProperty] private string _ownerRoleId = string.Empty;
    [ObservableProperty] private bool _allowDMs;
    [ObservableProperty] private bool _notifyPlayerJoin = true;
    [ObservableProperty] private bool _notifyPlayerLeave = true;
    [ObservableProperty] private bool _notifyCrash = true;
    [ObservableProperty] private bool _mirrorConsoleLog;
    [ObservableProperty] private GuildOption? _selectedGuild;

    public ObservableCollection<GuildOption> Guilds { get; } = new();

    public bool IsConnected => BotState == DiscordBotState.Online;

    public DiscordBotViewModel(IDiscordBotService bot, ISettingsService settings, ISecretProtector secrets, IDialogService dialogs)
    {
        _bot = bot;
        _settings = settings;
        _secrets = secrets;
        _dialogs = dialogs;
        Title = "Discord Bot";

        SeedFromConfig();
        ApplyState(_bot.BotState);
        _sub = _bot.StateChanged.Subscribe(s => OnUi(() => ApplyState(s)));
    }

    private void SeedFromConfig()
    {
        var c = _settings.Settings.DiscordBot;
        IsEnabled = c.IsEnabled;
        HasSavedToken = !string.IsNullOrWhiteSpace(c.TokenEncrypted);
        StatusChannelId = c.StatusChannelId == 0 ? string.Empty : c.StatusChannelId.ToString();
        PlayerLogChannelId = c.PlayerLogChannelId?.ToString() ?? string.Empty;
        ConsoleLogChannelId = c.ConsoleLogChannelId?.ToString() ?? string.Empty;
        CommandChannelId = c.CommandChannelId?.ToString() ?? string.Empty;
        AdminRoleIds = c.AdminRoleIds is { Length: > 0 } a ? string.Join(", ", a) : string.Empty;
        OwnerRoleId = c.OwnerRoleId?.ToString() ?? string.Empty;
        AllowDMs = c.AllowDMs;
        NotifyPlayerJoin = c.NotifyPlayerJoin;
        NotifyPlayerLeave = c.NotifyPlayerLeave;
        NotifyCrash = c.NotifyCrash;
        MirrorConsoleLog = c.MirrorConsoleLog;
    }

    private void ApplyState(DiscordBotState s)
    {
        BotState = s;
        BotStateText = s.ToString();
        OnPropertyChanged(nameof(IsConnected));
        DisconnectCommand.NotifyCanExecuteChanged();
        SendTestEmbedCommand.NotifyCanExecuteChanged();
        ReRegisterCommandsCommand.NotifyCanExecuteChanged();
        RefreshGuildsCommand.NotifyCanExecuteChanged();
        if (s == DiscordBotState.Online) _ = RefreshGuildsAsync();
    }

    /// <summary>Called from the code-behind with the PasswordBox value (token never lives in a bound property).</summary>
    public async Task ConnectAsync(string enteredToken)
    {
        ApplyToConfig();
        var c = _settings.Settings.DiscordBot;

        var token = !string.IsNullOrWhiteSpace(enteredToken) ? enteredToken.Trim() : _secrets.Decrypt(c.TokenEncrypted);
        if (string.IsNullOrWhiteSpace(token))
        {
            await _dialogs.ShowErrorAsync("Discord", "Enter the bot token to connect.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(enteredToken))
        {
            c.TokenEncrypted = _secrets.Encrypt(enteredToken.Trim());   // DPAPI; never stored plaintext
            HasSavedToken = true;
        }
        c.IsEnabled = true;
        IsEnabled = true;
        await _settings.SaveAsync();

        try { StatusMessage = "Connecting..."; await _bot.StartBotAsync(token); StatusMessage = "Connect requested."; }
        catch (Exception ex) { Log.Error(ex, "Discord connect failed."); await _dialogs.ShowErrorAsync("Discord", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task Disconnect()
    {
        var c = _settings.Settings.DiscordBot;
        c.IsEnabled = false;
        IsEnabled = false;
        await _settings.SaveAsync();
        await _bot.StopBotAsync();
        StatusMessage = "Disconnected.";
    }

    [RelayCommand]
    private async Task SaveConfig()
    {
        ApplyToConfig();
        await _settings.SaveAsync();
        StatusMessage = "Configuration saved.";
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task SendTestEmbed()
    {
        var ok = await _bot.SendTestEmbedAsync();
        StatusMessage = ok ? "Test embed sent." : "Could not send (check the status channel ID).";
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ReRegisterCommands()
    {
        try { await _bot.ReRegisterCommandsAsync(); StatusMessage = "Slash commands re-registered."; }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Discord", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task RefreshGuilds() => RefreshGuildsAsync();

    private async Task RefreshGuildsAsync()
    {
        try
        {
            var guilds = await _bot.GetGuildsAsync();
            var currentPrimary = _settings.Settings.DiscordBot.PrimaryGuildId;
            Guilds.Clear();
            foreach (var (id, name) in guilds) Guilds.Add(new GuildOption(id, name));
            // Only reflect the saved primary guild; never silently fall back to an arbitrary guild (that would
            // change slash-command registration scope behind the operator's back).
            SelectedGuild = Guilds.FirstOrDefault(g => g.Id == currentPrimary);
        }
        catch (Exception ex) { Log.Debug(ex, "Refresh guilds failed."); }
    }

    partial void OnSelectedGuildChanged(GuildOption? value)
    {
        if (value is not null)
        {
            _settings.Settings.DiscordBot.PrimaryGuildId = value.Id;
            _ = _settings.SaveAsync();   // persist the explicit pick immediately
        }
    }

    /// <summary>Writes the editable fields back into the persisted config (token is handled separately).</summary>
    private void ApplyToConfig()
    {
        var c = _settings.Settings.DiscordBot;
        c.StatusChannelId = ParseId(StatusChannelId) ?? 0;
        c.PlayerLogChannelId = ParseId(PlayerLogChannelId);
        c.ConsoleLogChannelId = ParseId(ConsoleLogChannelId);
        c.CommandChannelId = ParseId(CommandChannelId);
        c.AdminRoleIds = ParseIdList(AdminRoleIds);
        c.OwnerRoleId = ParseId(OwnerRoleId);
        c.AllowDMs = AllowDMs;
        c.NotifyPlayerJoin = NotifyPlayerJoin;
        c.NotifyPlayerLeave = NotifyPlayerLeave;
        c.NotifyCrash = NotifyCrash;
        c.MirrorConsoleLog = MirrorConsoleLog;
        if (SelectedGuild is not null) c.PrimaryGuildId = SelectedGuild.Id;
    }

    private static ulong? ParseId(string s) => ulong.TryParse(s?.Trim(), out var v) && v != 0 ? v : null;

    private static ulong[]? ParseIdList(string s)
    {
        var ids = (s ?? string.Empty)
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => ulong.TryParse(x, out var v) ? v : 0).Where(v => v != 0).Distinct().ToArray();
        return ids.Length > 0 ? ids : null;
    }

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }

    public void Dispose()
    {
        try { _sub.Dispose(); } catch { /* ignore */ }
    }
}
