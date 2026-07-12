using Avalonia;
using Avalonia.Styling;

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
