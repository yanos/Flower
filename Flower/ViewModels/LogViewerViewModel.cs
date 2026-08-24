using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Threading;

using Flower.Logging;
using Flower.Persistence;

using Serilog.Events;

namespace Flower.ViewModels
{
    // The log viewer itself, with no opinion about whose log it is showing:
    // level/text filtering, the font-size and word-wrap preferences, and the
    // two events the View drives its editor from. LogViewModel (the app's own
    // Log window, always this device's live log) and SettingsViewModel's Logs
    // tab (a server's own log, or one of its devices' pushed snapshots) both
    // sit on top of this, which is the point - the server's log used to be a
    // plain 500-line TextBox with none of this.
    //
    // The View (LogViewer) renders this through an AvaloniaEdit TextEditor,
    // not Avalonia's own TextBlock/ListBox controls - those either don't
    // virtualize (a single TextBlock holding the whole log, the original
    // design, got sluggish on any real log volume) or don't support one
    // continuous multi-line mouse selection (a virtualizing ListBox of
    // per-row TextBlocks, the second design - each row is its own
    // independently-selectable control). A real text-editor control handles
    // both a large document and cross-line selection natively, which is
    // exactly what a log viewer needs. TextEditor.Text/AppendText aren't
    // bindable AvaloniaProperties, so instead of an ObservableCollection this
    // class pushes changes via two events the View drives the editor control
    // from directly: LinesReset (replace the whole document) and LinesAppended
    // (append one batch to the end).
    public class LogViewerViewModel : ViewModelBase
    {
        private readonly AppSettings _appSettings;

        // Null on a viewer whose preferences are not worth persisting - the
        // panel-hosted one in tests, which has no store to write to and must
        // not touch the developer's real settings.json.
        private readonly AppSettingsStore? _appSettingsStore;

        // Raw entries backing what is on screen, unfiltered - re-rendered into
        // DisplayLines whenever this, MinimumLevel or FilterText changes.
        private List<InMemoryLogEntry> _currentEntries = new();
        private List<string> _displayLines = new();

        // Shown instead of the entries when there are none and the reason is
        // worth saying out loud ("loading", "this device has never pushed").
        private string? _placeholder;

        // _currentEntries.Count as of the last render - compared against in
        // RenderLines to tell "the underlying log actually grew" apart from
        // "only the filtered subset changed" (see LinesReset's own doc
        // comment). Zeroed by ShowLog, since a different log is a fresh view
        // rather than a continuation of the previous one.
        private int _lastRenderedEntryCount;

        // Coalesces a burst of rapid entries (e.g. a chatty retry loop logging
        // several times a second) into a single LinesAppended batch per UI
        // dispatch, rather than one Dispatcher.Post (and one
        // TextEditor.AppendText call) per line.
        private readonly List<InMemoryLogEntry> _pendingEntries = new();
        private bool _flushScheduled;

        public static IReadOnlyList<LogEventLevel> MinimumLevelOptions { get; } =
            Enum.GetValues<LogEventLevel>();

        public static IReadOnlyList<double> FontSizeOptions { get; } =
            new double[] { 10, 11, 12, 13, 14, 16, 18, 20, 24 };

        public LogViewerViewModel(AppSettings appSettings, AppSettingsStore? appSettingsStore = null)
        {
            _appSettings = appSettings;
            _appSettingsStore = appSettingsStore;

            // Restores the last-used font size/minimum level/word wrap - set
            // directly on the backing fields, not the properties below, so
            // restoring a saved preference does not immediately re-save the
            // exact same value back (harmless, just pointless I/O) and does not
            // fire RenderLines before there is anything to render.
            _fontSize = appSettings.LogFontSize;
            _minimumLevel = appSettings.LogMinimumLevel;
            _isWordWrapEnabled = appSettings.LogWordWrapEnabled;
        }

        // The current filtered/leveled lines - read by the View's LinesReset
        // handler to repopulate the editor from scratch.
        public IReadOnlyList<string> DisplayLines => _displayLines;

        // Fired whenever the whole displayed set changes: a different log
        // loaded, a filter change, a level change, or a placeholder. The bool
        // argument is whether the underlying entries actually grew since the
        // last render (not just whether the filtered/displayed subset changed)
        // - the View uses it to decide whether to scroll: a level/filter change
        // re-renders the same underlying log and should never yank the view
        // around, only genuinely new content arriving should.
        public event EventHandler<bool>? LinesReset;

        // Fired once per coalesced batch of new matching entries - the View
        // responds with exactly one TextEditor.AppendText call per batch,
        // however many lines it contains.
        public event EventHandler<IReadOnlyList<string>>? LinesAppended;

        private LogEventLevel _minimumLevel;
        public LogEventLevel MinimumLevel
        {
            get => _minimumLevel;
            set
            {
                _minimumLevel = value;
                OnPropertyChanged();
                RenderLines();
                _appSettings.LogMinimumLevel = value;
                Persist();
            }
        }

