namespace Atlas.Core.Models;

/// <summary>Lifecycle state of a managed server (or headless client) process.</summary>
public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
    Updating
}
