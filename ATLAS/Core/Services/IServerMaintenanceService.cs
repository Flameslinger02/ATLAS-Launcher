using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// One-shot "maintenance restart" that combines a server update, a Workshop-mod update and a restart —
/// the shared implementation behind both the scheduler's Update &amp; Restart task and the manual
/// "Update &amp; Restart Now" button on the Updates page.
/// </summary>
public interface IServerMaintenanceService
{
    /// <summary>
    /// Stops the server if it is running, updates the Arma 3 dedicated server (SteamCMD) and the profile's
    /// Workshop mods, then (re)launches the server (which re-writes configs and re-deploys mods). Streams
    /// step/SteamCMD output through <paramref name="progress"/> and returns a short human-readable summary.
    /// </summary>
    Task<string> UpdateAndRestartAsync(
        ServerProfile profile, IProgress<string>? progress = null, CancellationToken ct = default);
}
