using System.Text.Json;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atlas.Pages.Scheduler;

/// <summary>
/// Edits a <see cref="ScheduledTask"/> in place (mirrors ProfileEditor). Exposes the common fields plus
/// type-specific payload fields (visibility driven by the selected type) and a live cron preview. On Save
/// the input is validated, the payload is serialized back into <see cref="ScheduledTask.PayloadJson"/>, and
/// <see cref="CloseRequested"/> fires with <c>true</c>.
/// </summary>
public partial class ScheduledTaskEditorViewModel : BaseViewModel
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ScheduledTask _task;
    private readonly ISchedulerService _scheduler;

    public event Action<bool>? CloseRequested;

    [ObservableProperty] private string _taskName;
    [ObservableProperty] private ScheduledTaskType _selectedType;
    [ObservableProperty] private string _cron;
    [ObservableProperty] private bool _isEnabledOnSave;
    [ObservableProperty] private string _validationError = string.Empty;

    // Restart payload
    [ObservableProperty] private bool _countdownEnabled = true;
    [ObservableProperty] private string _warningMessage = "Server restarting in {minutes} minute(s)!";
    [ObservableProperty] private string _minutesWarningsText = "15, 10, 5, 1";
    // Message payload
    [ObservableProperty] private string _broadcastMessage = string.Empty;
    // RconCommand payload
    [ObservableProperty] private string _rconCommandText = string.Empty;
    // Mod/Server update payload
    [ObservableProperty] private bool _autoRestartAfterUpdate = true;

    public string DialogTitle { get; }
    public Array TaskTypes { get; } = Enum.GetValues(typeof(ScheduledTaskType));

    public ScheduledTaskEditorViewModel(ScheduledTask task, bool isNew, ISchedulerService scheduler)
    {
        _task = task;
        _scheduler = scheduler;
        DialogTitle = isNew ? "New Scheduled Task" : "Edit Scheduled Task";
        Title = DialogTitle;

        _taskName = task.Name;
        _selectedType = task.TaskType;
        _cron = string.IsNullOrWhiteSpace(task.CronExpression) ? "0 4 * * *" : task.CronExpression;
        _isEnabledOnSave = task.IsEnabled;
        SeedPayload(task);
    }

    private void SeedPayload(ScheduledTask task)
    {
        switch (task.TaskType)
        {
            case ScheduledTaskType.Restart when Parse<RestartPayload>(task.PayloadJson) is { } r:
                CountdownEnabled = r.CountdownEnabled;
                WarningMessage = r.WarningMessage;
                MinutesWarningsText = string.Join(", ", r.MinutesWarnings);
                break;
            case ScheduledTaskType.Message when Parse<MessagePayload>(task.PayloadJson) is { } m:
                BroadcastMessage = m.Message;
                break;
            case ScheduledTaskType.RconCommand when Parse<RconCommandPayload>(task.PayloadJson) is { } c:
                RconCommandText = c.Command;
                break;
            case ScheduledTaskType.ModUpdate or ScheduledTaskType.ServerUpdate
                when Parse<UpdatePayload>(task.PayloadJson) is { } u:
                AutoRestartAfterUpdate = u.AutoRestartAfterUpdate;
                break;
        }
    }

    // Type-driven field visibility
    public bool IsRestart => SelectedType == ScheduledTaskType.Restart;
    public bool IsMessage => SelectedType == ScheduledTaskType.Message;
    public bool IsRconCommand => SelectedType == ScheduledTaskType.RconCommand;
    public bool IsUpdate => SelectedType is ScheduledTaskType.ModUpdate or ScheduledTaskType.ServerUpdate;

    partial void OnSelectedTypeChanged(ScheduledTaskType value)
    {
        OnPropertyChanged(nameof(IsRestart));
        OnPropertyChanged(nameof(IsMessage));
        OnPropertyChanged(nameof(IsRconCommand));
        OnPropertyChanged(nameof(IsUpdate));
    }

    public string CronPreview
    {
        get
        {
            var human = _scheduler.GetHumanReadableCron(Cron);
            var next = _scheduler.GetNextRunTime(Cron);
            return next is { } n ? $"{human} — next: {n.ToLocalTime():yyyy-MM-dd HH:mm}" : human;
        }
    }

    partial void OnCronChanged(string value) => OnPropertyChanged(nameof(CronPreview));

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(TaskName)) { ValidationError = "Name is required."; return; }
        if (_scheduler.GetNextRunTime(Cron) is null) { ValidationError = "The cron expression is invalid."; return; }

        _task.Name = TaskName.Trim();
        _task.TaskType = SelectedType;
        _task.CronExpression = Cron.Trim();
        _task.IsEnabled = IsEnabledOnSave;
        _task.PayloadJson = BuildPayloadJson();
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    private string BuildPayloadJson()
    {
        object payload = SelectedType switch
        {
            ScheduledTaskType.Restart => new RestartPayload
            {
                CountdownEnabled = CountdownEnabled,
                WarningMessage = WarningMessage,
                MinutesWarnings = ParseMinutes(MinutesWarningsText),
            },
            ScheduledTaskType.Message => new MessagePayload { Message = BroadcastMessage },
            ScheduledTaskType.RconCommand => new RconCommandPayload { Command = RconCommandText },
            _ => new UpdatePayload { AutoRestartAfterUpdate = AutoRestartAfterUpdate },
        };
        return JsonSerializer.Serialize(payload, Json);
    }

    private static List<int> ParseMinutes(string csv) => csv
        .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => int.TryParse(s, out var n) ? n : 0)
        .Where(n => n > 0)
        .Distinct()
        .OrderByDescending(n => n)
        .ToList();

    private static T? Parse<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch { return null; }
    }
}
