using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Atlas.Core.Models;
using Atlas.Core.Services;
using Atlas.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atlas.Pages.ModPresets;

/// <summary>Mod preset library: create/edit/clone/delete presets, edit their mod lists, A3 import/export, apply to profile.</summary>
public partial class ModPresetsViewModel : BaseViewModel
{
    private readonly IModPresetService _presets;
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private readonly IModLibraryService _library;

    public ObservableCollection<ModPreset> Presets { get; } = new();
    public ObservableCollection<ArmaModEntry> Mods { get; } = new();

    [ObservableProperty] private ModPreset? _selectedPreset;
    [ObservableProperty] private ArmaModEntry? _selectedMod;

    public ModPresetsViewModel(
        IModPresetService presets, IProfileService profiles, IDialogService dialogs, IModLibraryService library)
    {
        _presets = presets;
        _profiles = profiles;
        _dialogs = dialogs;
        _library = library;
        Title = "Mod Presets";
        _ = LoadAsync();
    }

    partial void OnSelectedPresetChanged(ModPreset? value)
    {
        Mods.Clear();
        if (value is not null)
            foreach (var mod in value.Mods) Mods.Add(mod);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var selectedId = SelectedPreset?.Id;
            var all = await _presets.GetAllPresetsAsync();
            Presets.Clear();
            foreach (var p in all) Presets.Add(p);
            SelectedPreset = Presets.FirstOrDefault(p => p.Id == selectedId) ?? Presets.FirstOrDefault();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task NewPreset()
    {
        var name = await _dialogs.PromptAsync("New Mod Preset", "Preset name", "New Preset");
        if (string.IsNullOrWhiteSpace(name)) return;
        var created = await _presets.CreatePresetAsync(name, string.Empty, new List<ArmaModEntry>());
        await LoadAsync();
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == created.Id);
    }

