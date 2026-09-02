using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

// One "Genre is Jazz" row in the smart playlist editor.
//
// The row owns three things: which field/operator are picked, whatever the user
// has typed for each shape of value, and which value controls should be visible
// for the current pairing. The typed values are kept per-shape rather than in
// one box, so flipping from "is after 2019" to "is in the last 30 days" and
// back does not silently discard the date.
//
// Nothing here validates against the library - a row that cannot be turned into
// a condition says so through TryBuild, and the editor is what decides whether
// that blocks a save.
public sealed class SmartConditionRowViewModel : ViewModelBase
{
    // Display wrappers so the ComboBoxes can bind a name without a converter
    // each. The operator's name depends on the field's kind ("is after" for a
    // date, "is greater than" for a number), which is why this is built per
    // row rather than being a static table.
    public sealed record FieldOption(SmartField Field, string Name)
    {
        public override string ToString() => Name;
    }

    public sealed record OperatorOption(SmartOperator Operator, string Name)
    {
        public override string ToString() => Name;
    }

    public sealed record RelativeUnitOption(RelativeUnit Unit, string Name)
    {
        public override string ToString() => Name;
    }

    // A playlist a membership rule may point at. Only ever built from
    // SmartPlaylistGraph.ReferenceCandidates, which is what keeps a cycle
    // unrepresentable rather than merely refused on save.
    public sealed record PlaylistOption(Guid Id, string Name)
    {
        public override string ToString() => Name;
    }

    public static ImmutableArray<FieldOption> AllFields { get; } =
        [.. SmartPlaylistFields.All.Select(d => new FieldOption(d.Field, d.DisplayName))];

    public static ImmutableArray<RelativeUnitOption> AllRelativeUnits { get; } =
        [.. Enum.GetValues<RelativeUnit>().Select(u => new RelativeUnitOption(u, SmartPlaylistLabels.Name(u)))];

    // "Yes"/"No" rather than a CheckBox, so a bool row reads as the same
    // three-part sentence as every other row: "Starred / is / Yes".
    public static ImmutableArray<string> BoolOptions { get; } = ["Yes", "No"];

    // Instance-facing views of the three static tables above: a compiled
    // binding resolves its path against x:DataType's instance members, so a
    // ComboBox cannot bind the static property directly.
    public ImmutableArray<FieldOption> Fields => AllFields;

    public ImmutableArray<RelativeUnitOption> RelativeUnits => AllRelativeUnits;

    public ImmutableArray<string> BoolValues => BoolOptions;

    private readonly SmartPlaylistEditorViewModel _owner;

    public SmartConditionRowViewModel(SmartPlaylistEditorViewModel owner, SmartCondition? condition = null)
    {
        _owner = owner;

        _field = AllFields.FirstOrDefault(f => f.Field == condition?.Field) ?? AllFields[0];
        _operators = OperatorsFor(_field.Field);
        _operator = _operators.FirstOrDefault(o => o.Operator == condition?.Operator) ?? _operators[0];

        if (condition != null)
            Load(condition.Value);

        RefreshPlaylists();
    }

    // ── Field / operator ──────────────────────────────────────────────────────

    private FieldOption _field;
    public FieldOption Field
    {
        get => _field;
        set
        {
            if (value == null || value == _field)
                return;

            _field = value;
            OnPropertyChanged();

            // The operator list is per-kind, so changing Title to Year can
            // leave the current operator unavailable. Keep it when it survives
            // the move (Is/IsNot exist for everything) rather than resetting
            // the row for no reason.
            Operators = OperatorsFor(_field.Field);
            if (Operators.All(o => o.Operator != _operator.Operator))
                Operator = Operators[0];
            else
                Operator = Operators.First(o => o.Operator == _operator.Operator);

            RefreshPlaylists();
            NotifyLayoutChanged();
        }
    }

    private ImmutableArray<OperatorOption> _operators;
    public ImmutableArray<OperatorOption> Operators
    {
        get => _operators;
        private set { _operators = value; OnPropertyChanged(); }
    }

    private OperatorOption _operator;
    public OperatorOption Operator
    {
        get => _operator;
        set
        {
            if (value == null || value == _operator)
                return;

            _operator = value;
            OnPropertyChanged();
            NotifyLayoutChanged();
        }
    }

    private SmartValueKind Kind => SmartPlaylistFields.KindOf(_field.Field);

    private static ImmutableArray<OperatorOption> OperatorsFor(SmartField field)
    {
        var kind = SmartPlaylistFields.KindOf(field);
        return [.. SmartPlaylistFields.OperatorsFor(kind).Select(op => new OperatorOption(op, SmartPlaylistLabels.Name(op, kind)))];
    }

