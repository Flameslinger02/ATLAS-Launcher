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

    /// <summary>
    /// Asks the current view whether it's OK to navigate away. If the current view's DataContext
    /// implements <see cref="INavigationGuard"/>, its <see cref="INavigationGuard.CanLeaveAsync"/> is
    /// awaited; otherwise returns true. Call this before changing the active profile / navigating.
    /// </summary>
    Task<bool> ConfirmLeaveAsync();
}

/// <summary>Implemented by a page's view model to veto/handle navigation away (e.g. unsaved edits).</summary>
public interface INavigationGuard
{
    /// <summary>Return true to allow leaving the page; false to cancel the navigation.</summary>
    Task<bool> CanLeaveAsync();
}
