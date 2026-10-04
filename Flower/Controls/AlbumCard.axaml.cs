using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

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

    public event System.EventHandler<RoutedEventArgs> MenuRequested
    {
        add => AddHandler(MenuRequestedEvent, value);
        remove => RemoveHandler(MenuRequestedEvent, value);
    }

    public AlbumCard()
    {
        InitializeComponent();
    }

    private void Menu_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (MenuCommand is { } command && command.CanExecute(Tile))
            command.Execute(Tile);
        RaiseEvent(new RoutedEventArgs(MenuRequestedEvent, this));
    }
}
