using Flower.Persistence;

namespace Flower.ViewModels.Mobile;

// ── Theme ─────────────────────────────────────────────────────────────
//
// Settings' Follow System / Light / Dark, the same preference desktop's
// Settings > General picks (AppSettings.ThemePreference), applied and saved by
// MainViewModel.ThemePreference. Three bools rather than one enum because the
// phone shows it as three radio buttons, and a radio button binds IsChecked:
// each setter acts only on being ticked, since ticking one is what unticks
// the other two.
public partial class MobileMainViewModel
{
    public bool ThemeFollowsSystem
    {
        get => Main.ThemePreference == AppThemePreference.System;
        set => SetThemeIfTicked(value, AppThemePreference.System);
    }

    public bool ThemeIsLight
    {
        get => Main.ThemePreference == AppThemePreference.Light;
        set => SetThemeIfTicked(value, AppThemePreference.Light);
    }

    public bool ThemeIsDark
    {
        get => Main.ThemePreference == AppThemePreference.Dark;
        set => SetThemeIfTicked(value, AppThemePreference.Dark);
    }

    private void SetThemeIfTicked(bool ticked, AppThemePreference preference)
    {
        if (!ticked || Main.ThemePreference == preference)
            return;

        Main.ThemePreference = preference;
        OnPropertyChanged(nameof(ThemeFollowsSystem));
        OnPropertyChanged(nameof(ThemeIsLight));
        OnPropertyChanged(nameof(ThemeIsDark));
    }
}
