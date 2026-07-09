using System.IO;
using System.Text.RegularExpressions;

namespace Atlas.Core.Services;

public enum RptSeverity { Critical, Warning, Info }

/// <summary>A known RPT log pattern with an explanation and admin guidance.</summary>
public sealed record RptRule(
    string Title, string Category, RptSeverity Severity, Regex Pattern, string Guidance);

/// <summary>All occurrences of one rule in an RPT file (first few sample lines kept).</summary>
public sealed class RptFinding
{
    public required RptRule Rule { get; init; }
    public int Count { get; set; }
    public List<string> Samples { get; } = new();   // "line 123: <text>"
}

public sealed record RptAnalysis(string FilePath, int TotalLines, IReadOnlyList<RptFinding> Findings);

/// <summary>Scans an Arma 3 server RPT for known error/noise patterns and groups them with guidance.</summary>
public interface IRptAnalyzerService
{
    Task<RptAnalysis> AnalyzeAsync(string rptPath, CancellationToken ct = default);

    /// <summary>The newest .rpt under a server's ATLAS-managed profiles dir, or null.</summary>
    string? FindNewestRpt(string serverDirectory);
}

/// <inheritdoc cref="IRptAnalyzerService"/>
public sealed class RptAnalyzerService : IRptAnalyzerService
{
    private const int MaxSamplesPerRule = 5;

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Built-in detection rules, roughly ordered by severity.</summary>
    private static readonly RptRule[] Rules =
    {
        // --- Critical: broken content / crashes ---
        new("Missing required addon", "Mods & Addons", RptSeverity.Critical,
            Rx(@"requires addon|Missing addons detected|You cannot play/edit this mission"),
            "The mission or a mod depends on an addon that is not loaded. Add the missing mod to the profile (check the mission's Dependencies list on the Missions tab) or remove the dependent content."),
        new("Mission failed to load", "Missions", RptSeverity.Critical,
            Rx(@"Cannot load mission|Missing description\.ext|Invalid mission format"),
            "The mission file is missing, corrupt, or incompatible. Re-export the mission or check the template name in server.cfg matches the file in MPMissions."),
        new("Crash indicator", "Stability", RptSeverity.Critical,
            Rx(@"Out of memory|ErrorMessage:|Access violation|STATUS_ACCESS_VIOLATION"),
            "The server hit a fatal error. Check available RAM (-maxMem), suspect recently added mods, and look for a matching .mdmp/.bidmp crash dump next to the RPT."),
        new("BattlEye failure", "BattlEye", RptSeverity.Critical,
            Rx(@"BattlEye (initialization|Server) fail|Failed to (install|update|load) BattlEye"),
            "BattlEye could not start — clients with BE enabled cannot join. Verify the BEServer_x64.cfg exists and the server has internet access to update BE, or disable BattlEye on the profile."),

        // --- Warnings: degraded behaviour ---
        new("Signature / key problem", "Signatures", RptSeverity.Warning,
            Rx(@"Wrong signature for file|Signature check timed out|unsigned file|invalid key"),
            "A client (or the server) has files that fail signature verification. Make sure every mod's .bikey is deployed to the Keys folder (Mods → Deploy handles this) and clients run the same mod versions."),
        new("SQF script error", "Scripts", RptSeverity.Warning,
            Rx(@"Error Undefined variable|Error position:|Error Missing [;\)\]]|Error Zero divisor|Error Type .*, expected|Error Foreign error|Script .* not found"),
            "Mission or mod scripts are throwing errors. Usually a mission bug or a missing dependency — the 'Error position:' lines name the exact file and expression."),
        new("Missing config entry", "Mods & Addons", RptSeverity.Warning,
            Rx(@"No entry '.*\.bin|No entry 'config\.bin"),
            "The game looked up a config class that no loaded addon defines — typically a mod expecting another mod, or mismatched mod versions between content."),
        new("Missing texture / material / sound", "Content", RptSeverity.Warning,
            Rx(@"Cannot load texture|Cannot load material|Cannot load sound|Cannot find .*\.paa"),
            "An asset referenced by a mission or mod is absent. Harmless visually if occasional; frequent hits suggest a broken or partially downloaded mod."),
        new("Client connection problems", "Network", RptSeverity.Warning,
            Rx(@"Client .* timed out|NetServer::finishDestroyPlayer|Player .* kicked|Steam AuthTicket"),
            "Players are timing out or being kicked. Occasional entries are normal internet churn; clusters point at server bandwidth/basic.cfg tuning or a specific player's connection."),

        // --- Info: normal noise worth explaining ---
        new("SteamAPI initialization failed", "Steam", RptSeverity.Info,
            Rx(@"SteamAPI initialization failed"),
            "Normal and harmless on a dedicated server — the server exe has no Steam client context. Not related to server visibility."),
        new("Duplicate HitPoint config noise", "Config Noise", RptSeverity.Info,
            Rx(@"Duplicate HitPoint name"),
            "Cosmetic config warning from vanilla/mod vehicle configs. Safe to ignore."),
        new("Stringtable language noise", "Config Noise", RptSeverity.Info,
            Rx(@"Unsupported language .* in stringtable"),
            "A mod's stringtable.xml lacks your language block. Purely cosmetic; content falls back to English."),
        new("AI pathing noise", "Config Noise", RptSeverity.Info,
            Rx(@"Unaccessible ladder point|cannot find path|Path not found"),
            "Terrain/building pathing complaints from AI. Cosmetic unless AI visibly get stuck in one spot."),
    };

    public async Task<RptAnalysis> AnalyzeAsync(string rptPath, CancellationToken ct = default) =>
        await Task.Run(() =>
        {
            var findings = Rules.ToDictionary(r => r, _ => (RptFinding?)null);
            var lineNo = 0;

            // Share-tolerant read: the server may still be writing this RPT.
            using var fs = new FileStream(rptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line)
            {
                ct.ThrowIfCancellationRequested();
                lineNo++;
                foreach (var rule in Rules)
                {
                    if (!rule.Pattern.IsMatch(line)) continue;
                    var f = findings[rule] ??= new RptFinding { Rule = rule };
                    f.Count++;
                    if (f.Samples.Count < MaxSamplesPerRule)
                        f.Samples.Add($"line {lineNo}: {line.Trim()}");
                    break;   // first matching rule wins for a line
                }
            }

            var ordered = findings.Values
                .Where(f => f is not null).Cast<RptFinding>()
                .OrderBy(f => f.Rule.Severity)
                .ThenByDescending(f => f.Count)
                .ToList();
            return new RptAnalysis(rptPath, lineNo, ordered);
        }, ct).ConfigureAwait(false);

    public string? FindNewestRpt(string serverDirectory)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory)) return null;
        var dir = Path.Combine(serverDirectory, "profiles");
        if (!Directory.Exists(dir)) return null;
        try
        {
            return new DirectoryInfo(dir).GetFiles("*.rpt", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }
}
