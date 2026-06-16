namespace Atlas.Core.Models;

/// <summary>
/// A named, reusable mod collection. Assembled from <c>ModPresets</c> JOIN <c>PresetMods</c> JOIN
/// <c>Mods</c>; the <see cref="Mods"/> list is never stored as a JSON blob.
/// </summary>
public class ModPreset
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ArmaModEntry> Mods { get; set; } = [];
}
