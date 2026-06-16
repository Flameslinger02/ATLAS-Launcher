namespace Atlas.Core.Services;

/// <summary>
/// Provides themed, modal dialogs and file/folder pickers. All dialogs are custom WPF windows
/// styled with the ATLAS theme — no <c>MessageBox.Show</c> and no WinForms dialogs.
/// </summary>
public interface IDialogService
{
    /// <summary>Shows a confirmation dialog. Returns true if the user confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "No");

    /// <summary>Prompts for a single line of text (or password). Returns null if cancelled.</summary>
    Task<string?> PromptAsync(string title, string label, string defaultValue = "", bool isPassword = false);

    /// <summary>
    /// Prompts for text with live validation. <paramref name="validate"/> returns an error message
    /// (shown inline) or null when valid. Returns null if cancelled.
    /// </summary>
    Task<string?> PromptWithValidationAsync(string title, string label, Func<string, string?> validate);

    /// <summary>Shows an error dialog.</summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>Shows an informational dialog.</summary>
    Task ShowInfoAsync(string title, string message);

    /// <summary>Opens a file picker. Returns the selected path or null if cancelled.</summary>
    Task<string?> BrowseFileAsync(string title, string filter, string? initialDir = null);

    /// <summary>Opens a folder picker. Returns the selected path or null if cancelled.</summary>
    Task<string?> BrowseFolderAsync(string title, string? initialDir = null);

    /// <summary>Opens a save-file picker. Returns the chosen path or null if cancelled.</summary>
    Task<string?> SaveFileAsync(string title, string filter, string defaultFileName = "");

    /// <summary>Hosts a custom view model in a modal dialog and returns a typed result.</summary>
    Task<T?> ShowDialogAsync<T>(string title, object viewModel) where T : class;
}
