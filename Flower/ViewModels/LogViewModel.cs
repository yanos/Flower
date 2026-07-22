using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Avalonia.Threading;

using Flower.Logging;
using Flower.Persistence;
using Flower.Services;

using Serilog.Events;

namespace Flower.ViewModels
{
    // Backs the Log window (View > Log...) - see LogWindow.axaml. "This
    // Device" always shows this instance's own live log (fed by
    // InMemoryLogStore, updating in real time). A "paired client" row only
    // appears when this instance is running as a sync Server, one per
    // TrustedPeerStore entry, showing whatever snapshot that client's own
    // LibrarySyncService.PushLogSnapshotAsync last pushed here (see
    // ClientLogStore) - a batch refresh on the library-sync cadence, not a
    // live stream, since a Server never dials out to pull from a Client.
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
    public class LogViewModel : ViewModelBase
    {
        private readonly InMemoryLogStore _localLogStore;
        private readonly ClientLogStore _clientLogStore;
        private readonly AppSettings _appSettings;
        private readonly TrustedPeerStore _trustedPeerStore;
        private readonly DeviceNicknameStore _deviceNicknameStore;

        // Raw entries backing the current selection, unfiltered - re-rendered
        // into DisplayLines whenever this, MinimumLevel, or FilterText changes.
        private List<InMemoryLogEntry> _currentEntries = new();
        private bool _noSnapshotYet;
        private List<string> _displayLines = new();

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
        public event EventHandler? LinesReset;

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
            set { _fontSize = value; OnPropertyChanged(); }
        }

        // Off by default, matching the original NoWrap behavior - most log
        // lines are single lines and unwrapped text is easier to scan/diff,
        // but a long line (or a wide exception stack frame) can be toggled to
        // wrap instead of scrolling horizontally for it.
        private bool _isWordWrapEnabled;
        public bool IsWordWrapEnabled
        {
            get => _isWordWrapEnabled;
            set { _isWordWrapEnabled = value; OnPropertyChanged(); }
        }

        public LogViewModel(
            InMemoryLogStore localLogStore,
            ClientLogStore clientLogStore,
            AppSettings appSettings,
            TrustedPeerStore trustedPeerStore,
            DeviceNicknameStore deviceNicknameStore)
        {
            _localLogStore = localLogStore;
            _clientLogStore = clientLogStore;
            _appSettings = appSettings;
            _trustedPeerStore = trustedPeerStore;
            _deviceNicknameStore = deviceNicknameStore;

            _localLogStore.EntryAdded += OnLocalEntryAdded;
            _clientLogStore.SnapshotUpdated += OnClientSnapshotUpdated;

            RefreshSidebarItems();
        }

        // Rebuilds the sidebar from current AppSettings.IsServer/trusted-peer
        // state - called once here and again from LogWindow's constructor
        // every time the window is (re)opened, since toggling Server mode or
        // trusting a new peer since this ViewModel was constructed has no
        // live notification of its own to react to.
        public void RefreshSidebarItems()
        {
            var previouslySelectedFingerprint = _selectedSidebarItem?.Fingerprint;

            SidebarItems.Clear();
            foreach (var item in LogSidebarBuilder.Build(_appSettings.IsServer, _trustedPeerStore.Load(), _deviceNicknameStore.Get))
                SidebarItems.Add(item);

            SelectedSidebarItem = SidebarItems.FirstOrDefault(i => i.Fingerprint == previouslySelectedFingerprint)
                ?? SidebarItems.First(); // "This Device" is always index 0
        }

        private void LoadSelection()
        {
            _noSnapshotYet = false;

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
                var snapshot = _clientLogStore.Get(_selectedSidebarItem.Fingerprint!);
                if (snapshot == null)
                {
                    _currentEntries = new List<InMemoryLogEntry>();
                    _noSnapshotYet = true;
                }
                else
                {
                    _currentEntries = snapshot.Entries
                        .Select(e => new InMemoryLogEntry(e.Timestamp, e.Level, e.SourceContext, e.Message, e.Exception))
                        .ToList();
                }
            }

            RenderLines();
        }

        private void RenderLines()
        {
            _displayLines = _noSnapshotYet
                ? new List<string> { "(no log snapshot received from this device yet)" }
                : _currentEntries.Where(MatchesFilter).Select(e => e.ToDisplayLine()).ToList();

            LinesReset?.Invoke(this, EventArgs.Empty);
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

            if (appended.Count > 0)
                LinesAppended?.Invoke(this, appended);
        }

        private void OnClientSnapshotUpdated(object? sender, string fingerprint)
        {
            if (_selectedSidebarItem?.Kind != LogSidebarItemKind.PairedClient || _selectedSidebarItem.Fingerprint != fingerprint)
                return;

            // Full replace, not an append - matches ClientLogStore's own
            // "each push is a fresh full snapshot" design.
            Dispatcher.UIThread.Post(LoadSelection);
        }
    }
}
