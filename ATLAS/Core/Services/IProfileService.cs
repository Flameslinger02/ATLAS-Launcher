using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// The single owner of all profile persistence. Assembles the full <see cref="ServerProfile"/> object
/// graph from <c>ServerProfiles</c> and its child tables, and saves it atomically in a transaction.
/// Also tracks the in-memory active profile.
/// </summary>
public interface IProfileService
{
    Task<List<ServerProfile>> GetAllProfilesAsync();
    Task<ServerProfile?> GetProfileByIdAsync(int id);
    Task<ServerProfile> CreateProfileAsync(ServerProfile profile);
    Task UpdateProfileAsync(ServerProfile profile);
    Task DeleteProfileAsync(int id);
    Task<ServerProfile> CloneProfileAsync(int id, string newName);
    Task SetDefaultProfileAsync(int id);
    Task<ServerProfile?> GetDefaultProfileAsync();

    // Profile import/export to .atlasprofile JSON file
    Task ExportProfileAsync(int id, string outputPath);
    Task<ServerProfile> ImportProfileAsync(string filePath);

    // Active profile tracking (in-memory, not persisted)
    ServerProfile? ActiveProfile { get; }
    void SetActiveProfile(ServerProfile profile);
    event EventHandler<ServerProfile>? ActiveProfileChanged;

    /// <summary>Raised whenever the set of profiles changes (create/update/delete/clone/set-default/import),
    /// so the sidebar profile list and the overview page can refresh regardless of who made the change.</summary>
    event EventHandler? ProfilesChanged;
}
