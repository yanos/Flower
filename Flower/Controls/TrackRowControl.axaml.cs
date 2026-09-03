using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

using CommunityToolkit.Mvvm.DependencyInjection;

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
        _columnManager.ColumnsChanged += OnColumnsChanged;
        DataContextChanged += (_, _) =>
        {
            if (CellsPanel.Children.Count == 0)
                BuildCells();
            // Bindings on TextBlock children are dynamic and update automatically when DataContext changes.
        };
    }

    // The row's own download icon was clicked - forwarded up through
    // MusicListPanel to MusicListView, whose consumer (MainView) is the one
    // holding the ViewModel that can actually start a download.
    public event EventHandler<TrackRowViewModel>? DownloadRequested;

    private void OnColumnsChanged(object? sender, EventArgs e) => BuildCells();

    private void BuildCells()
    {
        // Unsubscribe width/visibility handlers from previous build
        foreach (var (col, handler) in _widthSubs)
            col.PropertyChanged -= handler;
        _widthSubs.Clear();

        ApplyArtWell();
        CellsPanel.Children.Clear();

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
    }

    // The art well is only shown to the first row of an album run, which is
    // an ordinary binding - but only while the well exists at all. Turning it
    // off has to beat that binding, and a local IsVisible = false would be
    // overwritten the next time the binding produced a value, so the binding
    // itself is what goes: both of these live here, held as subscriptions,
    // rather than in the XAML where they cannot be withdrawn. The grid column
    // collapses to zero alongside them, since the art deliberately overflows
    // its cell (ClipToBounds="False") and would otherwise still paint over the
    // title; and the art binding goes too, because reading
    // TrackRowViewModel.AlbumArt is what starts the load - a fetch off the
    // paired server, for a library synced from one.
    private readonly List<IDisposable> _artBindings = new();

    private void ApplyArtWell()
    {
        foreach (var binding in _artBindings)
            binding.Dispose();
        _artBindings.Clear();

        RowGrid.ColumnDefinitions[0].Width = new GridLength(_columnManager.ArtColumnWidth);

        if (!_columnManager.ShowAlbumArt)
        {
            ArtCell.IsVisible = false;
            ArtImage.AlbumArt = null;
            return;
        }

        _artBindings.Add(ArtCell.Bind(IsVisibleProperty, new Binding(nameof(TrackRowViewModel.IsFirstInAlbumGroup))));
        _artBindings.Add(ArtImage.Bind(AlbumArtView.AlbumArtProperty, new Binding(nameof(TrackRowViewModel.AlbumArt))));
    }

    // Cell content is built per-column from ColumnManager's runtime-configurable
    // definitions, so these bindings are constructed in code (new Binding(...))
    // rather than declared in TrackRowControl.axaml, where compiled bindings are
    // otherwise on. Each path used below is a real property on
    // TrackRowViewModel/Track - but a *string* path, which is the whole problem:
    // nothing in the codebase references DurationDisplay and its siblings any
    // other way, so on a trimmed build the linker has no reason to believe they
    // are used and drops them. Only Flower.Web trims (WasmBuildNative), so this
    // showed up as browser-only: #, Duration, Plays, Added and Last Played all
    // rendered blank in a tab while Title/Artist/Album/Genre - bound through
    // Track, whose properties are referenced directly all over - were fine.
    //
    // DynamicDependency rather than a descriptor XML because the dependency is
    // genuinely CellBindingPathFor's below: it is what turns a column id into a
    // property name, and a root declared anywhere else would be a fact about
    // that switch stored away from it.
    private Control BuildCellContent(MusicColumnDefinition col)
    {
        if (col.Id == "Title")
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(14, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1,  GridUnitType.Star));
            // Auto, not a fixed well: the download button only exists for a
            // placeholder the paired server can serve right now (see
            // TrackDownloadButton), so a library with nothing to download -
            // every library with no server paired - costs no width at all.
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var indicator = new TextBlock
            {
                Text                = "▶",
                FontSize            = 8,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            indicator.Bind(IsVisibleProperty, new Binding(nameof(TrackRowViewModel.IsCurrentlyPlaying)));
            Grid.SetColumn(indicator, 0);

            var text = MakeText();
            text.Bind(TextBlock.TextProperty, new Binding("Track.Title"));
            Grid.SetColumn(text, 1);

            // Pinned to the far right of the Title cell rather than sitting
            // against the end of the title itself, so the icons line up down
            // the column instead of stepping in and out with each song's name.
            var download = new TrackDownloadButton { Margin = new Thickness(4, 0, 0, 0) };
            download.DownloadRequested += (_, clicked) =>
            {
                if (clicked is TrackRowViewModel row)
                    DownloadRequested?.Invoke(this, row);
            };
            Grid.SetColumn(download, 2);

            grid.Children.Add(indicator);
            grid.Children.Add(text);
            grid.Children.Add(download);
            return grid;
        }

        var tb = MakeText();
        tb.Bind(TextBlock.TextProperty, new Binding(CellBindingPathFor(col.Id)));
        return tb;
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(TrackRowViewModel))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(Models.Track))]
    // The property each column's cell shows, as a binding path. Internal and
    // separate from the cell it feeds so the tests can hold every column
    // ColumnManager offers to it: a column whose id lands on the "." fallback
    // renders an empty cell forever, which is the kind of thing only noticed
    // once someone turns that column on.
    internal static string CellBindingPathFor(string columnId) =>
        columnId switch
        {
            "Title" => "Track.Title",
            "TrackNumber" => nameof(TrackRowViewModel.TrackNumberDisplay),
            "Artist"      => "Track.Artists",
            "Album"       => "Track.Album",
            "Year"        => "Track.Year",
            "Genre"       => "Track.Genre",
            "Duration"    => nameof(TrackRowViewModel.DurationDisplay),
            "PlayCount"   => nameof(TrackRowViewModel.PlayCountDisplay),
            "DateAdded"   => nameof(TrackRowViewModel.DateAddedDisplay),
            "LastPlayed"  => nameof(TrackRowViewModel.LastPlayedDisplay),
            "Composer"    => "Track.Composers",
            "Encoding"    => "Track.EncoderProfile",
            "SortTitle"   => "Track.TitleSort",
            "SortArtist"  => "Track.ArtistsSort",
            "SortAlbum"   => "Track.AlbumSort",
            "SortComposer" => "Track.ComposersSort",
            "Compilation"      => nameof(TrackRowViewModel.CompilationDisplay),
            "RememberPosition" => nameof(TrackRowViewModel.RememberPositionDisplay),
            "ResumePosition"   => nameof(TrackRowViewModel.ResumePositionDisplay),
            "SkipInShuffle"    => nameof(TrackRowViewModel.SkipInShuffleDisplay),
            "VolumeAdjustment" => nameof(TrackRowViewModel.VolumeAdjustmentDisplay),
            "AlbumArtist"  => "Track.AlbumArtists",
            "Subtitle"     => "Track.Subtitle",
            "Disc"         => nameof(TrackRowViewModel.DiscDisplay),
            "Conductor"    => "Track.Conductor",
            "RemixedBy"    => "Track.RemixedBy",
            "Bpm"          => nameof(TrackRowViewModel.BpmDisplay),
            "InitialKey"   => "Track.InitialKey",
            "Grouping"     => "Track.Grouping",
            "Publisher"    => "Track.Publisher",
            "Isrc"         => "Track.ISRC",
            "Comment"      => "Track.Comment",
            "Description"  => "Track.Description",
            "Copyright"    => "Track.Copyright",
            "Starred"      => nameof(TrackRowViewModel.StarredDisplay),
            "Codec"        => "Track.Codec",
            "Bitrate"      => nameof(TrackRowViewModel.BitrateDisplay),
            "SampleRate"   => nameof(TrackRowViewModel.SampleRateDisplay),
            "Channels"     => nameof(TrackRowViewModel.ChannelsDisplay),
            "BitDepth"     => nameof(TrackRowViewModel.BitDepthDisplay),
            "Location"     => "Track.Path",
            _             => ".",
        };

    private static TextBlock MakeText() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming      = TextTrimming.CharacterEllipsis,
        FontSize          = 12,
    };
}
