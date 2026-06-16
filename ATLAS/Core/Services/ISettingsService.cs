using Atlas.Core.Models;

namespace Atlas.Core.Services;

/// <summary>
/// Loads and persists <see cref="AppSettings"/> to <c>%AppData%\ATLAS\settings.json</c>.
/// </summary>
public interface ISettingsService
{
    /// <summary>The current, in-memory settings object. Never null after <see cref="LoadAsync"/>.</summary>
    AppSettings Settings { get; }

    /// <summary>Loads settings from disk, creating defaults if the file does not exist.</summary>
    Task LoadAsync();

    /// <summary>Persists the current <see cref="Settings"/> to disk.</summary>
    Task SaveAsync();

    /// <summary>
    /// Replaces <see cref="Settings"/> with a fresh defaults object and persists it, discarding all
    /// stored values including encrypted secrets (Phase 13 "Reset All Settings").
    /// </summary>
    Task ResetAsync();
}
