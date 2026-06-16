using CommunityToolkit.Mvvm.ComponentModel;

namespace Atlas.Core.ViewModels;

/// <summary>
/// Base class for all page/dialog view models. Provides change notification (via
/// <see cref="ObservableObject"/>), a shared busy flag, a title, and navigation lifecycle hooks.
/// </summary>
public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Called by the navigation host when this view model becomes the active page.</summary>
    public virtual void OnNavigatedTo() { }

    /// <summary>Called by the navigation host just before navigating away from this page.</summary>
    public virtual void OnNavigatedFrom() { }
}
