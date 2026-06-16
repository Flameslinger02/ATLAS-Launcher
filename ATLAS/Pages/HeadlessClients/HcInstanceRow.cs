using Atlas.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atlas.Pages.HeadlessClients;

/// <summary>
/// View wrapper around a <see cref="HeadlessClientInstance"/> for the status cards. State/PID/crash count
/// are snapshotted when the collection is rebuilt; <see cref="UptimeText"/> ticks live off a VM timer.
/// </summary>
public partial class HcInstanceRow : ObservableObject
{
    public HeadlessClientInstance Instance { get; }

    [ObservableProperty] private string _uptimeText = "00:00:00";

    public HcInstanceRow(HeadlessClientInstance instance) => Instance = instance;

    public int Index => Instance.Index;
    public string Name => Instance.Name;
    public ServerState State => Instance.State;
    public int CrashCount => Instance.CrashCount;

    public string PidText
    {
        get
        {
            try
            {
                var proc = Instance.Process;
                return proc is { HasExited: false } ? proc.Id.ToString() : "—";
            }
            catch { return "—"; }
        }
    }

    /// <summary>Recomputes the live uptime from <see cref="HeadlessClientInstance.StartedAt"/>.</summary>
    public void Tick()
    {
        if (State == ServerState.Running && Instance.StartedAt is { } started)
        {
            var up = DateTime.UtcNow - started;
            UptimeText = $"{(int)up.TotalHours:00}:{up.Minutes:00}:{up.Seconds:00}";
        }
        else
        {
            UptimeText = "00:00:00";
        }
    }
}
