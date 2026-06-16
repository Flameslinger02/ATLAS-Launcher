using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Owns scheduled-task persistence and execution. Runs as a hosted background service: a polling loop
/// fires due tasks (restart-with-countdown, broadcast, RCON command, mod/server update), records the
/// result, and recomputes the next run. Cron expressions are interpreted in local time; all stored times
/// are UTC. Fires <see cref="TasksChanged"/> after any mutation or run so view models can refresh.
/// </summary>
public interface ISchedulerService
{
    Task<List<ScheduledTask>> GetTasksForProfileAsync(int profileId);
    Task<ScheduledTask> CreateTaskAsync(ScheduledTask task);
    Task UpdateTaskAsync(ScheduledTask task);
    Task DeleteTaskAsync(int id);
    Task<ScheduledTask> CloneTaskAsync(int id);
    Task EnableTaskAsync(int id);
    Task DisableTaskAsync(int id);

    /// <summary>Runs a task immediately (off the schedule), e.g. for testing.</summary>
    Task TriggerNowAsync(int id);

    /// <summary>Skips the next scheduled run by advancing NextRunAt to the following occurrence.</summary>
    Task SkipNextAsync(int id);

    /// <summary>Next fire time (UTC) for a cron expression, or null if the expression is invalid.</summary>
    DateTime? GetNextRunTime(string cronExpression);

    /// <summary>A best-effort plain-English description of a cron expression.</summary>
    string GetHumanReadableCron(string cronExpression);

    /// <summary>Raised (off the UI thread) after any task is created/updated/deleted or finishes a run.</summary>
    event EventHandler? TasksChanged;
}
