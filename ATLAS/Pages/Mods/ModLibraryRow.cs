using Atlas.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atlas.Pages.Mods;

/// <summary>
/// One row in the global mod-library grid: the underlying registry entry (<see cref="Mod"/>) plus the
/// selection + status presentation the grid binds to. <see cref="Selected"/>/<see cref="Status"/> raise
/// change notifications so the checkbox column and status text update live.
/// </summary>
public partial class ModLibraryRow : ObservableObject
{
    public ArmaModEntry Mod { get; }

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private string _status;

    public ModLibraryRow(ArmaModEntry mod, string usedBy, string status)
    {
        Mod = mod;
        UsedBy = usedBy;
        _status = status;
    }

    public string Name => Mod.Name;
    public ulong WorkshopId => Mod.WorkshopId;
    public string WorkshopIdText => Mod.WorkshopId == 0 ? "—" : Mod.WorkshopId.ToString();
    public bool IsLocal => Mod.IsLocal;
    public string Version => string.IsNullOrWhiteSpace(Mod.Version) ? "—" : Mod.Version;
    public string SizeText => Mod.SteamFileSize == 0 ? "—" : FormatSize(Mod.SteamFileSize);
    public string LastUpdatedText =>
        Mod.LastUpdated == DateTime.MinValue ? "—" : Mod.LastUpdated.ToLocalTime().ToString("yyyy-MM-dd");
    public string UsedBy { get; }

    private static string FormatSize(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.#} {units[u]}";
    }
}
