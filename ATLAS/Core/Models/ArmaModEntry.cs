namespace Atlas.Core.Models;

/// <summary>
/// A mod as assigned to a profile or preset. Merges the shared metadata from the <c>Mods</c> table
/// with the per-assignment settings from <c>ProfileMods</c> / <c>PresetMods</c>.
/// </summary>
public class ArmaModEntry
{
    // ----- From Mods table (shared metadata) -----
    public int ModId { get; set; }            // Mods.Id (FK)
    public ulong WorkshopId { get; set; }     // 0 = local mod
    public string Name { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty; // @ModName
    public string LocalPath { get; set; } = string.Empty;
    public bool IsLocal { get; set; }
    public string Version { get; set; } = string.Empty;
    public ulong SteamFileSize { get; set; }
    public DateTime LastUpdated { get; set; }
    public DateTime LastChecked { get; set; }
    public bool UpdateAvailable { get; set; }

    // ----- From ProfileMods / PresetMods (per-assignment settings) -----
    public int LoadOrder { get; set; }
    public bool EnabledForServer { get; set; } = true;
    public bool EnabledForClient { get; set; } = true;
    public bool EnabledForHeadless { get; set; } = true;
    public bool IsOptional { get; set; }
    public bool IsServerOnly { get; set; }   // goes in -serverMod=
    public bool IsHeadlessOnly { get; set; }
}
