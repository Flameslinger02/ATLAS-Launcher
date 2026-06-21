using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Checks the project's GitHub releases for a newer version (Octokit) and opens release pages.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Queries the latest GitHub release for the configured owner/repo and compares its tag to the
    /// running assembly version. Never throws — failures are reported via
    /// <see cref="UpdateCheckResult.Error"/> with <see cref="UpdateCheckResult.UpdateAvailable"/> false.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>Opens a release URL in the user's default browser.</summary>
    void OpenReleasePage(string url);

    /// <summary>
    /// Downloads the release's <c>ATLAS.exe</c> asset (<see cref="UpdateCheckResult.AssetDownloadUrl"/>) to a
    /// temp file, reporting 0..1 progress, and returns the downloaded path. Verifies the byte length against
    /// <see cref="UpdateCheckResult.AssetSize"/> when known. Throws when there is no asset or the download fails.
    /// </summary>
    Task<string> DownloadUpdateAsync(UpdateCheckResult result, IProgress<double>? progress, CancellationToken ct = default);

    /// <summary>
    /// Applies a previously downloaded update: launches a detached helper that waits for this process to exit,
    /// swaps the new exe over the running one (<see cref="System.Environment.ProcessPath"/>) and relaunches
    /// ATLAS, then requests application shutdown. A running single-file exe cannot overwrite itself, hence the helper.
    /// </summary>
    void ApplyUpdateAndRestart(string downloadedExePath);
}
