using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// A BattlEye RCon (BERCon) UDP client: login, command/response (with multi-packet reassembly), server
/// messages (auto-acked), and keepalive. See <c>docs/REFERENCE_arma3.md</c> for the wire protocol.
/// </summary>
public interface IBattlEyeRconClient
{
    /// <summary>Current connection state (mirrors the latest value on <see cref="StateChanged"/>).</summary>
    RconState State { get; }

    /// <summary>Pushes the current state on subscribe and on every transition.</summary>
    IObservable<RconState> StateChanged { get; }

    /// <summary>Server-initiated messages (chat, connect/disconnect, kicks). Already acknowledged.</summary>
    IObservable<string> MessageReceived { get; }

    /// <summary>Pushes the latest player list on each 30s background poll (and on manual refreshes).</summary>
    IObservable<IReadOnlyList<RconPlayer>> PlayersUpdated { get; }

    /// <summary>Connects and logs in. Returns true on a successful login, false on bad password/timeout.</summary>
    Task<bool> ConnectAsync(string host, int port, string password, CancellationToken ct = default);

    /// <summary>Closes the connection (no-op if already disconnected).</summary>
    void Disconnect();

    /// <summary>Sends a command and returns the (possibly multi-packet) response text.</summary>
    Task<string> SendCommandAsync(string command, CancellationToken ct = default);

    /// <summary>Runs <c>players</c> and parses the result (also publishes to <see cref="PlayersUpdated"/>).</summary>
    Task<List<RconPlayer>> GetPlayersAsync(CancellationToken ct = default);

    /// <summary>Runs <c>bans</c> and parses the GUID + IP ban sections.</summary>
    Task<List<RconBan>> GetBansAsync(CancellationToken ct = default);

    /// <summary>Runs <c>missions</c> and returns the available mission template names.</summary>
    Task<List<string>> GetMissionsAsync(CancellationToken ct = default);

    Task SayGlobalAsync(string message);
    Task SayPlayerAsync(int id, string message);
    Task KickPlayerAsync(int id, string reason = "");
    Task BanPlayerAsync(int id, int minutes = 0, string reason = "");

    /// <summary>Adds a ban by GUID directly (<c>addBan</c>); the player need not be online.</summary>
    Task BanGuidAsync(string guid, int minutes = 0, string reason = "");

    /// <summary>Removes a ban by its index in the ban list (<c>removeBan</c>).</summary>
    Task UnbanAsync(int banIndex);

    Task LockServerAsync();
    Task UnlockServerAsync();
    Task ShutdownServerAsync();

    /// <summary>Restarts the current mission (<c>#restart</c>).</summary>
    Task RestartMissionAsync();

    /// <summary>Reassigns players to roles / reloads the mission (<c>#reassign</c>).</summary>
    Task ReassignMissionAsync();
}