    [RelayCommand]
    private async Task ClonePreset()
    {
        if (SelectedPreset is null) return;
        var name = await _dialogs.PromptAsync("Clone Preset", "New preset name", SelectedPreset.Name + " (copy)");
        if (string.IsNullOrWhiteSpace(name)) return;
        var clone = await _presets.ClonePresetAsync(SelectedPreset.Id, name);
        await LoadAsync();
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == clone.Id);
    }

    [RelayCommand]
    private async Task DeletePreset()
    {
        if (SelectedPreset is null) return;
        if (!await _dialogs.ConfirmAsync("Delete Preset",
                $"Delete preset '{SelectedPreset.Name}'? Profiles using it will revert to a manual mod list.",
                "Delete", "Cancel"))
            return;
        await _presets.DeletePresetAsync(SelectedPreset.Id);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SavePreset()
    {
        if (SelectedPreset is null) return;
        for (var i = 0; i < Mods.Count; i++) Mods[i].LoadOrder = i;
        SelectedPreset.Mods = Mods.ToList();
        await _presets.UpdatePresetAsync(SelectedPreset);
        await _dialogs.ShowInfoAsync("Saved", $"Preset '{SelectedPreset.Name}' saved.");
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ImportA3()
    {
        var path = await _dialogs.BrowseFileAsync("Import Arma 3 Launcher preset",
            "Arma 3 preset (*.html;*.htm)|*.html;*.htm|All files (*.*)|*.*");
        if (string.IsNullOrWhiteSpace(path)) return;
        var name = await _dialogs.PromptAsync("Import Preset", "New preset name",
            Path.GetFileNameWithoutExtension(path));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var imported = await _presets.ImportFromA3LauncherPresetAsync(path, name);
            await LoadAsync();
            SelectedPreset = Presets.FirstOrDefault(p => p.Id == imported.Id);
        }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Import failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportA3()
    {
        if (SelectedPreset is null) return;
        var path = await _dialogs.SaveFileAsync("Export to Arma 3 Launcher format",
            "Arma 3 preset (*.html)|*.html", SelectedPreset.Name + ".html");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await _presets.ExportToA3LauncherFormatAsync(SelectedPreset, path);
            await _dialogs.ShowInfoAsync("Export", $"Preset exported to:\n{path}");
        }
        catch (Exception ex) { await _dialogs.ShowErrorAsync("Export failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ApplyToActiveProfile()
    {
        if (SelectedPreset is null) return;
        var active = _profiles.ActiveProfile;
        if (active is null) { await _dialogs.ShowErrorAsync("No active profile", "Activate a profile first (Profiles page)."); return; }
        await _presets.ApplyPresetToProfileAsync(SelectedPreset.Id, active);
        await _profiles.UpdateProfileAsync(active);
        await _dialogs.ShowInfoAsync("Applied",
            $"Preset '{SelectedPreset.Name}' applied to profile '{active.Name}'.");
    }

    [RelayCommand]
    private async Task CreateFromActiveProfile()
    {
        var active = _profiles.ActiveProfile;
        if (active is null) { await _dialogs.ShowErrorAsync("No active profile", "Activate a profile first (Profiles page)."); return; }
        var name = await _dialogs.PromptAsync("New Preset from Profile", "Preset name", active.Name + " Mods");
        if (string.IsNullOrWhiteSpace(name)) return;
        var created = await _presets.CreatePresetFromProfileAsync(name, string.Empty, active);
        await LoadAsync();
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == created.Id);
    }

    /// <summary>Adds mods the library already knows about — no re-pasting Workshop IDs. Opens a
    /// multi-select picker over every library mod not yet in the preset.</summary>
    [RelayCommand]
    private async Task AddFromLibrary()
    {
        if (SelectedPreset is null) return;

        List<ArmaModEntry> library;
        try { library = await _library.GetAllModsAsync(); }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Add from Library", $"Could not load the mod library: {ex.Message}");
            return;
        }

        var present = new HashSet<string>(Mods.Select(ModKey), StringComparer.OrdinalIgnoreCase);
        var candidates = library.Where(m => !present.Contains(ModKey(m))).ToList();
        if (candidates.Count == 0)
        {
            await _dialogs.ShowInfoAsync("Add from Library",
                "Every library mod is already in this preset (or the library is empty — add mods on the Library tab).");
            return;
        }

        var picker = new Mods.LibraryModPickerWindow(candidates)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (picker.ShowDialog() != true) return;

        foreach (var mod in picker.SelectedMods)
        {
            // Copy the library row into a preset assignment (fresh per-assignment settings + load order).
            Mods.Add(new ArmaModEntry
            {
                ModId = mod.ModId,
                WorkshopId = mod.WorkshopId,
                Name = mod.Name,
                FolderName = mod.FolderName,
                LocalPath = mod.LocalPath,
                IsLocal = mod.IsLocal,
                Version = mod.Version,
                LoadOrder = Mods.Count,
            });
        }
    }

    /// <summary>Same mod-identity rule as the profile Mods tab: Workshop id, else folder name, else name.</summary>
    private static string ModKey(ArmaModEntry m) =>
        m.WorkshopId != 0 ? "w:" + m.WorkshopId
        : !string.IsNullOrWhiteSpace(m.FolderName) ? "f:" + m.FolderName.ToLowerInvariant()
        : "n:" + m.Name.ToLowerInvariant();

    [RelayCommand]
    private async Task AddByWorkshop()
    {
        if (SelectedPreset is null) return;
        var input = await _dialogs.PromptAsync("Add Workshop Mod", "Workshop URL or numeric ID", string.Empty);
        if (string.IsNullOrWhiteSpace(input)) return;
        var id = ParseWorkshopId(input);
        if (id == 0) { await _dialogs.ShowErrorAsync("Invalid input", "Could not find a Workshop ID."); return; }
        Mods.Add(new ArmaModEntry
        {
            WorkshopId = id,
            Name = $"Workshop {id}",
            FolderName = $"@{id}",
            LoadOrder = Mods.Count,
        });
    }

    [RelayCommand]
    private async Task AddLocal()
    {
        if (SelectedPreset is null) return;
        var folder = await _dialogs.BrowseFolderAsync("Select local mod folder (@ModName)");
        if (string.IsNullOrWhiteSpace(folder)) return;
        var folderName = new DirectoryInfo(folder).Name;
        Mods.Add(new ArmaModEntry
        {
            WorkshopId = 0,
            IsLocal = true,
            LocalPath = folder,
            Name = folderName.TrimStart('@'),
            FolderName = folderName.StartsWith('@') ? folderName : "@" + folderName,
            LoadOrder = Mods.Count,
        });
    }

    [RelayCommand]
    private void RemoveMod()
    {
        if (SelectedMod is not null) Mods.Remove(SelectedMod);
    }

    [RelayCommand]
    private void MoveUp()
    {
        var i = SelectedMod is null ? -1 : Mods.IndexOf(SelectedMod);
        if (i > 0) { Mods.Move(i, i - 1); }
    }

    [RelayCommand]
    private void MoveDown()
    {
        var i = SelectedMod is null ? -1 : Mods.IndexOf(SelectedMod);
        if (i >= 0 && i < Mods.Count - 1) { Mods.Move(i, i + 1); }
    }

    [RelayCommand]
    private void OpenInWorkshop()
    {
        if (SelectedMod is null || SelectedMod.WorkshopId == 0) return;
        var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={SelectedMod.WorkshopId}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore launch failures */ }
    }

    private static ulong ParseWorkshopId(string input)
    {
        var match = Regex.Match(input, @"\d{6,}");
        return match.Success && ulong.TryParse(match.Value, out var id) ? id : 0;
    }
}
