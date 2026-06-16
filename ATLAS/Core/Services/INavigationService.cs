namespace Atlas.Core.Services;

/// <summary>
/// Resolves page views from the DI container by key and notifies the shell when navigation occurs.
/// Page keys are defined in <see cref="AppConstants.Pages"/>.
/// </summary>
public interface INavigationService
{
    /// <summary>Raised after a successful navigation; the argument is the resolved view instance.</summary>
    event EventHandler<object>? Navigated;

    /// <summary>The currently displayed view, or null before the first navigation.</summary>
    object? CurrentView { get; }

    /// <summary>The key of the currently displayed page, or null before the first navigation.</summary>
    string? CurrentPageKey { get; }

    /// <summary>Navigates to the page registered under <paramref name="pageKey"/>.</summary>
    void NavigateTo(string pageKey);
}
