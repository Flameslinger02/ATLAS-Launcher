using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atlas.Pages.Scheduler;

/// <summary>How the schedule is entered. All modes emit a standard 5-field cron string under the hood
/// (stored in <see cref="ScheduledTask.CronExpression"/>); <see cref="ScheduleMode.Advanced"/> exposes that
/// string directly for power users.</summary>
public enum ScheduleMode { Daily, Weekly, Interval, Advanced }

/// <summary>
/// Edits a <see cref="ScheduledTask"/> in place. Exposes a friendly schedule builder (Daily / Weekly /
/// Every-N-hours / Advanced cron) that composes the cron expression, plus the type-specific payload fields.
/// On Save the input is validated, the payload is serialized into <see cref="ScheduledTask.PayloadJson"/>,
/// and <see cref="CloseRequested"/> fires with <c>true</c>.
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
    private bool _loading;   // suppresses cron recomposition while the ctor seeds builder fields

    public event Action<bool>? CloseRequested;

    [ObservableProperty] private string _taskName;
    [ObservableProperty] private ScheduledTaskType _selectedType;
    [ObservableProperty] private string _cron;
    [ObservableProperty] private bool _isEnabledOnSave;
    [ObservableProperty] private string _validationError = string.Empty;

    // Schedule builder
    [ObservableProperty] private ScheduleMode _scheduleMode = ScheduleMode.Daily;
    [ObservableProperty] private int _timeHour = 4;
    [ObservableProperty] private int _timeMinute;
    [ObservableProperty] private int _intervalHours = 6;
    [ObservableProperty] private bool _dayMon, _dayTue, _dayWed, _dayThu, _dayFri, _daySat, _daySun;

    // Restart / Update&Restart payload (shared countdown fields)
    [ObservableProperty] private bool _countdownEnabled = true;
    [ObservableProperty] private string _warningMessage = "Server restarting in {minutes} minute(s)!";
    [ObservableProperty] private string _minutesWarningsText = "15, 10, 5, 1";
    // Message payload
    [ObservableProperty] private string _broadcastMessage = string.Empty;
    // RconCommand payload
    [ObservableProperty] private string _rconCommandText = string.Empty;

    public string DialogTitle { get; }

    /// <summary>The task types the editor offers. The legacy ModUpdate/ServerUpdate types are intentionally
    /// excluded — they are superseded by the combined <see cref="ScheduledTaskType.UpdateRestart"/>.</summary>
    public ScheduledTaskType[] TaskTypes { get; } =
    {
        ScheduledTaskType.Restart,
        ScheduledTaskType.UpdateRestart,
        ScheduledTaskType.Message,
        ScheduledTaskType.RconCommand,
    };

    public Array ScheduleModes { get; } = Enum.GetValues(typeof(ScheduleMode));
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();
    public IReadOnlyList<int> Minutes { get; } = Enumerable.Range(0, 60).ToList();
    public IReadOnlyList<int> IntervalChoices { get; } = Enumerable.Range(1, 24).ToList();

    public ScheduledTaskEditorViewModel(ScheduledTask task, bool isNew, ISchedulerService scheduler)
    {
        _task = task;
        _scheduler = scheduler;
        DialogTitle = isNew ? "New Scheduled Task" : "Edit Scheduled Task";
        Title = DialogTitle;

        _loading = true;
        _taskName = task.Name;
        // Legacy update tasks are folded into the combined Update & Restart type.
        _selectedType = task.TaskType is ScheduledTaskType.ModUpdate or ScheduledTaskType.ServerUpdate
            ? ScheduledTaskType.UpdateRestart
            : task.TaskType;
        _cron = string.IsNullOrWhiteSpace(task.CronExpression) ? "0 4 * * *" : task.CronExpression;
        _isEnabledOnSave = task.IsEnabled;
        ParseCronIntoBuilder(_cron);
        SeedPayload(task);
        _loading = false;
    }

    private void SeedPayload(ScheduledTask task)
    {
        // Uses the (possibly coerced) SelectedType so a legacy ModUpdate/ServerUpdate row seeds the
        // Update & Restart countdown fields (from RestartPayload defaults if the stored JSON has none).
        if (SelectedType is ScheduledTaskType.Restart or ScheduledTaskType.UpdateRestart)
        {
            if (Parse<RestartPayload>(task.PayloadJson) is { } r)
            {
                CountdownEnabled = r.CountdownEnabled;
                WarningMessage = r.WarningMessage;
                MinutesWarningsText = string.Join(", ", r.MinutesWarnings);
            }
        }
        else if (SelectedType is ScheduledTaskType.Message && Parse<MessagePayload>(task.PayloadJson) is { } m)
        {
            BroadcastMessage = m.Message;
        }
        else if (SelectedType is ScheduledTaskType.RconCommand && Parse<RconCommandPayload>(task.PayloadJson) is { } c)
        {
            RconCommandText = c.Command;
        }
    }

    // ----- Type-driven field visibility -----

    public bool IsRestart => SelectedType == ScheduledTaskType.Restart;
    public bool IsUpdateRestart => SelectedType == ScheduledTaskType.UpdateRestart;
    public bool IsMessage => SelectedType == ScheduledTaskType.Message;
    public bool IsRconCommand => SelectedType == ScheduledTaskType.RconCommand;
    /// <summary>The countdown block is shared by Restart and Update &amp; Restart (both end in a restart).</summary>
    public bool ShowCountdown => IsRestart || IsUpdateRestart;

    partial void OnSelectedTypeChanged(ScheduledTaskType value)
    {
        OnPropertyChanged(nameof(IsRestart));
        OnPropertyChanged(nameof(IsUpdateRestart));
        OnPropertyChanged(nameof(IsMessage));
        OnPropertyChanged(nameof(IsRconCommand));
        OnPropertyChanged(nameof(ShowCountdown));
    }

    // ----- Schedule-mode visibility + recomposition -----

    public bool IsDaily => ScheduleMode == ScheduleMode.Daily;
    public bool IsWeekly => ScheduleMode == ScheduleMode.Weekly;
    public bool IsInterval => ScheduleMode == ScheduleMode.Interval;
    public bool IsAdvanced => ScheduleMode == ScheduleMode.Advanced;

    partial void OnScheduleModeChanged(ScheduleMode value)
    {
        OnPropertyChanged(nameof(IsDaily));
        OnPropertyChanged(nameof(IsWeekly));
        OnPropertyChanged(nameof(IsInterval));
        OnPropertyChanged(nameof(IsAdvanced));
        Recompose();
    }

    partial void OnTimeHourChanged(int value) => Recompose();
    partial void OnTimeMinuteChanged(int value) => Recompose();
    partial void OnIntervalHoursChanged(int value) => Recompose();
    partial void OnDayMonChanged(bool value) => Recompose();
    partial void OnDayTueChanged(bool value) => Recompose();
    partial void OnDayWedChanged(bool value) => Recompose();
    partial void OnDayThuChanged(bool value) => Recompose();
    partial void OnDayFriChanged(bool value) => Recompose();
    partial void OnDaySatChanged(bool value) => Recompose();
    partial void OnDaySunChanged(bool value) => Recompose();

    /// <summary>Rebuilds the cron string from the builder fields (except in Advanced mode, where the user
    /// edits the cron directly).</summary>
    private void Recompose()
    {
        if (_loading || ScheduleMode == ScheduleMode.Advanced) return;
        Cron = ScheduleMode switch
        {
            ScheduleMode.Daily => $"{TimeMinute} {TimeHour} * * *",
            ScheduleMode.Weekly => $"{TimeMinute} {TimeHour} * * {WeeklyDowCsv()}",
            ScheduleMode.Interval => $"0 */{Math.Clamp(IntervalHours, 1, 24)} * * *",
            _ => Cron,
        };
    }

    private string WeeklyDowCsv()
    {
        // cron day-of-week: Sun=0, Mon=1 … Sat=6.
        var days = new List<int>();
        if (DaySun) days.Add(0);
        if (DayMon) days.Add(1);
        if (DayTue) days.Add(2);
        if (DayWed) days.Add(3);
        if (DayThu) days.Add(4);
        if (DayFri) days.Add(5);
        if (DaySat) days.Add(6);
        return days.Count == 0 ? "*" : string.Join(",", days);
    }

    private bool AnyDaySelected =>
        DayMon || DayTue || DayWed || DayThu || DayFri || DaySat || DaySun;

    /// <summary>Best-effort reverse mapping of an existing cron into the builder fields. Anything that does
    /// not match a Daily/Weekly/Interval shape falls back to Advanced (raw cron).</summary>
    private void ParseCronIntoBuilder(string cron)
    {
        var f = cron.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5) { ScheduleMode = ScheduleMode.Advanced; return; }
        var (min, hour, dom, mon, dow) = (f[0], f[1], f[2], f[3], f[4]);

        var interval = Regex.Match(hour, @"^\*/(\d+)$");
        if (min == "0" && interval.Success && dom == "*" && mon == "*" && dow == "*"
            && int.TryParse(interval.Groups[1].Value, out var n) && n is >= 1 and <= 24)
        {
            IntervalHours = n;
            ScheduleMode = ScheduleMode.Interval;
            return;
        }

        if (int.TryParse(min, out var mm) && int.TryParse(hour, out var hh)
            && mm is >= 0 and <= 59 && hh is >= 0 and <= 23 && dom == "*" && mon == "*")
        {
            TimeHour = hh;
            TimeMinute = mm;
            if (dow == "*") { ScheduleMode = ScheduleMode.Daily; return; }

            var parts = dow.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && parts.All(p => int.TryParse(p, out var d) && d is >= 0 and <= 7))
            {
                foreach (var p in parts) SetDow(int.Parse(p));
                ScheduleMode = ScheduleMode.Weekly;
                return;
            }
        }

        ScheduleMode = ScheduleMode.Advanced;
    }

    private void SetDow(int d)
    {
        switch (d)
        {
            case 0: case 7: DaySun = true; break;   // cron accepts 0 and 7 for Sunday
            case 1: DayMon = true; break;
            case 2: DayTue = true; break;
            case 3: DayWed = true; break;
            case 4: DayThu = true; break;
            case 5: DayFri = true; break;
            case 6: DaySat = true; break;
        }
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
        if (ScheduleMode == ScheduleMode.Weekly && !AnyDaySelected)
        {
            ValidationError = "Pick at least one day of the week.";
            return;
        }
        if (_scheduler.GetNextRunTime(Cron) is null) { ValidationError = "The schedule is invalid."; return; }

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
            ScheduledTaskType.Restart or ScheduledTaskType.UpdateRestart => new RestartPayload
            {
                CountdownEnabled = CountdownEnabled,
                WarningMessage = WarningMessage,
                MinutesWarnings = ParseMinutes(MinutesWarningsText),
            },
            ScheduledTaskType.Message => new MessagePayload { Message = BroadcastMessage },
            ScheduledTaskType.RconCommand => new RconCommandPayload { Command = RconCommandText },
            _ => new RestartPayload(),
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
