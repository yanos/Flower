using System;
using System.Collections.Generic;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using CommunityToolkit.Mvvm.DependencyInjection;

using Material.Icons;
using Material.Icons.Avalonia;

using Flower.ViewModels;

namespace Flower.Controls;

public partial class TrackRowControl : UserControl
{
    private readonly ColumnManager _columnManager;

    // Store actual handler delegates so we can remove them properly
    private readonly List<(MusicColumnDefinition Col, PropertyChangedEventHandler Handler)> _widthSubs = new();

    // The ColumnManager is passed in by MusicListPanel, which pools these and
    // already has the instance MusicListView handed it - see
    // docs/ARCHITECTURE-REVIEW.md Tier 2.3. The fallback covers the XAML
    // previewer, which constructs this control with no arguments.
    // Explicitly parameterless as well as the real one below: Avalonia's XAML
    // compiler resolves a constructor by arity and does not consider optional
    // parameters, so a single (ColumnManager? = null) overload fails the build
    // of this control's own .axaml.
    public TrackRowControl() : this(null) { }

    public TrackRowControl(ColumnManager? columnManager)
    {
        InitializeComponent();
        _columnManager = columnManager ?? Ioc.Default.GetService<ColumnManager>()!;
        // The download button follows the row itself, and is the only part
        // that draws its spinner.
        _row = new ModelWatch(this, Show, ignoring: nameof(DownloadIndicatorViewModel.SpinAngle));
        DataContextChanged += (_, _) =>
        {
            if (CellsPanel.Children.Count == 0)
                BuildCells();
            _row.Watch(DataContext as TrackRowViewModel);
        };
    }

    private readonly ModelWatch _row;

    // Each data cell's text and what it shows, rebuilt with the cells.
    private readonly List<(RowText Text, Func<TrackRowViewModel, string?> Value)> _cells = new();

    // Whether the art well is there at all (ColumnManager.ShowAlbumArt).
    private bool _showsArt;

    // Everything the row shows, read off the song whatever changed - see
    // ModelWatch. Called again for every song the panel hands this row.
    private void Show()
    {
        if (DataContext is not TrackRowViewModel row)
            return;

        Classes.Set("playing", row.IsCurrentlyPlaying);
        Classes.Set("played", row.IsPlayed);
        SelectionBorder.IsVisible = row.IsSelected;
        UpdateHighlighted();
        CellsPanel.Classes.Set("dimmed", row.IsUnavailable);
        ArtCell.Classes.Set("dimmed", row.IsAlbumGroupUnavailable);

        // Only the first row of an album run draws the art, which spans down
        // over the rest; asking a hidden well for its art would load it.
        ArtCell.IsVisible = _showsArt && row.IsFirstInAlbumGroup;
        ArtImage.AlbumArt = ArtCell.IsVisible ? row.AlbumArt : null;

        foreach (var (text, value) in _cells)
            text.Text = value(row);
    }

    // The three backgrounds' CornerRadius in the XAML.
    private const double HighlightRadius = 8;

    // Squares the backgrounds' top and/or bottom corners where the lit row
    // above or below touches this one, so a run of lit rows - picked, playing
    // or pointed at - reads as one shape rounded only at its ends. All three
    // alike, since whichever is on top is the one that shows. See
    // MusicListPanel.RefreshHighlightShape.
    public void JoinHighlight(bool above, bool below)
    {
        double top = above ? 0 : HighlightRadius;
        double bottom = below ? 0 : HighlightRadius;
        var corners = new CornerRadius(top, top, bottom, bottom);
        PlayingBorder.CornerRadius = corners;
        HoverBorder.CornerRadius = corners;
        SelectionBorder.CornerRadius = corners;
    }

    // The row went lit or dark - picked, playing or pointed at, or not - so
    // its neighbours' shapes may need to change with it.
    public event EventHandler? HighlightChanged;

    private bool _highlighted;

