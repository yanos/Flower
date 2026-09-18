using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

// Backs the smart playlist rule editor: the name, the All/Any header, the rows,
// the limit, the live-updating checkbox and what Save does with them.
//
// Deliberately knows nothing about windows. The editor is a dialog today and
// might be a pane later, and everything here - including "cancel on a playlist
// that was created just to be edited deletes it again" - is decidable without
// either.
public sealed class SmartPlaylistEditorViewModel : ViewModelBase
{
    public sealed record MatchModeOption(MatchMode Mode, string Name)
    {
        public override string ToString() => Name;
    }

    public sealed record LimitUnitOption(LimitUnit Unit, string Name)
    {
        public override string ToString() => Name;
    }

    public sealed record LimitSelectorOption(LimitSelector Selector, string Name)
    {
        public override string ToString() => Name;
    }

    public static ImmutableArray<MatchModeOption> AllMatchModes { get; } =
        [.. Enum.GetValues<MatchMode>().Select(m => new MatchModeOption(m, SmartPlaylistLabels.Name(m)))];

    public static ImmutableArray<LimitUnitOption> AllLimitUnits { get; } =
        [.. Enum.GetValues<LimitUnit>().Select(u => new LimitUnitOption(u, SmartPlaylistLabels.Name(u)))];

    public static ImmutableArray<LimitSelectorOption> AllLimitSelectors { get; } =
        [.. Enum.GetValues<LimitSelector>().Select(s => new LimitSelectorOption(s, SmartPlaylistLabels.Name(s)))];

    // Instance-facing views of the static tables above - see
    // SmartConditionRowViewModel.Fields for why a compiled binding needs them.
    public ImmutableArray<MatchModeOption> MatchModes => AllMatchModes;

    public ImmutableArray<LimitUnitOption> LimitUnits => AllLimitUnits;

    public ImmutableArray<LimitSelectorOption> LimitSelectors => AllLimitSelectors;

    private readonly Library _library;
    private readonly Playlist _playlist;
    private readonly SmartPlaylistRefresher _refresher;
    private readonly bool _isNew;

    // The playlist being edited. Exposed so the view can reselect it in the
    // sidebar after a save, and so Cancel can find the row to delete.
    public Playlist Playlist => _playlist;

    public SmartPlaylistEditorViewModel(
        Playlist playlist,
        Library library,
        SmartPlaylistRefresher refresher,
        bool isNew = false)
    {
        _playlist = playlist;
        _library = library;
        _refresher = refresher;
        _isNew = isNew;

        // A new playlist opens with an empty box reading "Playlist Name", as a
        // new ordinary one does, rather than asking the user to select and
        // delete a placeholder first. Saving it empty keeps the name it was
        // created under - see Save.
        _name = isNew ? string.Empty : playlist.Name;
        PlaylistCandidates = BuildCandidates();

        var rules = playlist.Rules;
        _matchMode = AllMatchModes.First(m => m.Mode == (rules?.Mode ?? Models.MatchMode.All));
        _liveUpdating = rules?.LiveUpdating ?? true;

        if (rules?.Limit is { } limit)
        {
            _limitEnabled = true;
            _limitAmount = limit.Amount;
            _limitUnit = AllLimitUnits.First(u => u.Unit == limit.Unit);
            _limitSelector = AllLimitSelectors.First(s => s.Selector == limit.SelectedBy);
        }

        Conditions = [];
        foreach (var condition in rules?.Conditions ?? [])
            Conditions.Add(new SmartConditionRowViewModel(this, condition));

        // A playlist with no rules yet still opens on one row: an editor that
        // starts empty makes the user find the + button before it explains
        // anything about itself.
        if (Conditions.Count == 0)
            Conditions.Add(new SmartConditionRowViewModel(this));

        foreach (var row in Conditions)
            row.PropertyChanged += OnRowChanged;
        Conditions.CollectionChanged += OnConditionsChanged;
        PropertyChanged += OnEditorChanged;

        SchedulePreview(afterTypingPause: false);
    }

    public bool IsNew => _isNew;

    public string Title => _isNew ? "New Smart Playlist" : "Edit Smart Playlist";

    // ── Header ────────────────────────────────────────────────────────────────

