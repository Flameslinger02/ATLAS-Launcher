namespace Atlas.Core.Services;

/// <summary>
/// Thrown by an interactive SteamCMD mod operation when SteamCMD needs a Steam login that isn't available
/// — i.e. there is no cached session, or it has expired. The caller should run a login and retry.
/// </summary>
public sealed class SteamLoginRequiredException : Exception
{
    public SteamLoginRequiredException()
        : base("SteamCMD requires a Steam login (no cached session, or it has expired).") { }
}
