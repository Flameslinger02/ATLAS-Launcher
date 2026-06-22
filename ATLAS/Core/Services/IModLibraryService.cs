using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Global mod-registry operations spanning every profile and preset. Reads the master <c>Mods</c> table
/// (the deduplicated set of mods ATLAS knows about), reports which profiles reference each, and adds or
/// removes registry rows. Per-profile / per-preset <i>selection</i> and flags live in
/// <see cref="IProfileService"/> / <see cref="IModPresetService"/>; this service owns only the shared library.
/// </summary>
public interface IModLibraryService
{
    /// <summary>Every row in the master <c>Mods</c> table, ordered by name.</summary>
    Task<List<ArmaModEntry>> GetAllModsAsync();

    /// <summary>Maps each mod's <c>Id</c> to the names of the profiles that reference it (via <c>ProfileMods</c>).</summary>
    Task<Dictionary<int, List<string>>> GetModUsageAsync();

    /// <summary>Inserts or updates a mod in the master registry (keyed on WorkshopId + LocalPath); returns its <c>Id</c>.</summary>
    Task<int> UpsertModAsync(ArmaModEntry mod);

    /// <summary>Removes a mod from the registry and from any profile/preset assignment that references it.</summary>
    Task DeleteModAsync(int modId);
}
