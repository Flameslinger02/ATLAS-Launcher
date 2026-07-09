using Atlas.Core.Models;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IServerMaintenanceService"/>
public sealed class ServerMaintenanceService : IServerMaintenanceService
{
    private readonly IServerProcessService _server;
    private readonly ISteamCmdService _steamCmd;
    private readonly ISettingsService _settings;

    private static readonly IProgress<string> Sink = new Progress<string>(_ => { });

    public ServerMaintenanceService(
        IServerProcessService server, ISteamCmdService steamCmd, ISettingsService settings)
    {
        _server = server;
        _steamCmd = steamCmd;
        _settings = settings;
    }

    public async Task<string> UpdateAndRestartAsync(
        ServerProfile profile, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var report = progress ?? Sink;
        void Say(string m) => report.Report(m);

        // Stop first so SteamCMD can replace the server exe / mod files without file locks, and so mod
        // (re)deployment on the next launch links the freshly-updated content.
        var wasRunning = _server.CurrentState == ServerState.Running;
        if (wasRunning)
        {
            Say("Stopping the server…");
            await _server.StopAsync(force: false, ct).ConfigureAwait(false);
        }

        var serverUpdated = false;
        if (!string.IsNullOrWhiteSpace(profile.ServerDirectory))
        {
            Say("Updating the Arma 3 server (SteamCMD)…");
            await _steamCmd.UpdateServerAsync(profile.ServerDirectory, profile.UseProfilingBranch, report, ct)
                .ConfigureAwait(false);
            serverUpdated = true;
        }
        else
        {
            Say("Server directory not set — skipping the server update.");
        }

        var ids = profile.Mods.Where(m => m.WorkshopId > 0).Select(m => m.WorkshopId).Distinct().ToList();
        var modsUpdated = 0;
        if (ids.Count > 0)
        {
            var login = _steamCmd.GetSavedUsername();
            if (string.IsNullOrWhiteSpace(login))
            {
                Say("No saved Steam login — skipping mod updates (log in on the Mods page).");
            }
            else
            {
                Say($"Updating {ids.Count} Workshop mod(s)…");
                await _steamCmd.UpdateModsAsync(ids, StagingPath(), login, report, ct).ConfigureAwait(false);
                modsUpdated = ids.Count;
            }
        }

        Say("Launching the server…");
        await _server.LaunchAsync(profile, ct).ConfigureAwait(false);   // writes configs, deploys mods, starts

        var summary = $"Server {(serverUpdated ? "updated" : "update skipped")}, {modsUpdated} mod(s) updated, server restarted.";
        Log.Information("Update & Restart for '{Profile}': {Summary}", profile.Name, summary);
        return summary;
    }

    private string StagingPath()
    {
        var configured = _settings.Settings.ModStagingDirectory;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppConstants.AppDataRoot, "Mods")
            : configured;
    }
}
