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
        // "cannot play/edit this mission" is a notorious false positive: mission.sqm addOns[] often
        // carries stale entries (vanilla a3_* names especially — a known BI cfgPatches quirk) and the
        // mission loads and runs fine anyway. Only a "Cannot load mission" line means it truly failed.
        new("Mission dependency warning", "Mods & Addons", RptSeverity.Warning,
            Rx(@"requires addon|Missing addons detected|You cannot play/edit this mission"),
            "The mission lists addons that are not loaded. This is very often harmless — missions edited with extra mods (or naming vanilla a3_* content) carry stale entries in mission.sqm and still run fine. It only matters if the mission actually failed to start: check for a 'Mission failed to load' finding. If a real mod is missing, add it to the profile (the Missions tab's Dependencies list shows what the mission asks for)."),
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

        // ── Nexus Admin (inherited mod) ──────────────────────────────────────
        // Every line carries a mod-unique tag ([NexusAdmin] or [CN ADMIN]); none
        // share a substring with the generic rules above, so a Nexus line is
        // always claimed by a Nexus rule regardless of block position. Internal
        // order matters: keep the [NexusAdmin][INFO] catch-all LAST so the two
        // specific INFO rules above it win first. Derived from the mod source
        // (addons/main/functions/*.sqf); all strings are real engine literals.
        new("Nexus Admin: license-enforcement shutdown", "Nexus Admin", RptSeverity.Critical,
            Rx(@"\[CN ADMIN\] Status:\s+REJECTED"),
            "Nexus Admin has latched an enforcement shutdown. Read the following '[CN ADMIN] Reason:' line in the RPT for the specific code, fix licensing (renew plan / clear host binding) on the Command Nexus dashboard, then restart the server — the countdown will not stop on its own."),
        new("Nexus Admin: licensing server unreachable, shutdown imminent", "Nexus Admin", RptSeverity.Critical,
            Rx(@"\[NexusAdmin\]\[WARN\] \[Command Nexus\] licensing server unreachable"),
            "The server can't reach the Command Nexus API and will shut itself down in ~60s unless connectivity is restored. Check outbound HTTPS from this box to the Command Nexus API endpoint (firewall/DNS)."),
        new("Nexus Admin: license rejected at startup", "Nexus Admin", RptSeverity.Critical,
            Rx(@"\[NexusAdmin\]\[ERROR\] license REJECTED at startup \("),
            "The mod never got a valid license this boot and is staying inert (server keeps running, admin tools don't activate). Verify token/apiBase/gamePort in userconfig\\cn_admin\\config.hpp against the server row on the Command Nexus dashboard — the code in parentheses tells you exactly what was rejected."),
        new("Nexus Admin: running build newer than entitled version", "Nexus Admin", RptSeverity.Critical,
            Rx(@"\[NexusAdmin\]\[WARN\] build \S+ is newer than the entitled version"),
            "The installed Nexus Admin build is newer than what the org's plan entitles, so it refuses to activate. Downgrade to the entitled build or upgrade the plan on the Command Nexus dashboard."),
        new("Nexus Admin: admin menu failed to open entirely", "Nexus Admin", RptSeverity.Critical,
            Rx(@"\[NexusAdmin\]\[ERROR\] admin menu failed to open by BOTH createDisplay and createDialog"),
            "Both display-creation paths failed — no admin can open the menu right now. Have the admin close other open dialogs/displays and retry; if it persists, check for another addon hijacking display 46 or the RscNexusAdminV2 dialog resource."),
        new("Nexus Admin: admin menu createDisplay fallback", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[ERROR\] admin menu createDisplay returned a null display"),
            "createDisplay was refused (a display-stacking edge case) and the mod fell back to createDialog — the menu still opened. No action needed unless it recurs constantly or the admin is unconscious when it fires (the dialog fallback force-closes under ACE3)."),
        new("Nexus Admin: entitlement denial, retrying before enforcement", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] entitlement denied — one re-verify before enforcing"),
            "The site reported the org isn't licensed for admin tools (plan expired/inactive/not licensed). One re-verify is attempted before this becomes a shutdown — renew/reactivate the plan on the Command Nexus dashboard now."),
        new("Nexus Admin: host-binding mismatch, retrying before enforcement", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] host binding mismatch — one re-verify before enforcing"),
            "The licensed server files appear to be running on a different machine than they were bound to. If this is an intentional migration, clear the host binding under Nexus Admin -> Servers & Tokens on the dashboard, then restart."),
        new("Nexus Admin: license rejected mid-session, retrying before enforcement", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] license rejected mid-session — one re-verify before enforcing"),
            "The session token was rejected mid-session. One fresh verify is attempted before treating it as a real revoke. Check the token/server status on the Command Nexus dashboard if it escalates to a shutdown."),
        new("Nexus Admin: enforcement triggered", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] enforcement: \S+"),
            "Reason-specific license enforcement is running; the token after 'enforcement:' is the raw code (TRANSPORT, PLAN_EXPIRED, TOKEN_REVOKED, HWID_BINDING_MISMATCH, ...). Use it to identify the cause, then act on the matching specific message."),
        new("Nexus Admin: API call skipped, no apiBase configured", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] apiCall skipped: no API base configured"),
            "apiBase is empty in the mod's config, so it's intentionally inert. Set apiBase and token in userconfig\\cn_admin\\config.hpp if this server is meant to be licensed."),
        new("Nexus Admin: RCON not configured", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] rcon skipped: no RCON port/password configured"),
            "rconPort/rconPassword are unset. Bans, kicks, and scheduled shutdown/restart all route through local RCON and silently no-op without it. Set rconPort/rconPassword in userconfig\\cn_admin\\config.hpp to match this server's own BEServer.cfg RConPort/RConPassword."),
        new("Nexus Admin: bridge/RCON transport unavailable", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] (transport|rcon transport) unavailable \("),
            "The cn_admin native extension isn't loaded or its worker pool is saturated. Confirm cn_admin_x64.dll/.so sits next to the server binary and loaded (check earlier RPT lines for extension load errors). Transient if it clears on its own."),
        new("Nexus Admin: API/RCON call failed", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] (transport failed \(endpoint=|rcon failed \(cmd=)"),
            "A request completed but didn't succeed (network blip, bad RCON auth, or a site-side error). Usually self-heals on retry; check network/firewall or rconPort/rconPassword if it persists."),
        new("Nexus Admin: admin menu blocked, no active license", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] admin menu blocked: no active license"),
            "An admin pressed the menu key before/without a verified license. Check the RPT for the startup license-rejection or unreachable-site entries explaining why it hasn't activated."),
        new("Nexus Admin: admin menu blocked, UID not whitelisted", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] admin menu blocked: uid \S+ not on the synced whitelist"),
            "This Steam UID isn't in the synced admin whitelist. Add it as an admin on the Command Nexus dashboard and wait for the next whitelist sync (or restart the server)."),
        new("Nexus Admin: menu key pressed, a gate failed", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] menu key pressed but gates failed"),
            "Client-side diagnostic showing exactly which gate is failing for this admin — read the licenseActive=/whitelisted= fields in the same line."),
        new("Nexus Admin: privileged action rejected, caller not whitelisted", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] rejected \S+ from non-whitelisted"),
            "A privileged admin action arrived from a UID that isn't whitelisted (identity is always re-derived server-side, so this can't be spoofed). If it's a legitimate admin, add their UID on the dashboard; otherwise treat as a possible tampering attempt — the action was rejected either way."),
        new("Nexus Admin: privileged action rejected, insufficient level", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] rejected \S+ from \S+: insufficient admin level"),
            "The whitelisted admin's level doesn't meet this action's minimum. Raise their level on the dashboard, or adjust the relevant CN_Perm_* override if the org wants a custom threshold."),
        new("Nexus Admin: audit/ban-list/AC/spawn rejected, not whitelisted", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] (audit|ban-list \S+|AC \S+|spawn) rejected:.*not whitelisted"),
            "A whitelist gate was hit on the audit log, ban-list panel, anti-cheat clear, or spawner path. Verify the UID is whitelisted and synced."),
        new("Nexus Admin: ban action rejected, plan lacks ban_sync", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] ban(-list \S+)? rejected: plan lacks ban_sync"),
            "The org's current plan doesn't include the ban_sync feature, so ban/ban-lift actions are blocked. Upgrade the plan on the Command Nexus dashboard."),
        new("Nexus Admin: ban-list lift rejected, lacks ban level", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] ban-list lift rejected: \S+ lacks ban level"),
            "The admin tried to lift a ban but doesn't meet the ban action level. Raise their level on the dashboard or set CN_Perm_Ban."),
        new("Nexus Admin: framework action rejected/unhandled", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] fw (action rejected|module did not handle)"),
            "A framework tool (Exile/Altis Life/CN Zombies) was rejected or unhandled — the line states which: caller not whitelisted, plan lacks framework_sync, action not valid for the detected framework, or the module didn't handle it. Whitelist the admin, upgrade the plan for framework_sync, or confirm the action fits the detected framework."),
        new("Nexus Admin: Altis Life framework tool unavailable", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] life_\w+:"),
            "An Altis Life depth verb needs a framework function that isn't present (e.g. TON_fnc_removeGang, life_fnc_jailSys) or the issuing admin isn't online. Confirm the Altis Life framework exposes the referenced function, or retry while the admin is connected."),
        new("Nexus Admin: whitelisted admin missing the client mod", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[INFO\] admin online: .* client mod NOT LOADED"),
            "This whitelisted admin is connected but isn't running the Nexus Admin client mod, so their menu key does nothing. Have them enable @NexusAdmin in their launcher and reconnect."),
        new("Nexus Admin: ban sync failed", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] ban sync failed \((transport|server)\) — keeping cached list"),
            "Couldn't reach the site on this ban-reconcile cycle; the last-known ban list is kept and enforcement continues on it. Self-heals next cycle — investigate connectivity if it repeats for many cycles."),
        new("Nexus Admin: whitelist sync failed", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] whitelist sync failed \((transport|server)\) — keeping cached list"),
            "Couldn't pull the admin whitelist this cycle; the cached whitelist is kept so admins aren't locked out by a blip. Self-heals next cycle — investigate connectivity if it repeats."),
        new("Nexus Admin: BE ban-list read failed during removal sweep", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] ban removal sweep: could not read BE ban list"),
            "The RCON 'bans' command didn't return successfully, so stale/lifted bans couldn't be swept this cycle. Check rconPort/rconPassword and that BattlEye RCON is reachable on loopback. Retries automatically."),
        new("Nexus Admin: BE ban import failed", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] ban import: (could not read BE ban list \(RCON\)|upload failed \(transport\))"),
            "Hand-added BattlEye bans couldn't be read or uploaded to Command Nexus this cycle. Transient; retries on the next banImportInterval. Check RCON creds/connectivity if persistent."),
        new("Nexus Admin: command-queue poll failed", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] command poll failed \((transport|server)\) — will retry"),
            "The server couldn't poll the dashboard's command queue this cycle, so restarts/slot-lock/weather commands issued from the website won't be picked up until it clears. Retries automatically."),
        new("Nexus Admin: RCON kick could not land", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] rconKick: (no BE GUID for uid|could not read BE player list)"),
            "A kick/ban couldn't remove the player — no BE GUID derivable for the UID, or the BE players list couldn't be read over RCON. Verify RCON creds and BattlEye reachability. Ban state is re-enforced next reconcile, but the live kick did not happen."),
        new("Nexus Admin: audit post to website failed", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] audit post failed"),
            "An admin-action audit entry couldn't be posted to the website audit ingest. The in-game action still happened; only the central audit record failed. Check site connectivity if it repeats."),
        new("Nexus Admin: player report submit failed", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] case submit failed \(transport\)"),
            "A player report couldn't be submitted to the Command Nexus case queue. Transient; the reporter was already thanked in-game. Check connectivity if it repeats."),
        new("Nexus Admin: mission XP hook rejected unregistered event", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] mission API: rejected unregistered event"),
            "A mission script called CN_fnc_awardXP with an event key that was never registered via CN_fnc_registerEvent, so the award was dropped. Register the event key at mission init before awarding it, or fix the key in the mission script."),
        new("Nexus Admin: client never got license confirmation", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] no license confirmation from the server after 60 s"),
            "60s after joining, this client still hasn't seen the server confirm an active license. Not fatal — the menu key stays live and re-checks each press. Check the server RPT for a startup license-rejection or unreachable-site entry explaining the delay."),
        new("Nexus Admin: server presence not confirmed on client", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] server presence not confirmed — mod staying inert"),
            "This client never saw the server broadcast that it's running Nexus Admin. Confirm the server is actually running the mod (check for 'preInit complete'/'postInit complete' in the server RPT) and that client/server mod versions match."),
        new("Nexus Admin: anti-cheat auto-kick", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] AC auto-kick"),
            "The built-in anti-cheat's suspicion score crossed the auto-kick threshold for this player; the message names which heuristic tripped (speed/teleport/altitude/ammo) and the score. Review in the admin menu's Watchlist/AC tab; tune the relevant CN_AH_* threshold if this looks like a false positive."),
        new("Nexus Admin: admin escalated a player to the CN network", "Nexus Admin", RptSeverity.Warning,
            Rx(@"\[NexusAdmin\]\[WARN\] escalation: .* -> CN network"),
            "An admin used the escalate action — bans the player locally and files a high-priority cross-server review case. Informational; cross-check the linked case on the dashboard if auditing admin conduct."),
        new("Nexus Admin: userconfig file not found", "Nexus Admin", RptSeverity.Info,
            Rx(@"\[NexusAdmin\]\[INFO\] userconfig not found \(userconfig\\cn_admin\\config\.hpp\)"),
            "No userconfig\\cn_admin\\config.hpp on disk; the mod fell back to CfgPatches/description.ext defaults, which can't safely carry secrets like token/rconPassword. Expected on a fresh install — create the userconfig file if this server should be licensed."),
        new("Nexus Admin: ban import deferred, license not active", "Nexus Admin", RptSeverity.Info,
            Rx(@"\[NexusAdmin\]\[WARN\] ban import skipped: license not active"),
            "Expected before the first successful license verify completes. Only worth investigating if the license never activates (see the startup license-rejection rules)."),
        // KEEP LAST among Nexus Admin rules: sweeps all remaining benign INFO output.
        new("Nexus Admin: routine informational output", "Nexus Admin", RptSeverity.Info,
            Rx(@"\[NexusAdmin\]\[INFO\]"),
            "Normal Nexus Admin init/status output — confirms the mod loaded and is operating normally. No action needed."),
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
