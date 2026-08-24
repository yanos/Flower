using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Threading;

using Flower.Logging;
using Flower.Persistence;
using Flower.Services;

using Serilog.Events;

namespace Flower.ViewModels
{
    // Backs the Log window (View > Log...) - see LogWindow.axaml. "This
    // Device" always shows this instance's own live log (fed by
    // InMemoryLogStore, updating in real time). Every other row is read from
    // the paired server's admin API: "Server" is that server's own log, and a
    // row per device on its roster shows whatever snapshot that device last
    // pushed to it (see ClientLogStore, and LibrarySyncService.
    // PushLogSnapshotAsync on the pushing side).
    //
    // Those rows are snapshots on the sync cadence, not live tails - a phone
    // pushes when it syncs and is otherwise asleep - and they are fetched on
    // selection rather than kept warm, since reading somebody else's log is
    // something a person does deliberately and rarely.
    //
    // The View (LogWindow) renders this via an AvaloniaEdit TextEditor, not
    // Avalonia's own TextBlock/ListBox controls - those either don't
    // virtualize (a single TextBlock holding the whole log, the original
    // design, got sluggish on any real log volume) or don't support one
    // continuous multi-line mouse selection (a virtualizing ListBox of
    // per-row TextBlocks, the second design - each row is its own
    // independently-selectable control). A real text-editor control handles
    // both a large document and cross-line selection natively, which is
    // exactly what a log viewer needs. TextEditor.Text/AppendText aren't
    // bindable AvaloniaProperties, so instead of an ObservableCollection this
    // class pushes changes via two events the View drives the editor
    // control from directly: LinesReset (replace the whole document) and
    // LinesAppended (append one batch to the end).
    public class LogViewModel : ViewModelBase, IDisposable
    {
        private readonly InMemoryLogStore _localLogStore;
        private readonly AppSettings _appSettings;
        private readonly AppSettingsStore _appSettingsStore;
        private readonly IRemoteLogSource? _remoteLogs;
        private readonly DeviceNicknameStore _deviceNicknameStore;

        // Guards against a slow fetch for a row the user has already navigated
        // away from winning the race and painting its lines under a different
        // selection - the same stale-response problem PeerLibraryViewModel
        // solves with its own _requestId.
        private int _selectionId;

        // Raw entries backing the current selection, unfiltered - re-rendered
        // into DisplayLines whenever this, MinimumLevel, or FilterText changes.
        private List<InMemoryLogEntry> _currentEntries = new();
        private List<string> _displayLines = new();

        // _currentEntries.Count as of the last render - compared against in
        // RenderLines to tell "the underlying log actually grew" apart from
        // "only the filtered subset changed" (see LinesReset's own doc
        // comment). Reset to 0 on a sidebar selection change - see
        // SelectedSidebarItem's setter.
        private int _lastRenderedEntryCount;

        // Coalesces a burst of rapid local entries (e.g. a chatty retry loop
        // logging several times a second) into a single LinesAppended batch
        // per UI dispatch, rather than one Dispatcher.Post (and one
        // TextEditor.AppendText call) per line.
        private readonly List<InMemoryLogEntry> _pendingLocalEntries = new();
        private bool _flushScheduled;

        public static IReadOnlyList<LogEventLevel> MinimumLevelOptions { get; } =
            Enum.GetValues<LogEventLevel>();

        public static IReadOnlyList<double> FontSizeOptions { get; } =
            new double[] { 10, 11, 12, 13, 14, 16, 18, 20, 24 };

        public ObservableCollection<LogSidebarItem> SidebarItems { get; } = new();

        // The current filtered/leveled lines for the selected sidebar item -
        // read by the View's LinesReset handler to repopulate the editor from
        // scratch.
        public IReadOnlyList<string> DisplayLines => _displayLines;

        // Fired whenever the whole displayed set changes: selection change,
        // filter change, level change, or the "no snapshot yet" placeholder.
        // The bool argument is whether the underlying entries actually grew
        // since the last render (not just whether the filtered/displayed
        // subset changed) - the View uses it to decide whether to scroll:
        // a level/filter change re-renders the same underlying log and
        // should never yank the view around, only genuinely new content
        // arriving should.
        public event EventHandler<bool>? LinesReset;

        // Fired once per coalesced batch of new matching local entries - the
        // View responds with exactly one TextEditor.AppendText call per
        // batch, however many lines it contains.
        public event EventHandler<IReadOnlyList<string>>? LinesAppended;

        private LogSidebarItem? _selectedSidebarItem;
        public LogSidebarItem? SelectedSidebarItem
        {
            get => _selectedSidebarItem;
            set
            {
                _selectedSidebarItem = value;
                OnPropertyChanged();
                // A different selection is a fresh view of different
                // content, not a continuation of the previous one - the
                // entry-count comparison in RenderLines only makes sense
                // within the same selection, so reset it here rather than
                // comparing against whatever the last-viewed device had.
                _lastRenderedEntryCount = 0;
                LoadSelection();
            }
        }

