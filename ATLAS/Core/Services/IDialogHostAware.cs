namespace Atlas.Core.Services;

/// <summary>
/// Optional contract a view model can implement so <see cref="IDialogService.ShowDialogAsync{T}"/> can close
/// its hosting window with a typed result. The payload is cast to the dialog's result type (null if it doesn't match).
/// </summary>
public interface IDialogHostAware
{
    /// <summary>Raised by the view model to request its host dialog close, carrying the result payload (or null).</summary>
    event EventHandler<object?>? CloseRequested;
}