        private string _filterText = "";
        public string FilterText
        {
            get => _filterText;
            set
            {
                _filterText = value;
                OnPropertyChanged();
                RenderLines();
            }
        }

        private double _fontSize;
        public double FontSize
        {
            get => _fontSize;
            set
            {
                _fontSize = value;
                OnPropertyChanged();
                _appSettings.LogFontSize = value;
                Persist();
            }
        }

        // Off by default, matching the original NoWrap behavior - most log
        // lines are single lines and unwrapped text is easier to scan/diff,
        // but a long line (or a wide exception stack frame) can be toggled to
        // wrap instead of scrolling horizontally for it.
        private bool _isWordWrapEnabled;
        public bool IsWordWrapEnabled
        {
            get => _isWordWrapEnabled;
            set
            {
                _isWordWrapEnabled = value;
                OnPropertyChanged();
                _appSettings.LogWordWrapEnabled = value;
                Persist();
            }
        }

        private void Persist()
        {
            if (_appSettingsStore != null)
                _ = _appSettingsStore.SaveAsync(_appSettings);
        }

        // Replaces everything on screen with a different log: another device's,
        // or the same one fetched again. Growth tracking restarts here, so the
        // newly loaded content always counts as growth and the View scrolls to
        // its end - measured against the previous log it could read as a shrink
        // (a long local log replaced by a one-line snapshot) and never scroll
        // into view at all.
        public void ShowLog(IReadOnlyList<InMemoryLogEntry> entries)
        {
            DropPendingEntries();
            _placeholder = null;
            _currentEntries = entries.ToList();
            _lastRenderedEntryCount = 0;
            RenderLines();
        }

        // For when there is nothing to show and why matters - "(loading...)"
        // and "(nothing has arrived from this device yet)" are different
        // answers, and a blank pane is neither of them.
        public void ShowPlaceholder(string message)
        {
            DropPendingEntries();
            _placeholder = message;
            _currentEntries = new List<InMemoryLogEntry>();
            _lastRenderedEntryCount = 0;
            RenderLines();
        }

        // One new entry for the log already on screen, coalesced with whatever
        // else arrives before the UI thread next goes idle. Only a live source
        // calls this - a fetched snapshot is whole when it lands.
        public void Append(InMemoryLogEntry entry)
        {
            lock (_pendingEntries)
            {
                _pendingEntries.Add(entry);
                if (_flushScheduled)
                    return;
                _flushScheduled = true;
            }

            // Background priority: flushes whenever the UI thread is otherwise
            // idle, so a burst naturally coalesces into however many flushes
            // the UI can actually keep up with rather than one per line.
            Dispatcher.UIThread.Post(FlushPendingEntries, DispatcherPriority.Background);
        }

        // Entries that arrived for the log that was on screen a moment ago must
        // not land under the one replacing it.
        private void DropPendingEntries()
        {
            lock (_pendingEntries)
                _pendingEntries.Clear();
        }

        private void FlushPendingEntries()
        {
            List<InMemoryLogEntry> batch;
            lock (_pendingEntries)
            {
                batch = new List<InMemoryLogEntry>(_pendingEntries);
                _pendingEntries.Clear();
                _flushScheduled = false;
            }

            if (batch.Count == 0)
                return;

            var appended = new List<string>();
            foreach (var entry in batch)
            {
                _currentEntries.Add(entry);
                if (MatchesFilter(entry))
                {
                    var line = entry.ToDisplayLine();
                    _displayLines.Add(line);
                    appended.Add(line);
                }
            }

            // Keeps RenderLines' own growth comparison accurate for a later
            // filter/level change that does not go through this method.
            _lastRenderedEntryCount = _currentEntries.Count;

            if (appended.Count > 0)
                LinesAppended?.Invoke(this, appended);
        }

        private void RenderLines()
        {
            // Compares the raw entry count, not _displayLines.Count - a pure
            // filter/level change re-derives _displayLines from the exact same
            // _currentEntries, so this correctly reports "nothing new" for that
            // case regardless of how the filtered subset changed.
            var grew = _currentEntries.Count > _lastRenderedEntryCount;
            _lastRenderedEntryCount = _currentEntries.Count;

            _displayLines = _placeholder != null
                ? [_placeholder]
                : _currentEntries.Where(MatchesFilter).Select(e => e.ToDisplayLine()).ToList();

            LinesReset?.Invoke(this, grew);
        }

        private bool MatchesFilter(InMemoryLogEntry entry)
        {
            if (!Enum.TryParse<LogEventLevel>(entry.Level, out var level) || level < _minimumLevel)
                return false;

            if (string.IsNullOrWhiteSpace(_filterText))
                return true;

            return entry.Message.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
                || (entry.SourceContext?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ?? false);
        }
    }
}
