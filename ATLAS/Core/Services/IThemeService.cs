namespace Atlas.Core.Services;

/// <summary>
/// Applies the ATLAS color palette ("Dark" / "Light") at runtime by swapping the active palette
/// dictionary in <c>App.Current.Resources.MergedDictionaries</c> (and the MaterialDesign base theme).
/// Because every consumer references <c>Atlas.Brush.*</c> via <c>DynamicResource</c>, the whole UI
/// recolors instantly with no restart.
/// </summary>
public interface IThemeService
{
    /// <summary>The currently applied theme name ("Dark" or "Light").</summary>
    string CurrentTheme { get; }

    /// <summary>
    /// Applies <paramref name="theme"/> ("Dark" or "Light"; anything else falls back to "Dark").
    /// Idempotent and safe to call on the UI thread during startup. Does not persist settings.
    /// </summary>
    void Apply(string theme);
}
