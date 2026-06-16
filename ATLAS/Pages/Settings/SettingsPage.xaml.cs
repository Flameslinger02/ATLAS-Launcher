using System.Windows.Controls;

namespace Atlas.Pages.Settings;

/// <summary>
/// Settings view. The Steam API key is entered via a <see cref="PasswordBox"/> (never bound to a property);
/// the Save and Test-API-Key buttons read it here and hand it to the view model.
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void OnSaveClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var enteredKey = ApiKeyBox.Password;
        ApiKeyBox.Clear();   // never keep the plaintext key in the UI after handing it off
        await _viewModel.ApplyAndSaveAsync(enteredKey);
    }

    private async void OnTestApiKeyClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var enteredKey = ApiKeyBox.Password;
        ApiKeyBox.Clear();   // never keep the plaintext key in the UI after handing it off
        await _viewModel.TestApiKeyAsync(enteredKey);
    }
}