    private void UpdateHighlighted()
    {
        bool lit = IsPointerOver
            || DataContext is TrackRowViewModel { IsSelected: true } or TrackRowViewModel { IsCurrentlyPlaying: true };
        if (lit == _highlighted)
            return;
        _highlighted = lit;
        HighlightChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPointerOverProperty)
            UpdateHighlighted();
    }

    // The row's own download icon was clicked - forwarded up through
    // MusicListPanel to MusicListView, whose consumer (MainView) is the one
    // holding the ViewModel that can actually start a download.
    public event EventHandler<TrackRowViewModel>? DownloadRequested;

    // The row's "..." was pressed - the menu a right-click on the row opens,
    // for anyone who does not know to right-click. Forwarded the same way.
    public event EventHandler<TrackRowViewModel>? MenuRequested;

    // The "..." cell's width, after the last column. MusicListView's header
    // keeps a spacer this wide, so the header and the cells end together.
    public const double MenuCellWidth = 36;

    private void OnColumnsChanged(object? sender, EventArgs e) => BuildCells();

    // Subscribed to the app-wide ColumnManager only while attached, so a row
    // thrown away with its list is not kept alive, and rebuilt, by it - see
    // MusicListView's own OnAttachedToVisualTree. The per-column width
    // subscriptions BuildCells makes go with it, and a row that was away
    // rebuilds its cells on return, having missed whatever changed.
    private bool _missedColumnChanges;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _columnManager.ColumnsChanged += OnColumnsChanged;
        if (_missedColumnChanges)
        {
            _missedColumnChanges = false;
            BuildCells();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _columnManager.ColumnsChanged -= OnColumnsChanged;
        foreach (var (col, handler) in _widthSubs)
            col.PropertyChanged -= handler;
        _widthSubs.Clear();
        _missedColumnChanges = true;
    }

    private void BuildCells()
    {
        // Unsubscribe width/visibility handlers from previous build
        foreach (var (col, handler) in _widthSubs)
            col.PropertyChanged -= handler;
        _widthSubs.Clear();

        ApplyArtWell();
        CellsPanel.Children.Clear();
        _cells.Clear();

        foreach (var col in _columnManager.VisibleColumns)
        {
            var border = new Border
            {
                Width             = col.Width,
                Padding           = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            // Capture border + col for the handler
            var capturedBorder = border;
            var capturedCol    = col;
            PropertyChangedEventHandler handler = (_, e) =>
            {
                if (e.PropertyName == nameof(MusicColumnDefinition.Width))
                    capturedBorder.Width = capturedCol.Width;
            };
            col.PropertyChanged += handler;
            _widthSubs.Add((col, handler));

            border.Child = BuildCellContent(col);
            CellsPanel.Children.Add(border);
        }

        CellsPanel.Children.Add(BuildMenuCell());
        Show();
    }

    // The phone's row ends in the same "..." (TrackRowTemplate).
    private Control BuildMenuCell()
    {
        var icon = new MaterialIcon { Kind = MaterialIconKind.DotsVertical, Width = 18, Height = 18, Opacity = 0.6, Classes = { "chrome" } };
        var button = new Button
        {
            Content = icon,
            Padding = new Thickness(4),
            Width = MenuCellWidth,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("pill");
        ToolTip.SetTip(button, "More");
        button.Click += (_, e) =>
        {
            e.Handled = true;
            if (DataContext is TrackRowViewModel row)
                MenuRequested?.Invoke(this, row);
        };
        return button;
    }

    // The art well's column collapses to zero when the art is turned off,
    // since the art deliberately overflows its cell (ClipToBounds="False") and
    // would otherwise still paint over the title. Show does the rest.
    private void ApplyArtWell()
    {
        RowGrid.ColumnDefinitions[0].Width = new GridLength(_columnManager.ArtColumnWidth);
        _showsArt = _columnManager.ShowAlbumArt;
    }

    // Cell content is built per-column from ColumnManager's runtime-configurable
    // definitions, each cell's text read off the row by CellTextFor - a
    // function per column rather than a binding path, so nothing here is found
    // by reflection: a trimmed build (Flower.Web, a NativeAOT head) keeps
    // whatever the functions below name, where a string path named properties
    // the linker had no reason to keep, and #, Duration, Plays, Added and Last
    // Played once rendered blank in a browser tab for exactly that reason.
    private Control BuildCellContent(MusicColumnDefinition col)
    {
        if (col.Id == "Title")
        {
            var grid = new Grid();
            // The title takes the width, the download button trails it at
            // the cell's right edge (the redesign's song rows: Never played,
            // Songs, Search). An Auto column rather than a fixed well: the
            // button only exists for a placeholder the paired server can
            // serve right now (see TrackDownloadButton, which hides itself
            // otherwise), and with it gone the title gets that room back.
            // Pinned to the right of a star column, the icons still line up
            // down the list whatever the title's length.
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var text = MakeText();
            _cells.Add((text, CellTextFor(col.Id)!));
            Grid.SetColumn(text, 0);

            var download = new TrackDownloadButton { Margin = new Thickness(4, 0, 4, 0) };
            download.DownloadRequested += (_, clicked) =>
            {
                if (clicked is TrackRowViewModel row)
                    DownloadRequested?.Invoke(this, row);
            };
            Grid.SetColumn(download, 1);

            grid.Children.Add(download);
            grid.Children.Add(text);
            return grid;
        }

        var tb = MakeText();
        if (CellTextFor(col.Id) is { } value)
            _cells.Add((tb, value));
        return tb;
    }

    // What each column's cell shows. Internal and separate from the cell it
    // feeds so the tests can hold every column ColumnManager offers to it: a
    // column with no entry here renders an empty cell forever, which is the
    // kind of thing only noticed once someone turns that column on. Static
    // lambdas, so each is one delegate for the life of the process.
    internal static Func<TrackRowViewModel, string?>? CellTextFor(string columnId) =>
        columnId switch
        {
            "Title"            => static row => row.Track.Title,
            "TrackNumber"      => static row => row.TrackNumberDisplay,
            "Artist"           => static row => row.Track.Artists,
            "Album"            => static row => row.Track.Album,
            "Year"             => static row => row.Track.Year,
            "Genre"            => static row => row.Track.Genre,
            "Duration"         => static row => row.DurationDisplay,
            "PlayCount"        => static row => row.PlayCountDisplay,
            "DateAdded"        => static row => row.DateAddedDisplay,
            "LastPlayed"       => static row => row.LastPlayedDisplay,
            "Composer"         => static row => row.Track.Composers,
            "Encoding"         => static row => row.Track.EncoderProfile,
            "SortTitle"        => static row => row.Track.TitleSort,
            "SortArtist"       => static row => row.Track.ArtistsSort,
            "SortAlbum"        => static row => row.Track.AlbumSort,
            "SortComposer"     => static row => row.Track.ComposersSort,
            "Compilation"      => static row => row.CompilationDisplay,
            "RememberPosition" => static row => row.RememberPositionDisplay,
            "ResumePosition"   => static row => row.ResumePositionDisplay,
            "SkipInShuffle"    => static row => row.SkipInShuffleDisplay,
            "VolumeAdjustment" => static row => row.VolumeAdjustmentDisplay,
            "AlbumArtist"      => static row => row.Track.AlbumArtists,
            "Subtitle"         => static row => row.Track.Subtitle,
            "Disc"             => static row => row.DiscDisplay,
            "Conductor"        => static row => row.Track.Conductor,
            "RemixedBy"        => static row => row.Track.RemixedBy,
            "Bpm"              => static row => row.BpmDisplay,
            "InitialKey"       => static row => row.Track.InitialKey,
            "Grouping"         => static row => row.Track.Grouping,
            "Publisher"        => static row => row.Track.Publisher,
            "Isrc"             => static row => row.Track.ISRC,
            "Comment"          => static row => row.Track.Comment,
            "Description"      => static row => row.Track.Description,
            "Copyright"        => static row => row.Track.Copyright,
            "Starred"          => static row => row.StarredDisplay,
            "Codec"            => static row => row.Track.Codec,
            "Bitrate"          => static row => row.BitrateDisplay,
            "SampleRate"       => static row => row.SampleRateDisplay,
            "Channels"         => static row => row.ChannelsDisplay,
            "BitDepth"         => static row => row.BitDepthDisplay,
            "Location"         => static row => row.Track.Path,
            _ => null,
        };

    private static RowText MakeText() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming      = TextTrimming.CharacterEllipsis,
        FontSize          = 14,
    };
}
