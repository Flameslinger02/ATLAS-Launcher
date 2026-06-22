using System.Text.RegularExpressions;
using Microsoft.Win32;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IArmaInstallLocator"/>
public sealed class ArmaInstallLocator : IArmaInstallLocator
{
    // Steam "common" subfolders that may hold a dedicated-server exe, best match first.
    private static readonly string[] CandidateFolders = { "Arma 3 Server", "Arma 3" };
    private static readonly string[] ServerExeNames = { "arma3server_x64.exe", "arma3server.exe" };
    // The Arma 3 GAME client executables. Their presence marks a folder as the game install (app 107410),
    // where a dedicated-server (app 233780) update must NEVER run — SteamCMD would install a different app
    // over the game and corrupt it (Steam then re-validates the whole game).
    private static readonly string[] GameClientExeNames = { "arma3_x64.exe", "arma3.exe" };

    public string? FindServerDirectory()
    {
        try
        {
            foreach (var lib in SteamLibraries())
                foreach (var folder in CandidateFolders)
                {
                    var dir = Path.Combine(lib, "steamapps", "common", folder);
                    if (FindServerExecutable(dir) is not null) return dir;
                }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Arma install auto-detect failed.");
        }
        return null;
    }

    /// <summary>Returns the full path to a server exe inside <paramref name="directory"/>, or null if none exists.</summary>
    public static string? FindServerExecutable(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        foreach (var name in ServerExeNames)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>True if <paramref name="directory"/> is the Arma 3 GAME install (contains a game client exe).
    /// Updating the dedicated server (app 233780) into such a folder overwrites and corrupts the game, so the
    /// updater and scheduler must refuse it. The game folder ships the server exe too, so a bare server-exe
    /// check (see <see cref="FindServerExecutable"/>) is not enough to tell a server-only install apart.</summary>
    public static bool LooksLikeGameInstall(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        foreach (var name in GameClientExeNames)
            if (File.Exists(Path.Combine(directory, name))) return true;
        return false;
    }

    /// <summary>All Steam library roots: the base install plus every entry in libraryfolders.vdf.</summary>
    private static IEnumerable<string> SteamLibraries()
    {
        var steam = SteamInstallPath();
        if (steam is null) return Array.Empty<string>();

        var libraries = new List<string> { steam };  // the base install is itself a library

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                var text = File.ReadAllText(vdf);
                // Each library is listed as:  "path"   "D:\\SteamLibrary"
                foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
                {
                    var path = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (!string.IsNullOrWhiteSpace(path)) libraries.Add(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not parse Steam libraryfolders.vdf.");
            }
        }

        return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? SteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && !string.IsNullOrWhiteSpace(p))
            {
                var normalized = p.Replace('/', '\\');
                if (Directory.Exists(normalized)) return normalized;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the Steam path from the registry.");
        }

        // Fallback: common default install locations.
        foreach (var envVar in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            var root = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(root)) continue;
            var guess = Path.Combine(root, "Steam");
            if (Directory.Exists(guess)) return guess;
        }
        return null;
    }
}
