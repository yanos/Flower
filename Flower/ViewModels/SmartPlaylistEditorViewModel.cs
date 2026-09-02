using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;

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

        _name = playlist.Name;
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
    }

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
    }

    public bool CanRemoveConditions => Conditions.Count > 1;

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

        return true;
    }

    // A playlist created solely to be edited should not survive the user
    // changing their mind - otherwise Cancel leaves an empty "New Smart
    // Playlist" in the sidebar, which is the one outcome nobody wanted.
    public void Cancel()
    {
        if (_isNew)
            _library.RemovePlaylist(_playlist);
    }
}

// What MainViewModel hands the view when the rule editor should open. IsNew
// means the playlist was created for this edit and should be removed again if
// the user cancels.
public sealed record SmartPlaylistEditorEventArgs(Playlist Playlist, bool IsNew);
