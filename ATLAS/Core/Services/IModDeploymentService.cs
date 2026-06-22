using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Deploys a profile's enabled mods into its server directory using NTFS junctions (with symlink/copy
/// fallbacks), syncs <c>.bikey</c> files into the server's <c>Keys</c> folder, removes stale deployed
/// links and reports duplicate-key conflicts.
/// </summary>
public interface IModDeploymentService
{
    /// <summary>Links/copies every server-enabled mod into the profile's server directory, then syncs keys.</summary>
    Task DeployModsAsync(ServerProfile profile, IProgress<string> progress, CancellationToken ct);

    /// <summary>Removes deployed <c>@*</c> links in the server directory that are no longer enabled,
    /// reporting what it scanned, skipped and removed via <paramref name="progress"/>.</summary>
    Task CleanupStaleDeployedModsAsync(ServerProfile profile, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Copies each enabled mod's <c>.bikey</c> files into the server's <c>Keys</c> folder.</summary>
    Task CopyModKeysAsync(ServerProfile profile, IProgress<string> progress, CancellationToken ct = default);

    /// <summary>Returns the set of <c>.bikey</c> file names shipped by more than one enabled mod.</summary>
    Task<List<KeyConflict>> CheckKeyConflictsAsync(ServerProfile profile, CancellationToken ct = default);
}
