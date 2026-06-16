using System.Windows.Controls;

namespace Atlas.Pages.DiscordBot;

/// <summary>Discord Bot config view. The token is read from the PasswordBox and handed to the VM on Connect
/// (never bound, never persisted plaintext), then cleared from the box.</summary>
public partial class DiscordBotPage : UserControl
{
    private readonly DiscordBotViewModel _viewModel;

    public DiscordBotPage(DiscordBotViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void OnConnectClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var token = TokenBox.Password;
        TokenBox.Clear();
        await _viewModel.ConnectAsync(token);
    }
}
