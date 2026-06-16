namespace Atlas.Core.Models;

/// <summary>The kind of action a <see cref="ScheduledTask"/> performs when it fires.</summary>
public enum ScheduledTaskType
{
    Restart,
    Message,
    RconCommand,
    ModUpdate,
    ServerUpdate
}

/// <summary>
/// A cron-scheduled action for a profile. Maps 1:1 to the <c>ScheduledTasks</c> table. Type-specific
/// settings live in <see cref="PayloadJson"/> (EAV) — see the <c>*Payload</c> records. All times are UTC.
/// </summary>
public class ScheduledTask
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ScheduledTaskType TaskType { get; set; }
    public string CronExpression { get; set; } = string.Empty;
    public DateTime? NextRunAt { get; set; }   // UTC
    public DateTime? LastRunAt { get; set; }   // UTC
    public string LastRunResult { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string PayloadJson { get; set; } = "{}";
}

// ----- Payloads (System.Text.Json, camelCase; see master Phase 11 payload spec) -----

/// <summary>Restart task: optional pre-restart RCON countdown warnings.</summary>
public sealed class RestartPayload
{
    public bool CountdownEnabled { get; set; } = true;
    public string WarningMessage { get; set; } = "Server restarting in {minutes} minute(s)!";
    public List<int> MinutesWarnings { get; set; } = new() { 15, 10, 5, 1 };
}

/// <summary>Message task: a global RCON broadcast.</summary>
public sealed class MessagePayload
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>RconCommand task: an arbitrary RCON command (e.g. <c>#lock</c>).</summary>
public sealed class RconCommandPayload
{
    public string Command { get; set; } = string.Empty;
}

/// <summary>Shared payload for ModUpdate / ServerUpdate tasks.</summary>
public sealed class UpdatePayload
{
    public bool AutoRestartAfterUpdate { get; set; } = true;
}
