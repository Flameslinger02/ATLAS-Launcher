using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Owns mod-preset persistence and the import/export of Arma 3 Launcher <c>.html</c> presets.
/// Assembles each <see cref="ModPreset"/> from <c>ModPresets</c> JOIN <c>PresetMods</c> JOIN <c>Mods</c>.
/// </summary>
public interface IModPresetService
{
    Task<List<ModPreset>> GetAllPresetsAsync();
    Task<ModPreset?> GetPresetByIdAsync(int id);
    Task<ModPreset> CreatePresetAsync(string name, string description, List<ArmaModEntry> mods);
    Task UpdatePresetAsync(ModPreset preset);
    Task DeletePresetAsync(int id);
    Task<ModPreset> ClonePresetAsync(int id, string newName);

    /// <summary>Copies the preset's mods into <paramref name="profile"/> and links it (sets ActiveModPresetId). Caller persists.</summary>
    Task ApplyPresetToProfileAsync(int presetId, ServerProfile profile);

    /// <summary>Unlinks the profile from any preset (keeps the current mod list). Caller persists.</summary>
    void DetachPresetFromProfile(ServerProfile profile);

    /// <summary>Saves the profile's current mod list as a new named preset.</summary>
    Task<ModPreset> CreatePresetFromProfileAsync(string name, string description, ServerProfile profile);

    /// <summary>Parses an Arma 3 Launcher .html preset into a list of mod entries (no persistence).</summary>
    Task<List<ArmaModEntry>> ParseA3LauncherPresetAsync(string filePath);

    /// <summary>Imports an Arma 3 Launcher .html preset into a new named ATLAS preset.</summary>
    Task<ModPreset> ImportFromA3LauncherPresetAsync(string filePath, string presetName);

    /// <summary>Exports a preset to Arma 3 Launcher .html format (clients can import it into their launcher).</summary>
    Task ExportToA3LauncherFormatAsync(ModPreset preset, string outputPath);
}
