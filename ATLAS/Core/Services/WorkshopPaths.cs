namespace Atlas.Core.Services;

/// <summary>
/// Helpers for locating Steam Workshop mod folders. SteamCMD always installs Workshop items under
/// <c>&lt;root&gt;\steamapps\workshop\content\107410\&lt;id&gt;</c>, so ATLAS treats the configured mod
/// directory as that <i>root</i>. To forgive a user who points the directory deeper — at <c>steamapps</c>,
/// <c>…\workshop</c>, or <c>…\content\107410</c> (e.g. their existing Steam library) — these helpers
/// normalize the path back to the root (so SteamCMD doesn't nest a second <c>steamapps\workshop</c> under it)
/// and resolve a mod's actual on-disk folder.
/// </summary>
public static class WorkshopPaths
{
    private const string AppId = AppConstants.Arma3WorkshopAppId; // "107410"

    /// <summary>
    /// Normalizes the configured directory to the SteamCMD install root by stripping a trailing
    /// <c>steamapps[\workshop[\content[\107410]]]</c> segment if present. Pointing SteamCMD at the root
    /// makes it reuse existing Workshop content rather than create a duplicate nested tree.
    /// </summary>
    public static string ResolveSteamCmdRoot(string? configuredDir)
    {
        var dir = (configuredDir ?? string.Empty).TrimEnd('\\', '/');
        if (dir.Length == 0) return dir;

        // Longest-first so the deepest match wins.
        string[] suffixes =
        {
            Path.Combine("steamapps", "workshop", "content", AppId),
            Path.Combine("steamapps", "workshop", "content"),
            Path.Combine("steamapps", "workshop"),
            "steamapps",
        };
        foreach (var suffix in suffixes)
        {
            if (dir.EndsWith(Path.DirectorySeparatorChar + suffix, StringComparison.OrdinalIgnoreCase) ||
                dir.EndsWith(Path.AltDirectorySeparatorChar + suffix, StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = dir[..^suffix.Length].TrimEnd('\\', '/');
                if (trimmed.Length > 0) dir = trimmed;
                break;
            }
        }
        return dir;
    }

    /// <summary>The canonical Workshop content folder for a mod under a (resolved) Steam root.</summary>
    public static string ContentFolder(string steamCmdRoot, ulong workshopId) =>
        Path.Combine(steamCmdRoot, "steamapps", "workshop", "content", AppId, workshopId.ToString());

    /// <summary>
    /// Best-effort resolve of a mod's on-disk source folder, tolerating where the configured directory points.
    /// Returns the first candidate that exists on disk, otherwise the canonical path (for display/messages).
    /// </summary>
    public static string ResolveModFolder(string? configuredDir, ulong workshopId)
    {
        var dir = (configuredDir ?? string.Empty).TrimEnd('\\', '/');
        var id = workshopId.ToString();
        var candidates = new[]
        {
            ContentFolder(ResolveSteamCmdRoot(dir), workshopId),     // configured = root (canonical)
            Path.Combine(dir, id),                                   // configured = …\content\107410
            Path.Combine(dir, "content", AppId, id),                 // configured = …\workshop
            Path.Combine(dir, "workshop", "content", AppId, id),     // configured = …\steamapps
        };
        foreach (var c in candidates)
            if (Directory.Exists(c)) return c;
        return candidates[0];
    }
}