    // ── The typed values, one per shape ───────────────────────────────────────

    private string _valueText = string.Empty;
    public string ValueText
    {
        get => _valueText;
        set { _valueText = value ?? string.Empty; OnPropertyChanged(); }
    }

    private string _secondValueText = string.Empty;
    public string SecondValueText
    {
        get => _secondValueText;
        set { _secondValueText = value ?? string.Empty; OnPropertyChanged(); }
    }

    private DateTimeOffset? _dateValue = DateTimeOffset.Now;
    public DateTimeOffset? DateValue
    {
        get => _dateValue;
        set { _dateValue = value; OnPropertyChanged(); }
    }

    private DateTimeOffset? _secondDateValue = DateTimeOffset.Now;
    public DateTimeOffset? SecondDateValue
    {
        get => _secondDateValue;
        set { _secondDateValue = value; OnPropertyChanged(); }
    }

    private int _relativeAmount = 30;
    public int RelativeAmount
    {
        get => _relativeAmount;
        set { _relativeAmount = value; OnPropertyChanged(); }
    }

    private RelativeUnitOption _relativeUnit = AllRelativeUnits.First(u => u.Unit == Models.RelativeUnit.Days);
    public RelativeUnitOption SelectedRelativeUnit
    {
        get => _relativeUnit;
        set { if (value != null) { _relativeUnit = value; OnPropertyChanged(); } }
    }

    private string _boolValue = BoolOptions[0];
    public string BoolValue
    {
        get => _boolValue;
        set { if (value != null) { _boolValue = value; OnPropertyChanged(); } }
    }

    private ImmutableArray<PlaylistOption> _playlists = [];
    public ImmutableArray<PlaylistOption> Playlists
    {
        get => _playlists;
        private set { _playlists = value; OnPropertyChanged(); }
    }

    private PlaylistOption? _playlist;
    public PlaylistOption? Playlist
    {
        get => _playlist;
        set { _playlist = value; OnPropertyChanged(); }
    }

    // The candidate list is a property of the whole editor (it depends on every
    // other playlist's rules), so the row asks rather than computes - and asks
    // again whenever the field changes, since a playlist added in another row
    // does not change it but a rules edit elsewhere would.
    public void RefreshPlaylists()
    {
        if (Kind != SmartValueKind.Playlist)
            return;

        var previous = _playlist?.Id;
        Playlists = _owner.PlaylistCandidates;
        Playlist = Playlists.FirstOrDefault(p => p.Id == previous) ?? Playlists.FirstOrDefault();
    }

    // ── Which value controls the row shows ────────────────────────────────────

    private bool NeedsNoValue => Operator.Operator is SmartOperator.IsEmpty or SmartOperator.IsNotEmpty;

    private bool IsBetween => Operator.Operator == SmartOperator.Between;

    public bool ShowValueBox => !NeedsNoValue && Kind is SmartValueKind.Text or SmartValueKind.Number or SmartValueKind.Duration;

    public bool ShowDateBox => !NeedsNoValue && Kind == SmartValueKind.Date
                               && Operator.Operator is not (SmartOperator.InTheLast or SmartOperator.NotInTheLast);

    public bool ShowRelative => Kind == SmartValueKind.Date
                                && Operator.Operator is SmartOperator.InTheLast or SmartOperator.NotInTheLast;

    public bool ShowBoolBox => Kind == SmartValueKind.Bool;

    public bool ShowPlaylistBox => Kind == SmartValueKind.Playlist;

    public bool ShowSecondValueBox => IsBetween && ShowValueBox;

    public bool ShowSecondDateBox => IsBetween && ShowDateBox;

    public bool ShowRangeSeparator => ShowSecondValueBox || ShowSecondDateBox;

    // Told rather than shown for a duration, since the row is already three
    // controls wide and has no space for a format label. Null everywhere else,
    // which leaves the box with no tooltip at all.
    public string? ValueHint => Kind == SmartValueKind.Duration ? "A length, written as m:ss - 3:30, or 1:03:30." : null;

    private void NotifyLayoutChanged()
    {
        OnPropertyChanged(nameof(ShowValueBox));
        OnPropertyChanged(nameof(ShowDateBox));
        OnPropertyChanged(nameof(ShowRelative));
        OnPropertyChanged(nameof(ShowBoolBox));
        OnPropertyChanged(nameof(ShowPlaylistBox));
        OnPropertyChanged(nameof(ShowSecondValueBox));
        OnPropertyChanged(nameof(ShowSecondDateBox));
        OnPropertyChanged(nameof(ShowRangeSeparator));
        OnPropertyChanged(nameof(ValueHint));
    }

