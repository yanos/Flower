using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flower.Persistence;
using Flower.ViewModels;

namespace Flower.Controls;

public class ColumnManager
{
    private readonly AppSettings _appSettings;
    private readonly AppSettingsStore _appSettingsStore;
    private CancellationTokenSource? _pendingSaveCts;

    public List<MusicColumnDefinition> Columns { get; }

    public event EventHandler? ColumnsChanged;

    public IEnumerable<MusicColumnDefinition> VisibleColumns =>
        Columns.Where(c => c.IsVisible).OrderBy(c => c.Order);

    // The album-art well down the left of the track list (see
    // TrackRowControl's art cell). Not a MusicColumnDefinition, because it is
    // not one: it has no header, no sort, no resize handle, and its content
    // spans a whole album run rather than a row. It still belongs here rather
    // than in MainViewModel because every consumer of a column change - the
    // header bar, the row cells, the panel's content width - is exactly the
    // set that has to react to this too, and they already listen to
    // ColumnsChanged.
    public bool ShowAlbumArt
    {
        get => _appSettings.ShowAlbumArtColumn;
        set
        {
            if (_appSettings.ShowAlbumArtColumn == value)
                return;
            _appSettings.ShowAlbumArtColumn = value;
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }
    }

    // What the art well costs in width right now - the one number the header
    // spacer, the row grid and the panel's content width all offset by, so
    // hiding the art closes the gap instead of leaving an empty margin.
    public double ArtColumnWidth => ShowAlbumArt ? TrackRowViewModel.ArtColumnWidth : 0;

    public ColumnManager(AppSettings appSettings, AppSettingsStore appSettingsStore)
    {
        _appSettings = appSettings;
        _appSettingsStore = appSettingsStore;
        Columns = BuildDefaults();

        var saved = appSettings.ColumnStates;
        if (saved != null && saved.Count > 0)
            ApplySaved(saved);

        foreach (var col in Columns)
            col.PropertyChanged += (_, e) =>
            {
                // Width changes are already reflected live via each header
                // cell's own binding (see MusicListView.MakeHeaderCell) and each
                // row cell's direct width-sync subscription (see
                // TrackRowControl.BuildCells) - firing ColumnsChanged here too
                // would make MusicListView's ColumnsChanged handler rebuild the
                // whole header on every pixel of a resize drag, destroying and
                // replacing the very header cell (and its resize handle) whose
                // pointer capture is driving that drag, killing the gesture
                // after its first tiny movement. IsVisible/Order changes still
                // need the rebuild since those change which cells exist or
                // their sequence.
                if (e.PropertyName != nameof(MusicColumnDefinition.Width))
                    ColumnsChanged?.Invoke(this, EventArgs.Empty);
                ScheduleSave();
            };
    }

    // Moves `column` so it becomes the `newVisibleIndex`-th visible column
    // (other visible columns shifting to make room), then renumbers every
    // column's Order to match - hidden columns keep their existing relative
    // position among the ones that didn't move. Persisted the same way any
    // other column-state change is, via the PropertyChanged/ScheduleSave hookup
    // in the constructor.
    public void Reorder(MusicColumnDefinition column, int newVisibleIndex)
    {
        var ordered = Columns.OrderBy(c => c.Order).ToList();
        ordered.Remove(column);

        int insertAt = ordered.Count;
        int visibleSeen = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (!ordered[i].IsVisible)
                continue;
            if (visibleSeen == newVisibleIndex)
            {
                insertAt = i;
                break;
            }
            visibleSeen++;
        }