        private LogEventLevel _minimumLevel = LogEventLevel.Verbose;
        public LogEventLevel MinimumLevel
        {
            get => _minimumLevel;
            set
            {
                _minimumLevel = value;
                OnPropertyChanged();
                RenderLines();
                _appSettings.LogMinimumLevel = value;
                _ = _appSettingsStore.SaveAsync(_appSettings);
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

        private double _fontSize = 12;
        public double FontSize
        {
            get => _fontSize;
            set
            {
                _fontSize = value;
                OnPropertyChanged();
                _appSettings.LogFontSize = value;
                _ = _appSettingsStore.SaveAsync(_appSettings);
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
                _ = _appSettingsStore.SaveAsync(_appSettings);
            }
        }

        // remoteLogs is null on a head with no signing key at all (the browser
        // - see App.axaml.cs), which is also the head that has no Log window to
        // open. The sidebar then has exactly one row.
        public LogViewModel(
            InMemoryLogStore localLogStore,
            AppSettings appSettings,
            AppSettingsStore appSettingsStore,
            DeviceNicknameStore deviceNicknameStore,
            IRemoteLogSource? remoteLogs = null)
        {
            _localLogStore = localLogStore;
            _appSettings = appSettings;
            _appSettingsStore = appSettingsStore;
            _remoteLogs = remoteLogs;
            _deviceNicknameStore = deviceNicknameStore;

            // Restores the last-used font size/minimum level/word wrap -
            // set directly on the backing fields, not the properties above,
            // so restoring a saved preference does not immediately re-save
            // the exact same value back (harmless, just pointless I/O) and
            // does not fire RenderLines before SidebarItems/selection exist
            // yet (RefreshSidebarItems below does that once everything is
            // actually ready).
            _fontSize = appSettings.LogFontSize;
            _minimumLevel = appSettings.LogMinimumLevel;
            _isWordWrapEnabled = appSettings.LogWordWrapEnabled;

            _subscriptions.Add<EventHandler<InMemoryLogEntry>>(OnLocalEntryAdded,
                h => _localLogStore.EntryAdded += h, h => _localLogStore.EntryAdded -= h);

            RefreshSidebarItems();
        }

        // Every event this class attaches to in its constructor, paired with
        // its teardown - see SubscriptionBag, and docs/ARCHITECTURE-REVIEW.md
        // Tier 2.3.
        private readonly SubscriptionBag _subscriptions = new();

        public void Dispose() => _subscriptions.Dispose();

        // Rebuilds the sidebar from the paired server's current device roster -
        // called once here and again from LogWindow's constructor every time
        // the window is (re)opened, since pairing with a server, or that server
        // admitting a new device, has no live notification of its own to react
        // to on this side.
        //
        // Fire-and-forget rather than awaited: the window has to open now, with
        // "This Device" already selected and rendering, and the extra rows can
        // arrive a moment later. A server that is unreachable, or that says
        // this device is no admin, simply leaves the sidebar as that one row -
        // which is the honest answer, and the same one an unpaired device gets.
        public void RefreshSidebarItems()
        {
            // Only seeds the one always-present row, and only when there is
            // nothing at all yet. A refresh over existing rows deliberately
            // leaves them alone until the new roster lands: clearing first
            // would drop the selection to "This Device" every time the window
            // reopened, and put it back a moment later.
            if (SidebarItems.Count == 0)
                SetSidebarItems([]);

            _ = LoadRemoteSidebarItemsAsync();
        }

        private async Task LoadRemoteSidebarItemsAsync()
        {
            if (_remoteLogs == null)
                return;

            // Null means nothing to ask - unpaired, unreachable, or not an
            // admin of that server. None of those is worth an error surface
            // here: the window still shows this device's own log. It does mean
            // dropping any rows a previous refresh added, though - rows for a
            // server this device can no longer reach are rows whose every
            // selection would fail.
            var devices = await _remoteLogs.ListDevicesAsync() ?? [];

            Dispatcher.UIThread.Post(() => SetSidebarItems(devices));
        }

        private void SetSidebarItems(IReadOnlyList<RemoteDevice> serverDevices)
        {
            var rebuilt = LogSidebarBuilder.Build(serverDevices, _remoteLogs?.OwnFingerprint, _deviceNicknameStore.Get);

            // A refresh that changes nothing must change nothing. Rebuilding
            // unconditionally would hand the selection a fresh LogSidebarItem
            // with the same contents, and re-selecting is what reloads the pane
            // - so every reopen of the window would repaint the log the user
            // was already reading, scrolling them back to the bottom.
            if (SidebarItems.Count == rebuilt.Count &&
                SidebarItems.Zip(rebuilt).All(p => p.First.Kind == p.Second.Kind
                                                   && p.First.Fingerprint == p.Second.Fingerprint
                                                   && p.First.Name == p.Second.Name))
                return;

            var previouslySelectedFingerprint = _selectedSidebarItem?.Fingerprint;
            var previouslySelectedKind = _selectedSidebarItem?.Kind;

            SidebarItems.Clear();
            foreach (var item in rebuilt)
                SidebarItems.Add(item);

            SelectedSidebarItem = SidebarItems.FirstOrDefault(
                    i => i.Kind == previouslySelectedKind && i.Fingerprint == previouslySelectedFingerprint)
                ?? SidebarItems.First(); // "This Device" is always index 0
        }

        // How many lines a remote row asks the server for. The local buffer is
        // whatever InMemoryLogStore holds; this is the equivalent ceiling for a
        // log arriving over the wire.
        private const int RemoteLogLimit = 2000;

        private void LoadSelection()
        {
            var selectionId = ++_selectionId;
            if (_selectedSidebarItem == null)
            {
                _currentEntries = new List<InMemoryLogEntry>();
            }
            else if (_selectedSidebarItem.Kind == LogSidebarItemKind.ThisDevice)
            {
                _currentEntries = _localLogStore.Snapshot().ToList();
            }
            else
            {
                // Cleared first, then filled when the fetch lands: leaving the
                // previous row's lines on screen while a different row is
                // selected would read as that row's log.
                _currentEntries = new List<InMemoryLogEntry>();
                _fetchState = RemoteFetchState.Loading;
                _ = LoadRemoteSelectionAsync(_selectedSidebarItem, selectionId);
                RenderLines();
                return;
            }

            _fetchState = RemoteFetchState.None;
            RenderLines();
        }

        // What a remote row is showing while it has no lines - see
        // RemoteLogOutcome, plus the one state that belongs to the window
        // rather than to the fetch.
        private enum RemoteFetchState { None, Loading, NoSnapshot, Unavailable }
        private RemoteFetchState _fetchState = RemoteFetchState.None;

        private async Task LoadRemoteSelectionAsync(LogSidebarItem item, int selectionId)
        {
            var result = _remoteLogs == null
                ? RemoteLogResult.Unavailable
                : item.Kind == LogSidebarItemKind.Server
                    ? await _remoteLogs.GetServerLogAsync(RemoteLogLimit)
                    : await _remoteLogs.GetDeviceLogAsync(item.Fingerprint!, RemoteLogLimit);

            Dispatcher.UIThread.Post(() =>
            {
                if (selectionId != _selectionId)
                    return; // The user moved on while this was in flight.
                _currentEntries = result.Entries.ToList();
                _fetchState = result.Outcome switch
                {
                    RemoteLogOutcome.NoSnapshot => RemoteFetchState.NoSnapshot,
                    RemoteLogOutcome.Unavailable => RemoteFetchState.Unavailable,
                    _ => RemoteFetchState.None,
                };
                RenderLines();
            });
        }

        private void RenderLines()
        {
            // Compares the raw entry count, not _displayLines.Count - a pure
            // filter/level change re-derives _displayLines from the exact
            // same _currentEntries, so this correctly reports "nothing new"
            // for that case regardless of how the filtered subset changed.
            var grew = _currentEntries.Count > _lastRenderedEntryCount;
            _lastRenderedEntryCount = _currentEntries.Count;

            _displayLines = _fetchState switch
            {
                RemoteFetchState.Loading => ["(loading...)"],
                RemoteFetchState.NoSnapshot => ["(no log snapshot received from this device yet)"],
                RemoteFetchState.Unavailable => ["(could not read this log - the server is unreachable, or this device is not one of its administrators)"],
                _ => _currentEntries.Where(MatchesFilter).Select(e => e.ToDisplayLine()).ToList(),
            };

            LinesReset?.Invoke(this, grew);
        }

        private bool MatchesFilter(InMemoryLogEntry entry)
        {
            if (!Enum.TryParse<LogEventLevel>(entry.Level, out var level) || level < _minimumLevel)
                return false;

            if (string.IsNullOrWhiteSpace(_filterText))
                return true;

            return (entry.Message.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
                || (entry.SourceContext?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private void OnLocalEntryAdded(object? sender, InMemoryLogEntry entry)
        {
            if (_selectedSidebarItem?.Kind != LogSidebarItemKind.ThisDevice)
                return;

            lock (_pendingLocalEntries)
            {
                _pendingLocalEntries.Add(entry);
                if (_flushScheduled)
                    return;
                _flushScheduled = true;
            }

            // Background priority: flushes whenever the UI thread is
            // otherwise idle, so a burst naturally coalesces into however
            // many flushes the UI can actually keep up with rather than one
            // per line.
            Dispatcher.UIThread.Post(FlushPendingLocalEntries, DispatcherPriority.Background);
        }

        private void FlushPendingLocalEntries()
        {
            List<InMemoryLogEntry> batch;
            lock (_pendingLocalEntries)
            {
                batch = new List<InMemoryLogEntry>(_pendingLocalEntries);
                _pendingLocalEntries.Clear();
                _flushScheduled = false;
            }

            if (_selectedSidebarItem?.Kind != LogSidebarItemKind.ThisDevice)
                return; // Selection moved away while this flush was pending.

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

    }
}
