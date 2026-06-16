using System.Windows;
using MaterialDesignThemes.Wpf;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="IThemeService"/>
public sealed class ThemeService : IThemeService
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    // Relative pack paths to the two palette dictionaries (matched by suffix against a merged dict's Source).
    private const string DarkSource = "Resources/Themes/AtlasTheme.Dark.xaml";
    private const string LightSource = "Resources/Themes/AtlasTheme.Light.xaml";

    public string CurrentTheme { get; private set; } = Dark;

    public void Apply(string theme)
    {
        var normalized = string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

        var app = Application.Current;
        if (app is null)
        {
            // No Application yet (e.g. unit harness) — record intent so callers see the right value.
            CurrentTheme = normalized;
            return;
        }

        void DoApply()
        {
            try
            {
                SwapPalette(app, normalized);
                SwapMaterialDesignBaseTheme(normalized);
                CurrentTheme = normalized;
                Log.Information("Applied {Theme} theme.", normalized);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply {Theme} theme.", normalized);
            }
        }

        if (app.Dispatcher.CheckAccess()) DoApply();
        else app.Dispatcher.Invoke(DoApply);
    }

    /// <summary>Replaces the active Atlas palette dictionary so DynamicResource brushes re-resolve.</summary>
    private static void SwapPalette(Application app, string theme)
    {
        var targetSource = theme == Light ? LightSource : DarkSource;
        var dictionaries = app.Resources.MergedDictionaries;

        // Find whichever palette dictionary is currently merged (Dark or Light).
        var existing = dictionaries.FirstOrDefault(d =>
            d.Source is not null &&
            (d.Source.OriginalString.EndsWith(DarkSource, StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith(LightSource, StringComparison.OrdinalIgnoreCase)));

        // Already on the requested palette → nothing to do (idempotent).
        if (existing?.Source is not null &&
            existing.Source.OriginalString.EndsWith(targetSource, StringComparison.OrdinalIgnoreCase))
            return;

        var replacement = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{targetSource}", UriKind.Absolute)
        };

        if (existing is not null)
        {
            // Replace in place at the old palette's index so merge ordering is preserved. (Consumers use
            // DynamicResource, which re-resolves regardless of order, but keeping the slot stable is tidy.)
            var index = dictionaries.IndexOf(existing);
            dictionaries.Remove(existing);
            dictionaries.Insert(index, replacement);
        }
        else
        {
            dictionaries.Add(replacement);
        }
    }

    /// <summary>Keeps MaterialDesign control chrome (text boxes, combo boxes, etc.) in step with the palette.</summary>
    private static void SwapMaterialDesignBaseTheme(string theme)
    {
        try
        {
            var helper = new PaletteHelper();
            var mdTheme = helper.GetTheme();
            mdTheme.SetBaseTheme(theme == Light ? BaseTheme.Light : BaseTheme.Dark);
            helper.SetTheme(mdTheme);
        }
        catch (Exception ex)
        {
            // The Atlas palette is the primary driver of the UI; MD chrome is secondary. Don't fail the swap.
            Log.Warning(ex, "Could not switch the MaterialDesign base theme.");
        }
    }
}
