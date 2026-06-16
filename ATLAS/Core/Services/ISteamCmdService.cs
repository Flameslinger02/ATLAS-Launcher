using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Wraps the SteamCMD CLI: bootstrap/installation, dedicated-server updates, Workshop mod downloads,
/// staleness checks and Steam Web API metadata lookups. Long-running operations stream their SteamCMD
/// stdout through an <see cref="IProgress{T}"/> and support cancellation.
/// </summary>
/// <remarks>
/// SECURITY: the <see cref="IProgress{T}"/> string streams carry raw SteamCMD stdout. The implementation
/// redacts a bare typed Steam Guard code defensively, but callers must still NOT persist the progress
/// stream verbatim to durable logs without their own redaction. No Steam password is ever stored, passed
/// to SteamCMD, or logged (FASTER-style cached-token model — username only).
/// </remarks>
public interface ISteamCmdService
{
    /// <summary>True when a usable <c>steamcmd.exe</c> exists at the resolved path.</summary>
    Task<bool> IsSteamCmdAvailableAsync();

    /// <summary>Resolves the full path to <c>steamcmd.exe</c> (configured path if valid, else the default install dir).</summary>
    Task<string> GetSteamCmdPathAsync();

    /// <summary>Downloads and extracts SteamCMD into the app's SteamCMD directory, then persists its path to settings.</summary>
    Task DownloadSteamCmdAsync(IProgress<(string message, int percent)> progress, CancellationToken ct);

    /// <summary>Installs/updates the Arma 3 dedicated server (app 233780) into <paramref name="installPath"/>.</summary>
    Task UpdateServerAsync(string installPath, bool profilingBranch, IProgress<string> progress, CancellationToken ct,
        Func<CancellationToken, Task<string?>>? steamGuardProvider = null);

    /// <summary>Downloads/updates a single Workshop item (app 107410) into <paramref name="stagingPath"/>.</summary>
    Task UpdateModAsync(ulong workshopId, string stagingPath, string steamLogin, IProgress<string> progress,
        CancellationToken ct, Func<CancellationToken, Task<string?>>? steamGuardProvider = null);

    /// <summary>Downloads/updates many Workshop items in a single SteamCMD session (one login, one client spin-up).</summary>
    Task UpdateModsAsync(IEnumerable<ulong> workshopIds, string stagingPath, string steamLogin,
        IProgress<string> progress, CancellationToken ct,
        Func<CancellationToken, Task<string?>>? steamGuardProvider = null);

    /// <summary>
    /// Returns true when the locally stored mod is at least as new as the Workshop item. Returns true
    /// (cannot determine ⇒ assume up to date) when metadata cannot be fetched.
    /// </summary>
    Task<bool> IsModUpToDateAsync(ArmaModEntry mod, string stagingPath);

    /// <summary>The remembered Steam username, or null/empty when none is stored.</summary>
    string? GetSavedUsername();

    /// <summary>Persists the Steam username to settings.</summary>
    void SaveUsername(string username);

    /// <summary>Clears the saved username and best-effort removes SteamCMD's cached login tokens.</summary>
    void ClearSavedCredentials();

    /// <summary>Fetches Workshop metadata for an item. Returns null on any failure (no API key required).</summary>
    Task<WorkshopModInfo?> GetWorkshopModInfoAsync(ulong workshopId, CancellationToken ct = default);

    /// <summary>
    /// Launches <c>steamcmd +quit</c> to confirm the configured executable runs. Returns a success flag and
    /// a human-readable message. Never throws.
    /// </summary>
    Task<(bool ok, string message)> TestSteamCmdAsync(CancellationToken ct = default);

    /// <summary>
    /// Validates a Steam Web API key against the public API. Returns true when Steam accepts the key.
    /// Never throws; returns false on any error (including network failure).
    /// </summary>
    Task<bool> TestApiKeyAsync(string apiKey, CancellationToken ct = default);
}
