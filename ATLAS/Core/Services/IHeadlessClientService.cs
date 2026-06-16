using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Manages the pool of headless-client processes for a profile. Each instance connects to the local
/// server (<c>-client -connect=127.0.0.1</c>) with the profile's client/headless mod set. Crash
/// detection optionally auto-restarts an instance (per <c>ServerProfile.HeadlessAutoRestart</c>).
/// </summary>
public interface IHeadlessClientService
{
    /// <summary>Snapshot of the managed instances (ordered by index).</summary>
    IReadOnlyList<HeadlessClientInstance> Instances { get; }

    /// <summary>Fires (with the affected instance) whenever an instance is added or changes state.</summary>
    IObservable<HeadlessClientInstance> InstanceChanged { get; }

    /// <summary>Ensures exactly the instances <c>[0..count)</c> exist (never removes running ones).</summary>
    void ConfigureInstances(int count);

    /// <summary>Launches all instances for <paramref name="profile"/> (count = HeadlessClientCount).</summary>
    Task LaunchAllAsync(ServerProfile profile, CancellationToken ct = default);

    /// <summary>Launches (or relaunches) a single instance by index.</summary>
    Task LaunchSingleAsync(ServerProfile profile, int index, CancellationToken ct = default);

    /// <summary>Stops every running instance.</summary>
    Task StopAllAsync(CancellationToken ct = default);

    /// <summary>Stops a single instance by index.</summary>
    Task StopSingleAsync(int index, CancellationToken ct = default);

    /// <summary>Stops then relaunches a single instance by index.</summary>
    Task RestartSingleAsync(ServerProfile profile, int index, CancellationToken ct = default);

    /// <summary>The per-instance <c>-profiles</c> directory where instance <paramref name="index"/> writes its <c>.rpt</c>.</summary>
    string GetInstanceLogDirectory(int index);
}
