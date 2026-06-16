using System.Windows;
using Atlas.Core.Models;
using Atlas.Core.Services;

namespace Atlas.Pages.Profiles;

/// <summary>
/// Opens the modal profile editor. Abstracted so the view model does not create windows directly.
/// </summary>
public interface IProfileEditorLauncher
{
    /// <summary>Edits <paramref name="profile"/> in place. Returns true if the user saved.</summary>
    Task<bool> EditAsync(ServerProfile profile, bool isNew);
}

/// <inheritdoc cref="IProfileEditorLauncher"/>
public sealed class ProfileEditorLauncher : IProfileEditorLauncher
{
    private readonly IDialogService _dialogs;
    private readonly IModPresetService _modPresets;
    private readonly IConfigGeneratorService _configGen;

    public ProfileEditorLauncher(IDialogService dialogs, IModPresetService modPresets, IConfigGeneratorService configGen)
    {
        _dialogs = dialogs;
        _modPresets = modPresets;
        _configGen = configGen;
    }

    public async Task<bool> EditAsync(ServerProfile profile, bool isNew)
    {
        var presets = await _modPresets.GetAllPresetsAsync();
        var app = Application.Current;
        if (app is null) return false;

        return await app.Dispatcher.InvokeAsync(() =>
        {
            var vm = new ProfileEditorViewModel(profile, isNew, _dialogs, presets, _configGen);
            var dialog = new ProfileEditorDialog(vm)
            {
                Owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow
            };
            return dialog.ShowDialog() == true;
        }).Task;
    }
}
