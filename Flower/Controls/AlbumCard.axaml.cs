using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Controls;

// An album as a tile, on both heads - see the markup for what it draws and
// what it leaves to its host.
public partial class AlbumCard : UserControl
{
    public static readonly StyledProperty<AlbumTileViewModel?> TileProperty =
        AvaloniaProperty.Register<AlbumCard, AlbumTileViewModel?>(nameof(Tile));

    public static readonly StyledProperty<bool> ShowsProgressProperty =
        AvaloniaProperty.Register<AlbumCard, bool>(nameof(ShowsProgress));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<AlbumCard, double>(nameof(Progress));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<AlbumCard, string?>(nameof(Detail));

    public static readonly StyledProperty<ICommand?> MenuCommandProperty =
        AvaloniaProperty.Register<AlbumCard, ICommand?>(nameof(MenuCommand));

    public static readonly StyledProperty<ICommand?> DismissCommandProperty =
        AvaloniaProperty.Register<AlbumCard, ICommand?>(nameof(DismissCommand));

    public static readonly StyledProperty<object?> DismissParameterProperty =
        AvaloniaProperty.Register<AlbumCard, object?>(nameof(DismissParameter));

    public static readonly StyledProperty<string?> DismissTipProperty =
        AvaloniaProperty.Register<AlbumCard, string?>(nameof(DismissTip));

    // The "..." was pressed. Bubbles, so a view hosting many tiles handles it
    // once rather than per tile; the tile is the event's source.
    public static readonly RoutedEvent<RoutedEventArgs> MenuRequestedEvent =
        RoutedEvent.Register<AlbumCard, RoutedEventArgs>(nameof(MenuRequested), RoutingStrategies.Bubble);

    public AlbumTileViewModel? Tile
    {
        get => GetValue(TileProperty);
        set => SetValue(TileProperty, value);
    }

    // Continue Playing's: the play badge on the cover and the bar under it.
    public bool ShowsProgress
    {
        get => GetValue(ShowsProgressProperty);
        set => SetValue(ShowsProgressProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    // A third line under the artist, where there is one (Continue Playing's
    // "Song 4 of 10 · 26 min left").
    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    // Run with the tile when the "..." is pressed.
    public ICommand? MenuCommand
    {
        get => GetValue(MenuCommandProperty);
        set => SetValue(MenuCommandProperty, value);
    }

    // The round x on the cover, shown only where there is one to run: Continue
    // Playing's tiles, on both heads, take the album off the shelf with it
    // (HomeViewModel.ForgetCommand, the parameter its item), and Recently
    // Played's the same (HideFromRecentlyPlayedCommand, the parameter the tile).
    public ICommand? DismissCommand
    {
        get => GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    public object? DismissParameter
    {
        get => GetValue(DismissParameterProperty);
        set => SetValue(DismissParameterProperty, value);
    }

    // What the x says it does - which shelf it takes the album off.
    public string? DismissTip
    {
        get => GetValue(DismissTipProperty);
        set => SetValue(DismissTipProperty, value);
    }

    public event System.EventHandler<RoutedEventArgs> MenuRequested
    {
        add => AddHandler(MenuRequestedEvent, value);
        remove => RemoveHandler(MenuRequestedEvent, value);
    }

    // The tile, followed while the card is on screen - see ModelWatch.
    private readonly ModelWatch _tile;

    public AlbumCard()
    {
        InitializeComponent();
        // A tile is a DownloadIndicatorViewModel, whose spinner turns every
        // frame of a download; nothing here draws it.
        _tile = new ModelWatch(this, Show, ignoring: nameof(DownloadIndicatorViewModel.SpinAngle));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TileProperty)
            _tile.Watch(Tile);
        else if (change.Property == ShowsProgressProperty || change.Property == ProgressProperty || change.Property == DetailProperty)
            ShowProgress();
        else if (change.Property == DismissCommandProperty)
            DismissButton.IsVisible = DismissCommand != null;
        else if (change.Property == DismissTipProperty)
            ToolTip.SetTip(DismissButton, DismissTip);
    }

    private void Show()
    {
        var tile = Tile;
        Card.Classes.Set("dimmed", tile?.IsUnavailable == true);
        Art.AlbumArt = tile?.AlbumArt;
        AlbumName.Text = tile?.Name;
        ArtistName.Text = tile?.Artist;
    }

    private void ShowProgress()
    {
        Badge.IsVisible = ShowsProgress;
        ProgressLine.IsVisible = ShowsProgress;
        ProgressLine.Value = Progress;
        DetailLine.Text = Detail;
        DetailLine.IsVisible = !string.IsNullOrEmpty(Detail);
    }

    private void Dismiss_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DismissCommand is { } command && command.CanExecute(DismissParameter))
            command.Execute(DismissParameter);
    }

    private void Menu_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (MenuCommand is { } command && command.CanExecute(Tile))
            command.Execute(Tile);
        RaiseEvent(new RoutedEventArgs(MenuRequestedEvent, this));
    }
}