    private string _name;
    public string Name
    {
        get => _name;
        set { _name = value ?? string.Empty; OnPropertyChanged(); }
    }

    private MatchModeOption _matchMode;
    public MatchModeOption MatchMode
    {
        get => _matchMode;
        set { if (value != null) { _matchMode = value; OnPropertyChanged(); } }
    }

    public ObservableCollection<SmartConditionRowViewModel> Conditions { get; }

    // ── Limit ─────────────────────────────────────────────────────────────────

    private bool _limitEnabled;
    public bool LimitEnabled
    {
        get => _limitEnabled;
        set { _limitEnabled = value; OnPropertyChanged(); }
    }

    private int _limitAmount = 25;
    public int LimitAmount
    {
        get => _limitAmount;
        set { _limitAmount = value; OnPropertyChanged(); }
    }

    private LimitUnitOption _limitUnit = AllLimitUnits[0];
    public LimitUnitOption LimitUnit
    {
        get => _limitUnit;
        set { if (value != null) { _limitUnit = value; OnPropertyChanged(); } }
    }

    private LimitSelectorOption _limitSelector = AllLimitSelectors.First(s => s.Selector == Models.LimitSelector.Random);
    public LimitSelectorOption LimitSelector
    {
        get => _limitSelector;
        set { if (value != null) { _limitSelector = value; OnPropertyChanged(); } }
    }

    private bool _liveUpdating = true;
    public bool LiveUpdating
    {
        get => _liveUpdating;
        set { _liveUpdating = value; OnPropertyChanged(); }
    }

    // Whatever stopped the last Save, shown under the rows. Cleared on the next
    // attempt rather than as the user types - a message that vanishes while
    // being read is worse than one that lingers.
    private string? _error;
    public string? Error
    {
        get => _error;
        private set { _error = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    // ── Rows ──────────────────────────────────────────────────────────────────

    public void AddCondition(SmartConditionRowViewModel? after = null)
    {
        var row = new SmartConditionRowViewModel(this);
        var index = after != null ? Conditions.IndexOf(after) + 1 : Conditions.Count;
        Conditions.Insert(index < 0 ? Conditions.Count : index, row);
        OnPropertyChanged(nameof(CanRemoveConditions));
        OnPropertyChanged(nameof(RemoveButtonOpacity));
    }

    // The last row is never removable: rules with no conditions match the whole
    // library, which is a playlist nobody meant to make, and Validate would not
    // catch it because it is perfectly evaluable.
    public void RemoveCondition(SmartConditionRowViewModel row)
    {
        if (Conditions.Count <= 1)
            return;

        Conditions.Remove(row);
        OnPropertyChanged(nameof(CanRemoveConditions));
        OnPropertyChanged(nameof(RemoveButtonOpacity));
    }

    public bool CanRemoveConditions => Conditions.Count > 1;

    // The ring around a disabled - is not the button's own, so it does not dim
    // with it; the view dims the whole circle by this instead.
    public double RemoveButtonOpacity => CanRemoveConditions ? 1.0 : 0.4;

    // ── The playlists a membership rule may point at ──────────────────────────

    // Everything except this playlist and everything that already depends on
    // it, so picking one cannot build a loop - see
    // SmartPlaylistGraph.ReferenceCandidates. Computed once per editor session:
    // nothing in this window can change another playlist's rules.
    public ImmutableArray<SmartConditionRowViewModel.PlaylistOption> PlaylistCandidates { get; }

    private ImmutableArray<SmartConditionRowViewModel.PlaylistOption> BuildCandidates()
    {
        var smart = _library.Playlists
            .Where(p => p.Rules != null && p.Id != _playlist.Id)
            .ToDictionary(p => p.Id, p => p.Rules!);

        var byId = _library.Playlists.ToDictionary(p => p.Id);
        var candidates = SmartPlaylistGraph.ReferenceCandidates(_playlist.Id, byId.Keys, smart);

        return [.. candidates
            .Where(byId.ContainsKey)
            .Select(id => new SmartConditionRowViewModel.PlaylistOption(id, byId[id].Name))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    // The songs the rules as currently typed would pick, recomputed on every
    // change to them, so the user sees what a rule does while writing it
    // rather than after saving. Rows that do not say anything yet - an empty
    // text box, a number that does not parse - are left out instead of
    // emptying the list: half a rule is what a rule looks like while typed.
    private IReadOnlyList<Track> _previewTracks = [];
    public IReadOnlyList<Track> PreviewTracks
    {
        get => _previewTracks;
        private set
        {
            _previewTracks = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewSummary));
        }
    }

    private bool _hasPreviewRules;

    public string PreviewSummary => !_hasPreviewRules
        ? "Songs matching these rules will be listed here."
        : _previewTracks.Count switch
        {
            0 => "No songs match these rules.",
            1 => "1 song matches these rules.",
            var n => $"{n:N0} songs match these rules.",
        };

    // One seed per editor, and a fresh Random from it per pass: a random limit
    // then picks the same songs for the same rules, instead of reshuffling
    // the list on every keystroke.
    private readonly int _previewSeed = Random.Shared.Next();

    // How long typing has to pause before the preview follows it. A pass is a
    // walk of the whole library and a rebuild of the rows under the rules, and
    // doing that per keystroke made the text box stutter on a phone.
    public static readonly TimeSpan TypingPause = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _previewCts;
    private Task _previewPass = Task.CompletedTask;

    private bool _refreshingPreview;

    // Text waits for the typing to pause; a pick from a list (a field, an
    // operator, a row added or removed) is a finished thought and goes now.
    // Either way the evaluation itself runs off the UI thread, and a pass
    // overtaken by a newer one is dropped rather than shown.
    private void SchedulePreview(bool afterTypingPause)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        _previewPass = RunPreviewAsync(afterTypingPause ? TypingPause : TimeSpan.Zero, _previewCts.Token);
    }

    // The editor is closing: a pass still waiting out a pause has nobody to
    // show its songs to.
    private void StopPreview()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
    }

