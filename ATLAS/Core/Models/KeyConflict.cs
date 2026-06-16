namespace Atlas.Core.Models;

/// <summary>
/// Records that two deployed mods ship a <c>.bikey</c> with the same file name. When this happens the
/// last key copied into the server's <c>Keys</c> folder wins, which can cause signature-check surprises,
/// so conflicts are surfaced to the user.
/// </summary>
public class KeyConflict
{
    public string KeyFileName { get; set; } = "";
    public string FolderA { get; set; } = "";
    public string FolderB { get; set; } = "";
}
