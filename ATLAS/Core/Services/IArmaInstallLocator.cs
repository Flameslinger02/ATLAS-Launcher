namespace Atlas.Core.Services;

/// <summary>
/// Best-effort locator for a local Arma 3 dedicated-server install, discovered from Steam library folders.
/// </summary>
public interface IArmaInstallLocator
{
    /// <summary>
    /// Returns the directory containing an <c>arma3server*.exe</c> (checking the "Arma 3 Server" then "Arma 3"
    /// Steam folders across all libraries), or <c>null</c> if none is found.
    /// </summary>
    string? FindServerDirectory();
}
