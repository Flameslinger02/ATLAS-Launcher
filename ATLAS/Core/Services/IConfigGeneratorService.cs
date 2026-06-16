using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// The single source of truth for Arma 3 configuration-file and launch-argument generation.
/// Produces <c>server.cfg</c>, <c>basic.cfg</c>, the BattlEye <c>beserver_x64.cfg</c> and the
/// canonical command-line arguments from a <see cref="ServerProfile"/>, and can write them all to disk.
/// </summary>
public interface IConfigGeneratorService
{
    /// <summary>Builds a complete, commented <c>server.cfg</c> from the profile.</summary>
    string GenerateServerCfg(ServerProfile profile);

    /// <summary>Builds <c>basic.cfg</c> (network performance tuning) from the profile.</summary>
    string GenerateBasicCfg(ServerProfile profile);

    /// <summary>Builds the BattlEye <c>beserver_x64.cfg</c> (RConPassword / RConPort) from the profile.</summary>
    string GenerateBeServerCfg(ServerProfile profile);

    /// <summary>
    /// Builds the canonical command-line launch arguments (executable + every flag + the resolved
    /// mod list) from the profile. This replaces any ad-hoc argument building elsewhere in the app.
    /// </summary>
    string BuildLaunchArguments(ServerProfile profile);

    /// <summary>
    /// Writes <c>server.cfg</c> and <c>basic.cfg</c> to the profile's server directory and
    /// <c>beserver_x64.cfg</c> to its <c>BattlEye\</c> subdirectory (created if missing).
    /// Throws <see cref="InvalidOperationException"/> if the server directory is not set.
    /// </summary>
    Task WriteAllConfigFilesAsync(ServerProfile profile, CancellationToken ct = default);
}
