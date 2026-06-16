using System.Diagnostics;

namespace Atlas.Core.Models;

/// <summary>
/// Runtime state of a single headless-client process managed by <c>IHeadlessClientService</c>.
/// A plain POCO (no change notification): the service mutates its fields under its lock and signals the
/// UI via the marshalled <c>IHeadlessClientService.InstanceChanged</c> stream, which view models observe
/// on the dispatcher and reflect by rebuilding their bound collection. This avoids raising binding
/// notifications from the background process-exit threads.
/// </summary>
public class HeadlessClientInstance
{
    /// <summary>Zero-based index; also the <c>-name=HC{Index}</c> Arma profile name suffix.</summary>
    public int Index { get; init; }

    public string Name { get; init; } = string.Empty;     // "HC0", "HC1", ...
    public ServerState State { get; set; } = ServerState.Stopped;
    public DateTime? StartedAt { get; set; }
    public int CrashCount { get; set; }

    /// <summary>The live OS process, or null when stopped.</summary>
    public Process? Process { get; set; }
}
