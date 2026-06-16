using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Atlas.Core.Services;

/// <summary>
/// Code-built, theme-aware implementation of <see cref="IDialogService"/>. Phase 1 provides
/// confirm / info / error / prompt dialogs and file/folder pickers. The generic content-dialog
/// host (<see cref="ShowDialogAsync{T}"/>) is implemented in Phase 15.
/// </summary>
public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "No")
        => RunOnUiAsync(() => ShowChoice(title, message, confirmText, cancelText));

    public Task ShowErrorAsync(string title, string message)
        => RunOnUiAsync(() => ShowChoice(title, message, "OK", null));

    public Task ShowInfoAsync(string title, string message)
        => RunOnUiAsync(() => ShowChoice(title, message, "OK", null));

    public Task<string?> PromptAsync(string title, string label, string defaultValue = "", bool isPassword = false)
        => RunOnUiAsync(() => ShowPrompt(title, label, defaultValue, isPassword, null));

    public Task<string?> PromptWithValidationAsync(string title, string label, Func<string, string?> validate)
        => RunOnUiAsync(() => ShowPrompt(title, label, string.Empty, false, validate));

    public Task<string?> BrowseFileAsync(string title, string filter, string? initialDir = null)
        => RunOnUiAsync<string?>(() =>
        {
            var dlg = new OpenFileDialog { Title = title, Filter = filter };
            if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                dlg.InitialDirectory = initialDir;
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        });

    public Task<string?> BrowseFolderAsync(string title, string? initialDir = null)
        => RunOnUiAsync<string?>(() =>
        {
            var dlg = new OpenFolderDialog { Title = title };
            if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                dlg.InitialDirectory = initialDir;
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        });

    public Task<string?> SaveFileAsync(string title, string filter, string defaultFileName = "")
        => RunOnUiAsync<string?>(() =>
        {
            var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        });

    public Task<T?> ShowDialogAsync<T>(string title, object viewModel) where T : class
        => RunOnUiAsync<T?>(() =>
        {
            // Host the view model in a themed modal. A registered DataTemplate (if any) renders the view;
            // otherwise the ContentControl shows the VM's ToString(). The VM may implement IDialogHostAware
            // to close the window with a typed result; a Close button is always available as a fallback.
            var window = BuildWindow(title);
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.MinWidth = 360;

            T? result = null;
            EventHandler<object?>? handler = null;

            var panel = new StackPanel();
            panel.Children.Add(MakeTitle(title));
            panel.Children.Add(new ContentControl { Content = viewModel });

            var close = MakeButton("Close", true);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 16, 0, 0);
            close.Click += (_, _) => window.Close();
            panel.Children.Add(close);

            var aware = viewModel as IDialogHostAware;
            if (aware is not null)
            {
                handler = (_, payload) => { result = payload as T; window.Close(); };
                aware.CloseRequested += handler;
            }

            window.Content = WrapInBorder(panel);
            try { window.ShowDialog(); }
            finally { if (aware is not null && handler is not null) aware.CloseRequested -= handler; }
            return result;
        });

    // ---------------------------------------------------------------- helpers

    private static Task<TResult> RunOnUiAsync<TResult>(Func<TResult> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return Task.FromResult(func());
        return dispatcher.InvokeAsync(func).Task;
    }

    private static bool ShowChoice(string title, string message, string primaryText, string? secondaryText)
    {
        var window = BuildWindow(title);
        var result = false;

        var panel = new StackPanel();
        panel.Children.Add(MakeTitle(title));
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetBrush("Atlas.Brush.TextSecondary", "#888888"),
            Margin = new Thickness(0, 0, 0, 20)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        if (secondaryText is not null)
        {
            var cancel = MakeButton(secondaryText, false);
            cancel.Click += (_, _) => { result = false; window.Close(); };
            buttons.Children.Add(cancel);
        }
        var ok = MakeButton(primaryText, true);
        ok.Click += (_, _) => { result = true; window.Close(); };
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        window.Content = WrapInBorder(panel);
        window.ShowDialog();
        return result;
    }

    private static string? ShowPrompt(string title, string label, string defaultValue, bool isPassword,
        Func<string, string?>? validate)
    {
        var window = BuildWindow(title);
        string? result = null;

        var panel = new StackPanel();
        panel.Children.Add(MakeTitle(title));
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetBrush("Atlas.Brush.TextSecondary", "#888888"),
            Margin = new Thickness(0, 0, 0, 6)
        });

        TextBox? textBox = null;
        PasswordBox? passwordBox = null;
        Control input;
        if (isPassword) { passwordBox = new PasswordBox(); input = passwordBox; }
        else { textBox = new TextBox { Text = defaultValue }; input = textBox; }
        input.Padding = new Thickness(8, 6, 8, 6);
        input.Background = GetBrush("Atlas.Brush.Surface", "#1A1A1A");
        input.Foreground = GetBrush("Atlas.Brush.TextPrimary", "#E8E8E8");
        input.BorderBrush = GetBrush("Atlas.Brush.Border", "#2A2A2A");
        panel.Children.Add(input);

        var error = new TextBlock
        {
            Foreground = GetBrush("Atlas.Brush.Error", "#FF2222"),
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var cancel = MakeButton("Cancel", false);
        cancel.Click += (_, _) => { result = null; window.Close(); };
        var ok = MakeButton("OK", true);
        ok.Click += (_, _) =>
        {
            var value = isPassword ? passwordBox!.Password : textBox!.Text;
            if (validate is not null)
            {
                var err = validate(value);
                if (err is not null) { error.Text = err; error.Visibility = Visibility.Visible; return; }
            }
            result = value;
            window.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        window.Content = WrapInBorder(panel);
        input.Loaded += (_, _) => input.Focus();
        window.ShowDialog();
        return result;
    }

    private static Window BuildWindow(string title) => new()
    {
        Title = title,
        Width = 440,
        SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        Background = GetBrush("Atlas.Brush.SurfaceRaised", "#232323"),
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                ?? Application.Current?.MainWindow
    };

    private static Border WrapInBorder(UIElement child) => new()
    {
        BorderBrush = GetBrush("Atlas.Brush.Border", "#2A2A2A"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(24),
        Child = child
    };

    private static TextBlock MakeTitle(string title) => new()
    {
        Text = title,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Foreground = GetBrush("Atlas.Brush.TextPrimary", "#E8E8E8"),
        Margin = new Thickness(0, 0, 0, 12)
    };

    private static Button MakeButton(string text, bool primary) => new()
    {
        Content = text,
        MinWidth = 88,
        Margin = new Thickness(8, 0, 0, 0),
        Padding = new Thickness(16, 6, 16, 6),
        Foreground = GetBrush(primary ? "Atlas.Brush.TextPrimary" : "Atlas.Brush.TextSecondary",
            primary ? "#E8E8E8" : "#888888"),
        Background = primary ? GetBrush("Atlas.Brush.Accent", "#CC2200") : GetBrush("Atlas.Brush.Surface", "#1A1A1A"),
        BorderBrush = GetBrush("Atlas.Brush.Border", "#2A2A2A"),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        IsDefault = primary
    };

    private static Brush GetBrush(string key, string fallbackHex)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return (Brush)(new BrushConverter().ConvertFromString(fallbackHex) ?? Brushes.Gray);
    }
}