    // Skips any typing pause still pending and returns once the preview shows
    // the rules as they stand. For tests; the UI never needs to wait on it.
    public Task RefreshPreviewNowAsync()
    {
        SchedulePreview(afterTypingPause: false);
        return _previewPass;
    }

    private async Task RunPreviewAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token);

            // Read the rows here, on the UI thread, and hand the pass only the
            // immutable rules built from them.
            var (rules, hasRules) = BuildPreviewRules();
            var seed = _previewSeed;
            var tracks = hasRules
                ? await Task.Run<IReadOnlyList<Track>>(() => _refresher.Preview(rules, new Random(seed)), token)
                : [];

            if (token.IsCancellationRequested)
                return;

            _refreshingPreview = true;
            try
            {
                _hasPreviewRules = hasRules;
                PreviewTracks = tracks;
            }
            finally
            {
                _refreshingPreview = false;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer change restarted the pass; its own run will show the result.
        }
    }

    private (SmartPlaylistRules Rules, bool HasRules) BuildPreviewRules()
    {
        var conditions = new List<SmartCondition>(Conditions.Count);
        foreach (var row in Conditions)
        {
            if (row.IsBlank || !row.TryBuild(out var condition, out _))
                continue;
            conditions.Add(condition!);
        }

        SmartLimit? limit = LimitEnabled && LimitAmount > 0
            ? new SmartLimit(LimitAmount, LimitUnit.Unit, LimitSelector.Selector)
            : null;
        var rules = new SmartPlaylistRules(MatchMode.Mode, conditions, limit, LiveUpdating);

        // Validate before evaluating: a shape the evaluator does not accept
        // throws, and a preview is no place for that to surface.
        var hasRules = conditions.Count > 0 && SmartPlaylistEvaluator.Validate(rules) is { Count: 0 };
        return (rules, hasRules);
    }

    // Only what a row would build from: a field change alone raises a dozen
    // layout notifications, each of which would otherwise be a library pass.
    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SmartConditionRowViewModel.ValueText)
            or nameof(SmartConditionRowViewModel.SecondValueText)
            or nameof(SmartConditionRowViewModel.RelativeAmount))
            SchedulePreview(afterTypingPause: true);
        else if (e.PropertyName is nameof(SmartConditionRowViewModel.Field)
            or nameof(SmartConditionRowViewModel.Operator)
            or nameof(SmartConditionRowViewModel.DateValue)
            or nameof(SmartConditionRowViewModel.SecondDateValue)
            or nameof(SmartConditionRowViewModel.SelectedRelativeUnit)
            or nameof(SmartConditionRowViewModel.BoolValue)
            or nameof(SmartConditionRowViewModel.Playlist))
            SchedulePreview(afterTypingPause: false);
    }

    private void OnConditionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (SmartConditionRowViewModel row in e.OldItems ?? Array.Empty<SmartConditionRowViewModel>())
            row.PropertyChanged -= OnRowChanged;
        foreach (SmartConditionRowViewModel row in e.NewItems ?? Array.Empty<SmartConditionRowViewModel>())
            row.PropertyChanged += OnRowChanged;

        SchedulePreview(afterTypingPause: false);
    }

    // Everything on the editor itself that changes what the rules pick. Name
    // and Error do not, and the preview's own properties are what a pass sets.
    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_refreshingPreview)
            return;

        if (e.PropertyName is nameof(LimitAmount))
            SchedulePreview(afterTypingPause: true);
        else if (e.PropertyName is nameof(MatchMode) or nameof(LimitEnabled)
            or nameof(LimitUnit) or nameof(LimitSelector))
            SchedulePreview(afterTypingPause: false);
    }

    // ── Save / cancel ─────────────────────────────────────────────────────────

    // True when the rules were stored, which is also when the window may close.
    // Everything that can go wrong lands in Error instead of throwing: the
    // caller is a Save button, not a service.
    public bool Save()
    {
        Error = null;

        var conditions = new List<SmartCondition>(Conditions.Count);
        foreach (var row in Conditions)
        {
            if (!row.TryBuild(out var condition, out var error))
            {
                Error = error;
                return false;
            }
            conditions.Add(condition!);
        }

        SmartLimit? limit = null;
        if (LimitEnabled)
        {
            if (LimitAmount <= 0)
            {
                Error = "A limit of zero would leave the playlist permanently empty.";
                return false;
            }
            limit = new SmartLimit(LimitAmount, LimitUnit.Unit, LimitSelector.Selector);
        }

        var rules = new SmartPlaylistRules(MatchMode.Mode, conditions, limit, LiveUpdating);

        // Validate catches the shape errors a row cannot: an operator the field
        // does not support after a field change, a value kind that no longer
        // fits. Cheap, and the same check a rules blob arriving from a peer goes
        // through.
        if (SmartPlaylistEvaluator.Validate(rules) is { Count: > 0 } problems)
        {
            Error = string.Join(" ", problems);
            return false;
        }

        // Belt and braces over ReferenceCandidates, which already makes a loop
        // unpickable. Cheap enough to run, and the alternative to catching it
        // here is SmartPlaylistRefresher refusing every pass afterwards.
        var others = _library.Playlists
            .Where(p => p.Rules != null && p.Id != _playlist.Id)
            .ToDictionary(p => p.Id, p => p.Rules!);
        if (SmartPlaylistGraph.WouldCycle(_playlist.Id, rules, others))
        {
            Error = "These rules would make this playlist depend on itself.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Name) && Name != _playlist.Name)
            _playlist.Name = Name;

        // Touches UpdatedAt, unlike materialization - editing the rules is the
        // one thing about a smart playlist sync has to carry. That also raises
        // Library.PlaylistsChanged, which persists it.
        _playlist.Rules = rules;

        // Fill the contents in now rather than waiting for the debounced pass,
        // and regardless of LiveUpdating - a frozen playlist has to be
        // evaluated exactly once, at the moment it is defined.
        _refresher.RefreshOne(_playlist);
        _refresher.Schedule();
        StopPreview();

        return true;
    }

    // A playlist created solely to be edited should not survive the user
    // changing their mind - otherwise Cancel leaves an empty "New Smart
    // Playlist" in the sidebar, which is the one outcome nobody wanted.
    public void Cancel()
    {
        StopPreview();
        if (_isNew)
            _library.RemovePlaylist(_playlist);
    }
}

// What MainViewModel hands the view when the rule editor should open. IsNew
// means the playlist was created for this edit and should be removed again if
// the user cancels.
public sealed record SmartPlaylistEditorEventArgs(Playlist Playlist, bool IsNew);
