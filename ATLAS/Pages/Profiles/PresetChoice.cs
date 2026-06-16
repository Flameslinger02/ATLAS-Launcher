namespace Atlas.Pages.Profiles;

/// <summary>An entry in the profile editor's mod-preset dropdown. <see cref="Id"/> null = "(None)".</summary>
public sealed class PresetChoice
{
    public int? Id { get; init; }
    public string Name { get; init; } = string.Empty;
}
