namespace Atlas.Core.Models;

/// <summary>A single ATLAS application log line captured from Serilog for the in-app log viewer.</summary>
public class LogEntry
{
    public DateTime Timestamp { get; init; }

    /// <summary>Serilog level ordinal: 0 Verbose, 1 Debug, 2 Information, 3 Warning, 4 Error, 5 Fatal.</summary>
    public int Severity { get; init; }

    /// <summary>Three-letter level label (VRB/DBG/INF/WRN/ERR/FTL) for display.</summary>
    public string Level { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    /// <summary>Pre-formatted line ("HH:mm:ss [LVL] message") for display and export.</summary>
    public string Display => $"{Timestamp:HH:mm:ss} [{Level}] {Message}";
}
