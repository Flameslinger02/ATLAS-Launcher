using System.Windows;
using Atlas.Core.Models;
using Atlas.Core.Services;

namespace Atlas.Pages.Scheduler;

/// <summary>
/// Opens the modal scheduled-task editor. Abstracted so the view model does not create windows directly
/// (mirrors <c>IProfileEditorLauncher</c>; the generic dialog host is not available until Phase 15).
/// </summary>
public interface IScheduledTaskEditorLauncher
{
    /// <summary>Edits <paramref name="task"/> in place. Returns true if the user saved.</summary>
    Task<bool> EditAsync(ScheduledTask task, bool isNew);
}

/// <inheritdoc cref="IScheduledTaskEditorLauncher"/>
public sealed class ScheduledTaskEditorLauncher : IScheduledTaskEditorLauncher
{
    private readonly ISchedulerService _scheduler;

    public ScheduledTaskEditorLauncher(ISchedulerService scheduler) => _scheduler = scheduler;

    public Task<bool> EditAsync(ScheduledTask task, bool isNew)
    {
        var app = Application.Current;
        if (app is null) return Task.FromResult(false);

        return app.Dispatcher.InvokeAsync(() =>
        {
            var vm = new ScheduledTaskEditorViewModel(task, isNew, _scheduler);
            var dialog = new ScheduledTaskEditorDialog(vm)
            {
                Owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow
            };
            return dialog.ShowDialog() == true;
        }).Task;
    }
}
