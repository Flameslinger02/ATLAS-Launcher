using System.Text.Json;
using Atlas.Core.Models;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="ISettingsService"/>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Settings { get; private set; } = new();

    public async Task LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(AppConstants.AppDataRoot);
            if (File.Exists(AppConstants.SettingsPath))
            {
                await using var fs = File.OpenRead(AppConstants.SettingsPath);
                var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(fs, JsonOptions).ConfigureAwait(false);
                Settings = loaded ?? new AppSettings();
                Log.Information("Settings loaded from {Path}", AppConstants.SettingsPath);
            }
            else
            {
                Settings = new AppSettings();
                await SaveInternalAsync().ConfigureAwait(false);
                Log.Information("No settings file found; created defaults at {Path}", AppConstants.SettingsPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load settings; falling back to defaults.");
            Settings = new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveInternalAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Settings = new AppSettings();
            await SaveInternalAsync().ConfigureAwait(false);
            Log.Warning("All settings reset to defaults.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to reset settings.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveInternalAsync()
    {
        Directory.CreateDirectory(AppConstants.AppDataRoot);
        // Write to a temp file then move, so a crash mid-write never corrupts settings.json.
        var tempPath = AppConstants.SettingsPath + ".tmp";
        await using (var fs = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(fs, Settings, JsonOptions).ConfigureAwait(false);
        }
        File.Copy(tempPath, AppConstants.SettingsPath, overwrite: true);
        File.Delete(tempPath);
    }
}
