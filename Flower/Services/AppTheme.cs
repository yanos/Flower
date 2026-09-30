using System.Linq;

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

using Flower.Persistence;

namespace Flower.Services;

// Translates the user's Settings > Appearance choice into Avalonia's own
// ThemeVariant and applies it. Application.RequestedThemeVariant is a
// reactive property - every DynamicResource-driven color in the app (see
// Theme.axaml) repaints immediately when it changes, no restart needed.
// Called once at startup (App.axaml.cs, before any window is created, so the
// very first frame already renders in the right variant) and again whenever
// MainViewModel.ThemePreference changes.
public static class AppTheme
{
    // Hands FluentTheme the app's palette (Theme.axaml's Palette* colours),
    // so what Fluent draws for itself - check boxes, toggles, menus, its own
    // buttons and scrollbars - is drawn in the same colours as everything
    // else. Through its Palettes, which is what Fluent actually reads: keys of
    // the same names in the app's resources reached some of its brushes and
    // not others (a checked box stayed Fluent's blue in the light theme).
    // Once at startup, before any window, from the same colours the app's own
    // brushes use - so replacing a palette colour and calling this again is
    // all it takes for Fluent to follow.
    public static void ApplyPalette(Application app)
    {
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is not { } fluent)
            return;

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            fluent.Palettes[variant] = PaletteFor(app, variant);
    }

    private static ColorPaletteResources PaletteFor(Application app, ThemeVariant variant)
    {
        Color C(string key) =>
            app.TryGetResource(key, variant, out var value) && value is Color color ? color : default;

        var background = C("PaletteBackground");
        var surface = C("PaletteSurface");
        var divider = C("PaletteDivider");
        var textPrimary = C("PaletteTextPrimary");
        var textSecondary = C("PaletteTextSecondary");
        var iconInactive = C("PaletteIconInactive");
        var iconDisabled = C("PaletteIconDisabled");

        return new ColorPaletteResources
        {
            Accent = C("PaletteAccent"),
            AltHigh = background,
            AltMediumHigh = surface,
            AltMedium = surface,
            AltMediumLow = divider,
            AltLow = divider,
            BaseHigh = textPrimary,
            BaseMediumHigh = textSecondary,
            BaseMedium = iconInactive,
            BaseMediumLow = iconDisabled,
            BaseLow = divider,
            ChromeHigh = divider,
            ChromeMedium = surface,
            ChromeMediumLow = surface,
            ChromeLow = background,
            ChromeAltLow = textSecondary,
            ChromeDisabledHigh = divider,
            ChromeDisabledLow = iconDisabled,
            ChromeGray = iconInactive,
            ListLow = surface,
            ListMedium = divider,
            RegionColor = background,
            ErrorText = C("PaletteError"),
        };
    }

    public static void Apply(AppThemePreference preference)
    {
        if (Application.Current is not { } app)
            return;

        app.RequestedThemeVariant = preference switch
        {
            AppThemePreference.Light => ThemeVariant.Light,
            AppThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
