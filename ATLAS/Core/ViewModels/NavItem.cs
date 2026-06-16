using MaterialDesignThemes.Wpf;

namespace Atlas.Core.ViewModels;

/// <summary>A single sidebar navigation entry.</summary>
public sealed class NavItem
{
    /// <summary>Navigation key (see <see cref="AppConstants.Pages"/>).</summary>
    public required string Key { get; init; }

    /// <summary>Display label shown next to the icon when the sidebar is expanded.</summary>
    public required string Label { get; init; }

    /// <summary>Material Design icon shown for this entry.</summary>
    public PackIconKind Icon { get; init; }
}
