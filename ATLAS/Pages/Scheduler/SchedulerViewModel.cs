using System.Collections.ObjectModel;
using System.Windows;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Atlas.Pages.Scheduler;

/// <summary>
/// CRUD over the active profile's scheduled tasks, plus enable/disable and run-now. Singleton (subscribes
/// to <see cref="ISchedulerService.TasksChanged"/> for live Next/Last-run updates; mirrors the other
/// live pages' no-leak rule). Times are shown in local time.
/// </summary>
public partial class SchedulerViewModel : BaseViewModel, IDisposable
{
    private readonly ISchedulerService _scheduler;
    private readonly IProfileService _profiles;
    private readonly IScheduledTaskEditorLauncher _editor;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private ScheduledTaskRow? _selectedTask;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<ScheduledTaskRow> Tasks { get; } = new();

    public bool HasActiveProfile => _profiles.ActiveProfile is not null;
    public bool HasSelection => SelectedTask is not null;

    public SchedulerViewModel(ISchedulerService scheduler, IProfileService profiles,
        IScheduledTaskEditorLauncher editor, IDialogService dialogs)
    {
        _scheduler = scheduler;
        _profiles = profiles;
        _editor = editor;
        _dialogs = dialogs;
        Title = "Scheduler";

        _scheduler.TasksChanged += OnTasksChanged;
        _profiles.ActiveProfileChanged += OnActiveProfileChanged;
        _ = ReloadAsync();
    }

    private void OnTasksChanged(object? sender, EventArgs e) => OnUi(() => _ = ReloadAsync());

    private void OnActiveProfileChanged(object? sender, ServerProfile profile) => OnUi(() =>
    {
        OnPropertyChanged(nameof(HasActiveProfile));
        AddCommand.NotifyCanExecuteChanged();
        _ = ReloadAsync();
    });

    private async Task ReloadAsync()
    {
        var p = _profiles.ActiveProfile;
        if (p is null) { Tasks.Clear(); return; }
        try
        {
            var tasks = await _scheduler.GetTasksForProfileAsync(p.Id);
            var selectedId = SelectedTask?.Id;
            Tasks.Clear();
            foreach (var t in tasks)
                Tasks.Add(new ScheduledTaskRow(t, _scheduler.GetHumanReadableCron(t.CronExpression)));
            if (selectedId is int id) SelectedTask = Tasks.FirstOrDefault(r => r.Id == id);
        }
        catch (Exception ex) { Log.Error(ex, "Failed to load scheduled tasks."); }
    }

    partial void OnSelectedTaskChanged(ScheduledTaskRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        EditCommand.NotifyCanExecuteChanged();
        CloneCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RunNowCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
    }

    // ----- Commands -----

    [RelayCommand(CanExecute = nameof(HasActiveProfile))]
    private async Task Add()
    {
        var p = _profiles.ActiveProfile;
        if (p is null) return;
        var task = new ScheduledTask
        {
            ProfileId = p.Id,
            Name = "New Task",
            TaskType = ScheduledTaskType.Restart,
            CronExpression = "0 4 * * *",
            IsEnabled = true,
            PayloadJson = "{}",
        };
        if (await _editor.EditAsync(task, isNew: true))
        {
            await _scheduler.CreateTaskAsync(task);
            StatusMessage = $"Created '{task.Name}'.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Edit()
    {
        if (SelectedTask is null) return;
        var copy = Copy(SelectedTask.Task);
        if (await _editor.EditAsync(copy, isNew: false))
        {
            await _scheduler.UpdateTaskAsync(copy);
            StatusMessage = $"Updated '{copy.Name}'.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Clone()
    {
        if (SelectedTask is null) return;
        await _scheduler.CloneTaskAsync(SelectedTask.Id);
        StatusMessage = "Task cloned (disabled).";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Delete()
    {
        if (SelectedTask is null) return;
        if (!await _dialogs.ConfirmAsync("Delete task", $"Delete scheduled task '{SelectedTask.Name}'?")) return;
        await _scheduler.DeleteTaskAsync(SelectedTask.Id);
        StatusMessage = "Task deleted.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RunNow()
    {
        if (SelectedTask is null) return;
        await _scheduler.TriggerNowAsync(SelectedTask.Id);
        StatusMessage = $"Triggered '{SelectedTask.Name}'.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleEnabled()
    {
        if (SelectedTask is null) return;
        if (SelectedTask.IsEnabled) await _scheduler.DisableTaskAsync(SelectedTask.Id);
        else await _scheduler.EnableTaskAsync(SelectedTask.Id);
    }

    [RelayCommand]
    private Task Refresh() => ReloadAsync();

    private static ScheduledTask Copy(ScheduledTask s) => new()
    {
        Id = s.Id,
        ProfileId = s.ProfileId,
        Name = s.Name,
        TaskType = s.TaskType,
        CronExpression = s.CronExpression,
        NextRunAt = s.NextRunAt,
        LastRunAt = s.LastRunAt,
        LastRunResult = s.LastRunResult,
        IsEnabled = s.IsEnabled,
        PayloadJson = s.PayloadJson,
    };

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }

    public void Dispose()
    {
        _scheduler.TasksChanged -= OnTasksChanged;
        _profiles.ActiveProfileChanged -= OnActiveProfileChanged;
    }
}