    // ── Reading a stored condition back into the row ──────────────────────────

    private void Load(SmartValue value)
    {
        switch (value)
        {
            case SmartValue.Text text:
                _valueText = text.Value;
                break;
            case SmartValue.Number number:
                _valueText = Format(number.Value);
                break;
            case SmartValue.Duration duration:
                _valueText = Format(duration.Value);
                break;
            case SmartValue.Date date:
                _dateValue = date.Value;
                break;
            case SmartValue.Relative relative:
                _relativeAmount = relative.Amount;
                _relativeUnit = AllRelativeUnits.First(u => u.Unit == relative.Unit);
                break;
            case SmartValue.Bool flag:
                _boolValue = flag.Value ? BoolOptions[0] : BoolOptions[1];
                break;
            case SmartValue.PlaylistRef reference:
                // Resolved against the candidate list in RefreshPlaylists,
                // which the constructor runs after this. A reference the list
                // no longer offers (the playlist was deleted) falls back to
                // whatever is first rather than leaving the row unbuildable.
                _playlist = new PlaylistOption(reference.PlaylistId, string.Empty);
                break;
            case SmartValue.Range range:
                Load(range.From);
                switch (range.To)
                {
                    case SmartValue.Number number:
                        _secondValueText = Format(number.Value);
                        break;
                    case SmartValue.Duration duration:
                        _secondValueText = Format(duration.Value);
                        break;
                    case SmartValue.Date date:
                        _secondDateValue = date.Value;
                        break;
                }
                break;
        }
    }

    // ── Turning the row back into a condition ─────────────────────────────────

    public bool TryBuild(out SmartCondition? condition, out string? error)
    {
        condition = null;
        error = null;

        if (!TryBuildValue(out var value, out error))
            return false;

        condition = new SmartCondition(Field.Field, Operator.Operator, value!);
        return true;
    }

    private bool TryBuildValue(out SmartValue? value, out string? error)
    {
        value = null;
        error = null;

        if (NeedsNoValue)
        {
            value = SmartValue.None.Instance;
            return true;
        }

        if (IsBetween && Kind is SmartValueKind.Number or SmartValueKind.Duration or SmartValueKind.Date)
        {
            if (!TryBuildSingle(ValueText, DateValue, out var from, out error))
                return false;
            if (!TryBuildSingle(SecondValueText, SecondDateValue, out var to, out error))
                return false;

            value = new SmartValue.Range(from!, to!);
            return true;
        }

        return TryBuildSingle(ValueText, DateValue, out value, out error);
    }

    private bool TryBuildSingle(string text, DateTimeOffset? date, out SmartValue? value, out string? error)
    {
        value = null;
        error = null;

        switch (Kind)
        {
            case SmartValueKind.Text:
                value = new SmartValue.Text(text);
                return true;

            case SmartValueKind.Number:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number)
                    && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    error = $"“{text}” is not a number.";
                    return false;
                }
                value = new SmartValue.Number(number);
                return true;

            case SmartValueKind.Duration:
                if (!TryParseDuration(text, out var duration))
                {
                    error = $"“{text}” is not a length - write it as m:ss.";
                    return false;
                }
                value = new SmartValue.Duration(duration);
                return true;

            case SmartValueKind.Date:
                if (ShowRelative)
                {
                    if (RelativeAmount <= 0)
                    {
                        error = "A relative date needs a positive amount.";
                        return false;
                    }
                    value = new SmartValue.Relative(RelativeAmount, SelectedRelativeUnit.Unit);
                    return true;
                }
                if (date is not { } instant)
                {
                    error = "Pick a date.";
                    return false;
                }
                value = new SmartValue.Date(instant);
                return true;

            case SmartValueKind.Bool:
                value = new SmartValue.Bool(BoolValue == BoolOptions[0]);
                return true;

            case SmartValueKind.Playlist:
                if (Playlist is not { } playlist)
                {
                    error = "There is no other playlist this one can refer to.";
                    return false;
                }
                value = new SmartValue.PlaylistRef(playlist.Id);
                return true;

            default:
                error = $"{Field.Name} cannot be compared.";
                return false;
        }
    }

    // Accepts "210", "3:30" and "1:03:30" - the three ways someone reaches for
    // a length - rather than only TimeSpan.Parse's own "hh:mm:ss", which reads
    // "3:30" as three and a half hours.
    internal static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = default;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        var parts = trimmed.Split(':');
        if (parts.Length == 1)
        {
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return false;
            duration = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (parts.Length > 3)
            return false;

        var total = 0d;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var component) || component < 0)
                return false;
            total = total * 60 + component;
        }

        duration = TimeSpan.FromSeconds(total);
        return true;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
}
