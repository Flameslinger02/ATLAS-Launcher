using Atlas.Core.Models;

namespace Atlas.Pages.Scheduler;

/// <summary>Read-only display wrapper for a <see cref="ScheduledTask"/> in the scheduler grid (UTC → local).</summary>
public sealed class ScheduledTaskRow
{
    public ScheduledTask Task { get; }
    public string HumanCron { get; }

    public ScheduledTaskRow(ScheduledTask task, string humanCron)
    {
        Task = task;
        HumanCron = humanCron;
    }

    public int Id => Task.Id;
    public string Name => Task.Name;
    public string TypeText => Task.TaskType switch
    {
        ScheduledTaskType.UpdateRestart => "Update & Restart",
        ScheduledTaskType.ModUpdate => "Update & Restart",     // legacy rows now run the combined action
        ScheduledTaskType.ServerUpdate => "Update & Restart",  // "
        ScheduledTaskType.RconCommand => "RCON Command",
        _ => Task.TaskType.ToString(),
    };
    public string Cron => Task.CronExpression;
    public string NextRunText => Task.NextRunAt is { } n ? n.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
    public string LastRunText => Task.LastRunAt is { } l ? l.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
    public string LastRunResult => Task.LastRunResult;
    public bool IsEnabled => Task.IsEnabled;
    public string EnabledText => Task.IsEnabled ? "Enabled" : "Disabled";
}
