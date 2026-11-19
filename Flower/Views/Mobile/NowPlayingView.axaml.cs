using System;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;

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
        LayoutUpdated += (_, _) => SizeBackdrop();
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

    // The sheet is washed in the cover's colour, and with no cover in the
    // colour ArtBackdrop falls back to - the theme's pressed accent, put
    // through the same darkening - so it is always a colour white reads on,
    // and everything on it is drawn the one way. Resolved for the theme in
    // use, and again when it changes, since the accent is the theme's.
    // The blurred cover over it is the album page's (ArtBackdrop.Soft).
    private void ApplyCover()
    {
        Background = ArtBackdrop.SheetBrush(ArtBackdrop.DominantColor(_playing?.AlbumArt));
        BackdropImage.Source = ArtBackdrop.Soft(_playing?.AlbumArt);
    }

    // The blurred cover reaches down to whichever ends lower, the cover or
    // the names - the names under it upright, the cover when the phone is
    // turned and they sit beside it - so it fades out under the words.
    private void SizeBackdrop()
    {
        var cover = CoverFrame.TranslatePoint(new Point(0, CoverFrame.Bounds.Height), this)?.Y ?? 0;
        var names = Names.TranslatePoint(new Point(0, Names.Bounds.Height), this)?.Y ?? 0;
        var height = Math.Max(cover, names);
        if (Backdrop.Height != height)
            Backdrop.Height = height;
    }
}
