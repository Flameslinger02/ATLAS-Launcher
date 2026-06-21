namespace Atlas.Pages.Profiles;

/// <summary>An entry in the profile editor's mod-preset dropdown. <see cref="Id"/> null = "(None)".</summary>
public sealed class PresetChoice
{
    public int? Id { get; init; }
    public string Name { get; init; } = string.Empty;

    // The custom ComboBox template's selection box falls back to ToString() for the displayed item,
    // so return the name (DisplayMemberPath still drives the dropdown items).
    public override string ToString() => Name;
}
