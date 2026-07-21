using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace Atlas.Core.Services;

/// <summary>Outcome of a Steam master-server-list lookup for a given public IP + game port.</summary>
public enum MasterListStatus
{
    /// <summary>No Steam Web API key is configured — the public check can't run.</summary>
    NoApiKey,
    /// <summary>The API call failed (network/HTTP/parse) — reachability is undetermined.</summary>
    Unavailable,
    /// <summary>The call succeeded but no server at that address/port is in Steam's list.</summary>
    NotListed,
    /// <summary>A matching server is present in Steam's list (i.e. publicly reachable).</summary>
    Listed,
}

/// <summary>Basic info about a server found in Steam's master list.</summary>
public sealed record MasterListEntry(string Name, int Players, int MaxPlayers, string Map);

/// <summary>
/// Asks Steam's master server list whether a dedicated server is publicly visible. A server only appears
/// in that list once Steam's own infrastructure has reached it — the same list the in-game server browser
/// reads — so presence at our public IP proves the server is reachable from the internet (port forwarding
/// / UPnP working), independent of the local A2S loopback check.
/// </summary>
public interface ISteamMasterListService
{
    Task<(MasterListStatus Status, MasterListEntry? Entry)> FindAsync(
        string publicIp, int gamePort, CancellationToken ct = default);
}

/// <inheritdoc cref="ISteamMasterListService"/>
public sealed class SteamMasterListService : ISteamMasterListService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private const string GetServerListUrl = "https://api.steampowered.com/IGameServersService/GetServerList/v1/";
    private const int Arma3AppId = 107410;   // Arma 3 game app id — servers heartbeat to the master list under this.

    private readonly ISettingsService _settings;
    private readonly ISecretProtector _secrets;

    public SteamMasterListService(ISettingsService settings, ISecretProtector secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public async Task<(MasterListStatus Status, MasterListEntry? Entry)> FindAsync(
        string publicIp, int gamePort, CancellationToken ct = default)
    {
        var key = _secrets.Decrypt(_settings.Settings.SteamApiKeyEncrypted);
        if (string.IsNullOrWhiteSpace(key)) return (MasterListStatus.NoApiKey, null);

        try
        {
            // Primary: constrain server-side to Arma 3 (fewer rows). Arma 3 dedicated servers heartbeat to
            // the master list under the GAME app id 107410 (233780 is only the server-download app).
            var (status, entry) = await QueryAsync(key, $@"\appid\{Arma3AppId}\gameaddr\{publicIp}", gamePort, ct)
                .ConfigureAwait(false);
            if (status == MasterListStatus.Listed || status == MasterListStatus.Unavailable) return (status, entry);

            // Fallback: query every server Steam knows at this IP (no app-id constraint) and match the port.
            // This self-heals if 107410 were ever wrong — a false "not listed" would otherwise read as Local.
            var (status2, entry2) = await QueryAsync(key, $@"\gameaddr\{publicIp}", gamePort, ct).ConfigureAwait(false);
            if (status2 == MasterListStatus.Listed)
                Log.Warning("Server matched Steam's list only without the app-id filter — appid {AppId} may be wrong.", Arma3AppId);
            return (status2, entry2);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Steam master-list lookup for {Ip}:{Port} failed.", publicIp, gamePort);
            return (MasterListStatus.Unavailable, null);
        }
    }

    private static async Task<(MasterListStatus, MasterListEntry?)> QueryAsync(
        string key, string filter, int gamePort, CancellationToken ct)
    {
        var url = $"{GetServerListUrl}?key={Uri.EscapeDataString(key)}&filter={Uri.EscapeDataString(filter)}&limit=64";

        await using var stream = await Http.GetStreamAsync(url, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("servers", out var servers) ||
            servers.ValueKind != JsonValueKind.Array)
        {
            return (MasterListStatus.NotListed, null);   // empty response = Steam has nothing at this IP
        }

        foreach (var s in servers.EnumerateArray())
        {
            if (!s.TryGetProperty("gameport", out var gp) || gp.ValueKind != JsonValueKind.Number ||
                gp.GetInt32() != gamePort)
            {
                continue;
            }

            // Guard against an unrelated game sharing this public IP + port: accept only an Arma entry.
            // (When the app-id filter is applied server-side this is always true; it matters for the
            // unfiltered fallback query.)
            if (!IsArma(s)) continue;

            return (MasterListStatus.Listed, new MasterListEntry(
                GetString(s, "name"),
                GetInt(s, "players"),
                GetInt(s, "max_players"),
                GetString(s, "map")));
        }

        return (MasterListStatus.NotListed, null);
    }

    /// <summary>True when a master-list entry is an Arma server — by app id (107410) or, failing that, by
    /// its game directory/product ("arma3"). Tolerant of which field Steam populates.</summary>
    private static bool IsArma(JsonElement s)
    {
        if (s.TryGetProperty("appid", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() == Arma3AppId)
            return true;
        return GetString(s, "gamedir").Contains("arma", StringComparison.OrdinalIgnoreCase)
            || GetString(s, "product").Contains("arma", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
