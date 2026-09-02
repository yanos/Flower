using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// What a smart playlist thrown at a library produces. Pure: no I/O, no clock of
// its own, no randomness of its own - everything time- or chance-dependent
// arrives in the context, which is what makes "in the last 30 days" testable
// without waiting a month.
//
// Deliberately in-memory rather than translated to SQL. Library is fully
// resident and the deployment model is one owner with single-digit listeners
// (CLAUDE.md, "How It Gets Used"), so a whole-library pass per playlist is
// microseconds; a rules-to-SQL translator would be a second implementation of
// every operator here, with its own NULL and collation semantics to disagree
// with these. See docs/SMART-PLAYLIST-PLAN.md.
public static class SmartPlaylistEvaluator
{
    public static List<Track> Evaluate(
        SmartPlaylistRules rules,
        IReadOnlyList<Track> tracks,
        SmartPlaylistContext context)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(context);

        var matched = tracks.Where(track => Matches(track, rules, context)).ToList();
        return rules.Limit is { } limit ? Apply(limit, matched, context) : matched;
    }

    // Every smart playlist on the device, in one pass.
    //
    // The entry point Phase 3's recomputation will call, and the only place the
    // dependency order is honoured: a playlist that references another is
    // evaluated after it, against its freshly computed contents rather than
    // last time's. Doing this per playlist, reactively, is what would make the
    // order emergent - and wrong on the first pass after a rescan.
    //
    // ordinaryMembership answers for playlists that are not smart (and for ids
    // this device does not have, by returning null). Throws
    // SmartPlaylistCycleException if the rules on disk form a loop.
    public static Dictionary<Guid, List<Track>> EvaluateAll(
        IReadOnlyDictionary<Guid, SmartPlaylistRules> smart,
        IReadOnlyList<Track> library,
        Func<Guid, IReadOnlySet<Guid>?>? ordinaryMembership,
        DateTimeOffset now,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(smart);
        ArgumentNullException.ThrowIfNull(library);

        var results = new Dictionary<Guid, List<Track>>(smart.Count);
        var memberIds = new Dictionary<Guid, IReadOnlySet<Guid>>(smart.Count);

        IReadOnlySet<Guid>? Membership(Guid id) =>
            memberIds.TryGetValue(id, out var members) ? members : ordinaryMembership?.Invoke(id);

        foreach (var id in SmartPlaylistGraph.EvaluationOrder(smart))
        {
            // A context per playlist rather than one for the pass, purely so
            // each gets its own deterministic Random. A LimitSelector.Random
            // playlist is re-evaluated on every recompute - which is every
            // play, since play counts are an input - and with a shared or
            // ambient Random it would draw a different 25 songs each time,
            // reshuffling itself under a listener who is partway through it.
            // Seeded from the playlist id, the same candidate set always yields
            // the same pick, so the contents only move when the library does.
            // An explicit Random still wins, for tests that want a fixed draw.
            var context = new SmartPlaylistContext(now, Membership, random ?? SeededFor(id));

            var tracks = Evaluate(smart[id], library, context);
            results[id] = tracks;
            memberIds[id] = tracks.Select(t => t.Id).ToHashSet();
        }

        return results;
    }

    // Guid.GetHashCode would do, but spelling the seed out keeps it stable by
    // construction rather than by an implementation detail of Guid.
    private static Random SeededFor(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return new Random(BitConverter.ToInt32(bytes) ^ BitConverter.ToInt32(bytes[4..]));
    }

    public static bool Matches(Track track, SmartPlaylistRules rules, SmartPlaylistContext context)
    {
        // No conditions is "the whole library", not "nothing" - the state a
        // freshly created smart playlist is in before its first rule is typed,
        // and Any over an empty set would otherwise make it silently empty.
        if (rules.Conditions.Count == 0)
            return true;

        return rules.Mode == MatchMode.All
            ? rules.Conditions.All(condition => Matches(track, condition, context))
            : rules.Conditions.Any(condition => Matches(track, condition, context));
    }

    public static bool Matches(Track track, SmartCondition condition, SmartPlaylistContext context)
    {
        var descriptor = SmartPlaylistFields.For(condition.Field);
        if (!SmartPlaylistFields.Supports(condition.Field, condition.Operator))
            throw new SmartPlaylistRuleException($"{condition.Field} does not support the operator {condition.Operator}.");

        // Before anything reads the track, and deliberately not further down
        // where each operator picks its value apart. A malformed condition has
        // to be malformed for every track, not only for the ones that happen to
        // have that field filled in: "year is <the text 1979>" was reporting a
        // clean miss on a track with no year, so a rule that can never be
        // evaluated looked exactly like a rule that simply did not match - and
        // Validate, which checks conditions against a blank track, saw nothing
        // wrong with it either.
        EnsureValueFits(condition, descriptor.Kind);

        return descriptor.Kind switch
        {
            SmartValueKind.Text     => MatchesText(descriptor.Text!(track), condition),
            SmartValueKind.Number   => MatchesNumber(descriptor.Number!(track), condition),
            SmartValueKind.Duration => MatchesNumber(descriptor.Duration!(track)?.Ticks, condition),
            SmartValueKind.Date     => MatchesDate(descriptor.Date!(track), condition, context),
            SmartValueKind.Bool     => MatchesBool(descriptor.Bool!(track), condition),
            SmartValueKind.Playlist => MatchesPlaylist(track, condition, context),
            _ => throw new SmartPlaylistRuleException($"Unhandled value kind {descriptor.Kind}."),
        };
    }

    // The shape a condition's value must have, given its field's kind and its
    // operator. Three operators override the kind: IsEmpty/IsNotEmpty take no
    // value at all, Between takes a pair, and InTheLast takes a window rather
    // than an instant.
    private static void EnsureValueFits(SmartCondition condition, SmartValueKind kind)
    {
        switch (condition.Operator)
        {
            case SmartOperator.IsEmpty:
            case SmartOperator.IsNotEmpty:
                Expect<SmartValue.None>(condition);
                return;

            case SmartOperator.Between:
                var range = Expect<SmartValue.Range>(condition);
                EnsureLiteralFits(condition, kind, range.From);
                EnsureLiteralFits(condition, kind, range.To);
                return;

            case SmartOperator.InTheLast:
            case SmartOperator.NotInTheLast:
                Expect<SmartValue.Relative>(condition);
                return;

            default:
                EnsureLiteralFits(condition, kind, condition.Value);
                return;
        }
    }

    private static void EnsureLiteralFits(SmartCondition condition, SmartValueKind kind, SmartValue value)
    {
        var fits = kind switch
        {
            SmartValueKind.Text     => value is SmartValue.Text,
            SmartValueKind.Number   => value is SmartValue.Number,
            SmartValueKind.Duration => value is SmartValue.Duration,
            // A date comparison may be pinned to an instant or relative to now:
            // "added before 2020" and "added before 3 months ago" are both
            // things a person writes.
            SmartValueKind.Date     => value is SmartValue.Date or SmartValue.Relative,
            SmartValueKind.Bool     => value is SmartValue.Bool,
            SmartValueKind.Playlist => value is SmartValue.PlaylistRef,
            _ => false,
        };

        if (!fits)
            throw new SmartPlaylistRuleException($"{condition.Field} is a {kind} field and cannot be compared with {value.GetType().Name}.");
    }

    // Everything a field can be missing - an untagged genre, a track never
    // played, a year that is not a number - lands here, and the rule is the one
    // a listener would state out loud: a track with no genre is not "genre is
    // Rock", but it *is* "genre is not Rock". Positive operators fail on a
    // missing value, negative ones pass.
    private static bool MissingValueResult(SmartOperator op) => op switch
    {
        SmartOperator.IsNot or SmartOperator.DoesNotContain or SmartOperator.NotInTheLast or SmartOperator.IsEmpty => true,
        _ => false,
    };

    private static bool MatchesText(string? actual, SmartCondition condition)
    {
        if (condition.Operator is SmartOperator.IsEmpty or SmartOperator.IsNotEmpty)
            return string.IsNullOrWhiteSpace(actual) == (condition.Operator == SmartOperator.IsEmpty);

        if (string.IsNullOrEmpty(actual))
            return MissingValueResult(condition.Operator);

        var expected = Expect<SmartValue.Text>(condition).Value;

        return condition.Operator switch
        {
            // Case- and accent-insensitive throughout, via the same folding the
            // search box uses (TrackListBuilder.Filter -> SearchText). Two
            // different answers in one app to "does this track match Bjork?"
            // is a bug waiting to be filed.
            SmartOperator.Is             => TextEquals(actual, expected),
            SmartOperator.IsNot          => !TextEquals(actual, expected),
            SmartOperator.Contains       => SearchText.Contains(actual, expected),
            SmartOperator.DoesNotContain => !SearchText.Contains(actual, expected),
            SmartOperator.StartsWith     => EdgeEquals(actual, expected, atStart: true),
            SmartOperator.EndsWith       => EdgeEquals(actual, expected, atStart: false),
            _ => throw new SmartPlaylistRuleException($"{condition.Operator} is not a text operator."),
        };
    }

    private static bool TextEquals(string actual, string expected)
    {
        if (actual.Length != expected.Length)
            return false;

        return EdgeEquals(actual, expected, atStart: true);
    }

    private static bool EdgeEquals(string actual, string expected, bool atStart)
    {
        if (expected.Length > actual.Length)
            return false;

        var offset = atStart ? 0 : actual.Length - expected.Length;
        for (var i = 0; i < expected.Length; i++)
        {
            if (SearchText.Fold(actual[offset + i]) != SearchText.Fold(expected[i]))
                return false;
        }

        return true;
    }

    // Numbers and durations share every operator; a duration is compared as its
    // tick count, which is what TimeSpan is.
    private static bool MatchesNumber(double? actual, SmartCondition condition)
    {
        if (actual is not { } value)
            return MissingValueResult(condition.Operator);

        if (condition.Operator == SmartOperator.Between)
        {
            var (from, to) = ExpectRange(condition);
            var low = Math.Min(AsNumber(from, condition), AsNumber(to, condition));
            var high = Math.Max(AsNumber(from, condition), AsNumber(to, condition));
            return value >= low && value <= high;
        }

        var expected = AsNumber(condition.Value, condition);

        return condition.Operator switch
        {
            SmartOperator.Is          => value == expected,
            SmartOperator.IsNot       => value != expected,
            SmartOperator.GreaterThan => value > expected,
            SmartOperator.LessThan    => value < expected,
            _ => throw new SmartPlaylistRuleException($"{condition.Operator} is not a numeric operator."),
        };
    }

    private static double AsNumber(SmartValue value, SmartCondition condition) => value switch
    {
        SmartValue.Number number     => number.Value,
        SmartValue.Duration duration => duration.Value.Ticks,
        _ => throw new SmartPlaylistRuleException($"{condition.Field} needs a number, not {value.GetType().Name}."),
    };

    private static bool MatchesDate(DateTimeOffset? actual, SmartCondition condition, SmartPlaylistContext context)
    {
        if (condition.Operator is SmartOperator.IsEmpty or SmartOperator.IsNotEmpty)
            return (actual is null) == (condition.Operator == SmartOperator.IsEmpty);

        if (actual is not { } value)
            return MissingValueResult(condition.Operator);

        switch (condition.Operator)
        {
            case SmartOperator.InTheLast:
            case SmartOperator.NotInTheLast:
                var window = Resolve(Expect<SmartValue.Relative>(condition), context.Now);
                var inside = value >= window && value <= context.Now;
                return inside == (condition.Operator == SmartOperator.InTheLast);

            case SmartOperator.Between:
                var (from, to) = ExpectRange(condition);
                var a = AsInstant(from, context);
                var b = AsInstant(to, context);
                return value >= (a <= b ? a : b) && value <= (a <= b ? b : a);

            // "Is" on a date means the same day, not the same instant: a date
            // picker produces midnight, and no track was added at exactly
            // midnight. Compared in the offset the rule was written in, so
            // "added on the 3rd" means the 3rd where the person typing it was.
            case SmartOperator.Is:
            case SmartOperator.IsNot:
                var expected = Expect<SmartValue.Date>(condition).Value;
                var sameDay = value.ToOffset(expected.Offset).Date == expected.Date;
                return sameDay == (condition.Operator == SmartOperator.Is);

            case SmartOperator.GreaterThan:
                return value > AsInstant(condition.Value, context);
            case SmartOperator.LessThan:
                return value < AsInstant(condition.Value, context);

            default:
                throw new SmartPlaylistRuleException($"{condition.Operator} is not a date operator.");
        }
    }

    private static DateTimeOffset AsInstant(SmartValue value, SmartPlaylistContext context) => value switch
    {
        SmartValue.Date date         => date.Value,
        SmartValue.Relative relative => Resolve(relative, context.Now),
        _ => throw new SmartPlaylistRuleException($"Expected a date, not {value.GetType().Name}."),
    };

    // The one place a relative window becomes an instant, and it happens here,
    // at evaluation, every time - never at edit or save time. See
    // SmartValue.Relative's own remarks.
    public static DateTimeOffset Resolve(SmartValue.Relative relative, DateTimeOffset now) => relative.Unit switch
    {
        RelativeUnit.Minutes => now.AddMinutes(-relative.Amount),
        RelativeUnit.Hours   => now.AddHours(-relative.Amount),
        RelativeUnit.Days    => now.AddDays(-relative.Amount),
        RelativeUnit.Weeks   => now.AddDays(-7.0 * relative.Amount),
        // Calendar arithmetic, not 30- and 365-day approximations: "in the last
        // 3 months" should mean the same span in February as in July.
        RelativeUnit.Months  => now.AddMonths(-relative.Amount),
        RelativeUnit.Years   => now.AddYears(-relative.Amount),
        _ => throw new SmartPlaylistRuleException($"Unknown relative unit {relative.Unit}."),
    };

    private static bool MatchesBool(bool actual, SmartCondition condition)
    {
        var expected = Expect<SmartValue.Bool>(condition).Value;
        return condition.Operator == SmartOperator.Is ? actual == expected : actual != expected;
    }

    private static bool MatchesPlaylist(Track track, SmartCondition condition, SmartPlaylistContext context)
    {
        var reference = Expect<SmartValue.PlaylistRef>(condition);
        // A playlist this device does not have resolves to empty rather than
        // failing the rule - same tolerance playlist_tracks already shows for a
        // track id that no longer resolves, and the shape a half-synced peer
        // legitimately arrives in.
        var members = context.Membership(reference.PlaylistId);
        var contains = members is not null && members.Contains(track.Id);
        return contains == (condition.Operator == SmartOperator.Is);
    }

    private static T Expect<T>(SmartCondition condition) where T : SmartValue =>
        condition.Value as T
        ?? throw new SmartPlaylistRuleException(
            $"{condition.Field} {condition.Operator} needs a {typeof(T).Name} value, not {condition.Value.GetType().Name}.");

    private static (SmartValue From, SmartValue To) ExpectRange(SmartCondition condition)
    {
        var range = Expect<SmartValue.Range>(condition);
        return (range.From, range.To);
    }

    // "Limit to 25 items selected by least recently played". The selector
    // decides which matches survive, and it also decides the order they come
    // out in - the same as iTunes, and the reason a "least recently played"
    // playlist reads top-down as a to-listen list.
    private static List<Track> Apply(SmartLimit limit, List<Track> matched, SmartPlaylistContext context)
    {
        var ordered = Order(limit.SelectedBy, matched, context);

        if (limit.Unit == LimitUnit.Items)
            return ordered.Take(Math.Max(0, limit.Amount)).ToList();

        var budget = limit.Unit == LimitUnit.Hours
            ? TimeSpan.FromHours(limit.Amount)
            : TimeSpan.FromMinutes(limit.Amount);

        var taken = new List<Track>();
        var total = TimeSpan.Zero;
        foreach (var track in ordered)
        {
            // Stop before overshooting rather than after: a 60-minute limit
            // that produces 63 minutes will not fit on the thing it was sized
            // for. A later, shorter track is still allowed in - which is what
            // makes the budget worth filling.
            if (total + track.Duration > budget)
                continue;

            taken.Add(track);
            total += track.Duration;
        }

        return taken;
    }

    private static IEnumerable<Track> Order(LimitSelector selector, List<Track> matched, SmartPlaylistContext context) => selector switch
    {
        // Reshuffled on every recompute, deliberately, the same as iTunes: a
        // random playlist that never changes is not random. Seeded from the
        // context so tests get one answer twice.
        LimitSelector.Random => matched.OrderBy(_ => context.Random.Next()),

        LimitSelector.Title  => matched.OrderBy(t => t.TitleSortValue, StringComparer.CurrentCultureIgnoreCase),
        LimitSelector.Artist => matched.OrderBy(t => t.ArtistsSortValue, StringComparer.CurrentCultureIgnoreCase),
        LimitSelector.Album  => matched.OrderBy(t => t.AlbumSortValue, StringComparer.CurrentCultureIgnoreCase),

        LimitSelector.MostPlayed  => matched.OrderByDescending(t => t.TotalPlayCount),
        LimitSelector.LeastPlayed => matched.OrderBy(t => t.TotalPlayCount),

        // Never played sorts as least recently played, not as unknown: those
        // are exactly the tracks a "songs I have been neglecting" playlist is
        // for, and dropping them would leave it showing the second-best answer.
        LimitSelector.MostRecentlyPlayed  => matched.OrderByDescending(t => t.LastPlayedAt ?? DateTimeOffset.MinValue),
        LimitSelector.LeastRecentlyPlayed => matched.OrderBy(t => t.LastPlayedAt ?? DateTimeOffset.MinValue),

        LimitSelector.MostRecentlyAdded  => matched.OrderByDescending(t => t.DateAdded),
        LimitSelector.LeastRecentlyAdded => matched.OrderBy(t => t.DateAdded),

        _ => matched,
    };

    // Everything wrong with a rule that can be found without a library, so a
    // blob arriving from a peer (or from a newer Flower) can be checked once on
    // load instead of throwing in the middle of a library-wide recompute.
    // Empty means evaluable, not sensible - "year is 3000" is valid and matches
    // nothing.
    public static IReadOnlyList<string> Validate(SmartPlaylistRules rules)
    {
        var problems = new List<string>();

        foreach (var condition in rules.Conditions)
        {
            try
            {
                // The cheapest complete check there is: run the condition
                // against a blank track. Every shape error - unknown field,
                // wrong operator for the kind, wrong value type - throws, and
                // no other check can go stale as operators are added.
                Matches(new Track(), condition, SmartPlaylistContext.ForValidation);
            }
            catch (Exception e) when (e is SmartPlaylistRuleException or ArgumentOutOfRangeException)
            {
                problems.Add(e.Message);
            }
        }

        if (rules.Limit is { } limit && limit.Amount <= 0)
            problems.Add($"A limit of {limit.Amount} would leave the playlist permanently empty.");

        return problems;
    }
}

// What a smart playlist needs to know that is not in the rules or the library:
// the current time, how to resolve a referenced playlist, and where randomness
// comes from. Passed in rather than reached for so evaluation is reproducible.
public sealed class SmartPlaylistContext
{
    public SmartPlaylistContext(
        DateTimeOffset now,
        Func<Guid, IReadOnlySet<Guid>?>? membership = null,
        Random? random = null)
    {
        Now = now;
        _membership = membership;
        Random = random ?? Random.Shared;
    }

    private readonly Func<Guid, IReadOnlySet<Guid>?>? _membership;

    public DateTimeOffset Now { get; }

    public Random Random { get; }

    // The track ids in a referenced playlist, or null if this device has no
    // such playlist - see SmartPlaylistEvaluator.MatchesPlaylist.
    public IReadOnlySet<Guid>? Membership(Guid playlistId) => _membership?.Invoke(playlistId);

    // Used only by Validate, which runs conditions against a blank track purely
    // to see whether they are well-formed. Nothing it returns is a real answer.
    internal static readonly SmartPlaylistContext ForValidation = new(DateTimeOffset.UnixEpoch);
}

public sealed class SmartPlaylistRuleException(string message) : Exception(message);
