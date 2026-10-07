using System;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile;

public partial class NowPlayingView : UserControl
{
    private CurrentlyPlayingControlViewModel? _playing;

    public NowPlayingView()
    {
        InitializeComponent();
        // The menu every screen has, holding the song playing's own entries.
        NowPlayingMenu.Sections = () => (DataContext as MobileMainViewModel)?.BuildNowPlayingMenu() ?? [];

        DataContextChanged += (_, _) => WatchCover((DataContext as MobileMainViewModel)?.CurrentlyPlaying);
        ActualThemeVariantChanged += (_, _) => ApplyCover();
    }

    private void WatchCover(CurrentlyPlayingControlViewModel? playing)
    {
        if (_playing != null)
            _playing.PropertyChanged -= Playing_PropertyChanged;
        _playing = playing;
        if (_playing != null)
            _playing.PropertyChanged += Playing_PropertyChanged;
        ApplyCover();
    }

    private void Playing_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CurrentlyPlayingControlViewModel.AlbumArt))
            ApplyCover();
    }

    // The sheet's colours are for a cover's: white on its colour. Without a
    // cover it is plain (the .plain styles in the XAML) - the page's
    // background, and each AppOnArt* brush, which every word and both sliders
    // here are drawn with, pointed back at the page's own: the text colour for
    // words, the accent for the sliders, the ring colour for the play button's
    // ring. Resolved for the theme in use, and again when it changes.
    private static readonly string[] OnArtKeys = ["PaletteOnArt", "AppOnArtBrush", "AppOnArtMutedBrush", "AppOnArtRingBrush"];

    // The page background while plain, followed live so a theme change
    // repaints it; dropped when a cover's wash takes over.
    private IDisposable? _pageBackground;

    private void ApplyCover()
    {
        var art = _playing?.AlbumArt;
        var plain = art is null;
        Classes.Set("plain", plain);

        // Set here rather than bound in XAML to a converter: a binding that
        // yields nothing leaves the sheet with no background at all - it
        // outranks any style that would have supplied one - and the screen
        // under the sheet showed through.
        _pageBackground?.Dispose();
        _pageBackground = null;
        if (art is null)
            _pageBackground = Bind(BackgroundProperty, this.GetResourceObservable("AppBackgroundBrush"));
        else
            Background = ArtBackdrop.SheetBrush(ArtBackdrop.DominantColor(art));

        foreach (var key in OnArtKeys)
            Resources.Remove(key);
        if (!plain)
            return;

        // Sliders' fill and thumb are built from PaletteOnArt in this view's
        // own resources, so a local colour reaches them.
        if (Find("PaletteAccent") is Color accent)
            Resources["PaletteOnArt"] = accent;
        if (Find("AppTextPrimaryBrush") is IBrush text)
            Resources["AppOnArtBrush"] = text;
        if (Find("AppTextDisabledBrush") is IBrush muted)
            Resources["AppOnArtMutedBrush"] = muted;
        if (Find("AppPrimaryBorderBrush") is IBrush ring)
            Resources["AppOnArtRingBrush"] = ring;
    }

    private object? Find(string key) =>
        Application.Current?.TryGetResource(key, ActualThemeVariant, out var value) == true ? value : null;
}