        ordered.Insert(insertAt, column);

        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Order != i)
                ordered[i].Order = i;
        }
    }

    private void ApplySaved(List<ColumnState> states)
    {
        foreach (var state in states)
        {
            var col = Columns.FirstOrDefault(c => c.Id == state.Id);
            if (col == null)
                continue;
            col.Width = state.Width;
            col.IsVisible = state.IsVisible;
            col.Order = state.Order;
        }
    }

    // A real debounce: each call cancels the previous pending save, so only the
    // last change in a burst actually writes. This used to just start another
    // unawaited Task.Delay(500) chain and assign it to a field nothing ever
    // read - so a column-resize drag, which fires this on every width change,
    // spawned dozens of independent timers that all woke up and wrote
    // settings.json at once (and swallowed any exception they threw, since
    // nothing observed the tasks).
    private void ScheduleSave()
    {
        _pendingSaveCts?.Cancel();
        _pendingSaveCts?.Dispose();
        var cts = new CancellationTokenSource();
        _pendingSaveCts = cts;
        _ = SaveAfterDelayAsync(cts.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
            _appSettings.ColumnStates = BuildStates();
            await _appSettingsStore.SaveAsync(_appSettings);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer change - that one carries the current state.
        }
    }

    // Synchronous, immediate counterpart to the debounced SaveAsync above - for
    // the Window.Closing handler, where the process may exit before a pending
    // debounced save's Task.Delay(500) completes, silently losing a resize (or
    // reorder/hide) made shortly before quitting.
    public void Flush()
    {
        // Cancel the pending debounced save first: this writes the same state
        // synchronously and right now, so letting the queued one fire too would
        // just be a redundant write racing process shutdown.
        _pendingSaveCts?.Cancel();
        _appSettings.ColumnStates = BuildStates();
        _appSettingsStore.Save(_appSettings);
    }

    private List<ColumnState> BuildStates() =>
        Columns
            .Select(c => new ColumnState { Id = c.Id, IsVisible = c.IsVisible, Width = c.Width, Order = c.Order })
            .ToList();

    private static List<MusicColumnDefinition> BuildDefaults() =>
    [
        new("TrackNumber", "#",        40,  30, true, 0),
        new("Title",       "Title",   240,  60, true, 1),
        new("Artist",      "Artist",  180,  60, true, 2),
        new("Album",       "Album",   180,  60, true, 3),
        new("Year",        "Year",     60,  40, true, 4),
        new("Genre",       "Genre",   100,  60, true, 5),
        new("Duration",    "Duration", 80,  50, true, 6),
        new("PlayCount",   "Plays",    55,  40, true, 7),
        new("DateAdded",   "Added",   100,  70, true, 8),
        new("LastPlayed",  "Last Played", 100, 70, true, 9),

        // Hidden by default, and that is the only thing separating them from
        // the ten above: a column nobody asked for should not push Title off
        // the right edge on first launch, but every field Track Info shows
        // should be *available* here (right-click the header) rather than
        // visible only one track at a time.
        new("Composer",         "Composer",         140, 60, false, 10),
        new("Encoding",         "Encoding",         160, 60, false, 11),
        new("SortTitle",        "Sort Title",       140, 60, false, 12),
        new("SortArtist",       "Sort Artist",      140, 60, false, 13),
        new("SortAlbum",        "Sort Album",       140, 60, false, 14),
        new("SortComposer",     "Sort Composer",    140, 60, false, 15),
        new("Compilation",      "Compilation",       90, 50, false, 16),
        new("RememberPosition", "Remembers",         80, 50, false, 17),
        new("ResumePosition",   "Resume At",         80, 50, false, 18),
        new("SkipInShuffle",    "Skip in Shuffle",   90, 50, false, 19),
        new("VolumeAdjustment", "Volume",            70, 50, false, 20),

        // The rest of Track Info, on the same terms: every field that window
        // shows one track at a time is a column here, hidden until asked for.
        // Lyrics is the single exception - a whole song's words is not a cell.
        new("AlbumArtist",  "Album Artist", 160, 60, false, 21),
        new("Subtitle",     "Subtitle",     140, 60, false, 22),
        new("Disc",         "Disc",          50, 40, false, 23),
        new("Conductor",    "Conductor",    140, 60, false, 24),
        new("RemixedBy",    "Remixed By",   140, 60, false, 25),
        new("Bpm",          "BPM",           55, 40, false, 26),
        new("InitialKey",   "Key",           50, 40, false, 27),
        new("Grouping",     "Grouping",     120, 60, false, 28),
        new("Publisher",    "Publisher",    140, 60, false, 29),
        new("Isrc",         "ISRC",         120, 60, false, 30),
        new("Comment",      "Comment",      200, 60, false, 31),
        new("Description",  "Description",  200, 60, false, 32),
        new("Copyright",    "Copyright",    160, 60, false, 33),
        new("Starred",      "Rating",        60, 40, false, 34),
        new("Codec",        "Codec",         80, 50, false, 35),
        new("Bitrate",      "Bit Rate",      80, 50, false, 36),
        new("SampleRate",   "Sample Rate",   90, 50, false, 37),
        new("Channels",     "Channels",      70, 50, false, 38),
        new("BitDepth",     "Bit Depth",     70, 50, false, 39),
        new("Location",     "Location",     300, 80, false, 40),
    ];
}
