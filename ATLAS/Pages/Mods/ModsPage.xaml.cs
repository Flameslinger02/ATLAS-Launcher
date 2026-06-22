using System.Windows.Controls;

namespace Atlas.Pages.Mods;

/// <summary>
/// The global Mods hub: a <b>Library</b> tab (mod registry + SteamCMD acquire/update + deploy tools) and a
/// <b>Presets</b> tab (the composed Mod Presets view). The view model is a singleton, so the library is
/// refreshed each time the page is shown — unless an operation is in flight (which would clobber selection).
/// </summary>
public partial class ModsPage : UserControl
{
    private readonly ModsViewModel _viewModel;

    public ModsPage(ModsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += (_, _) =>
        {
            if (!_viewModel.IsWorking) _viewModel.LoadLibraryCommand.Execute(null);
        };
    }
}
