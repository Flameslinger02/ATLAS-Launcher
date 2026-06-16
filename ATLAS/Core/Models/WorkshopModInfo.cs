namespace Atlas.Core.Models;

/// <summary>
/// Metadata for a Steam Workshop item, fetched from the public
/// <c>ISteamRemoteStorage/GetPublishedFileDetails</c> endpoint (no API key required).
/// </summary>
public class WorkshopModInfo
{
    public ulong WorkshopId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Item size in bytes (the endpoint returns this as a numeric string).</summary>
    public ulong FileSize { get; set; }

    /// <summary>Last-updated time in UTC (from the endpoint's <c>time_updated</c> unix-epoch seconds).</summary>
    public DateTime TimeUpdated { get; set; }

    public string PreviewUrl { get; set; } = "";
}
