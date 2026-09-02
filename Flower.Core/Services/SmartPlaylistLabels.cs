using System;

using Flower.Models;

namespace Flower.Services;

// Display text for the enums a rule is made of. Beside SmartPlaylistFields
// (which names the fields) rather than inside the editor, for the same reason
// that registry exists: the desktop editor, the eventual browser one, and any
// error message that has to say what went wrong should all call an operator by
// the same name.
//
// Deliberately not [Description] attributes read by reflection - that is a
// trim/AOT hazard for an app that publishes AOT-safe (see
// SmartPlaylistRulesJsonContext), and a switch that fails to compile when an
// enum member is added is the better failure.
public static class SmartPlaylistLabels
{
    public static string Name(SmartOperator op) => op switch
    {
        SmartOperator.Is             => "is",
        SmartOperator.IsNot          => "is not",
        SmartOperator.Contains       => "contains",
        SmartOperator.DoesNotContain => "does not contain",
        SmartOperator.StartsWith     => "starts with",
        SmartOperator.EndsWith       => "ends with",
        SmartOperator.GreaterThan    => "is greater than",
        SmartOperator.LessThan       => "is less than",
        SmartOperator.Between        => "is between",
        SmartOperator.InTheLast      => "is in the last",
        SmartOperator.NotInTheLast   => "is not in the last",
        SmartOperator.IsEmpty        => "is empty",
        SmartOperator.IsNotEmpty     => "is not empty",
        _ => op.ToString(),
    };

    // "is greater than" reads wrong for a date - later is not greater. Same
    // operator, different word, which is why this takes the kind too.
    public static string Name(SmartOperator op, SmartValueKind kind) => (op, kind) switch
    {
        (SmartOperator.GreaterThan, SmartValueKind.Date) => "is after",
        (SmartOperator.LessThan,    SmartValueKind.Date) => "is before",
        (SmartOperator.Is,          SmartValueKind.Bool) => "is",
        _ => Name(op),
    };

    public static string Name(RelativeUnit unit) => unit switch
    {
        RelativeUnit.Minutes => "minutes",
        RelativeUnit.Hours   => "hours",
        RelativeUnit.Days    => "days",
        RelativeUnit.Weeks   => "weeks",
        RelativeUnit.Months  => "months",
        RelativeUnit.Years   => "years",
        _ => unit.ToString(),
    };

    public static string Name(LimitUnit unit) => unit switch
    {
        LimitUnit.Items   => "items",
        LimitUnit.Minutes => "minutes",
        LimitUnit.Hours   => "hours",
        _ => unit.ToString(),
    };

    public static string Name(LimitSelector selector) => selector switch
    {
        LimitSelector.Random              => "random",
        LimitSelector.Title               => "title",
        LimitSelector.Artist              => "artist",
        LimitSelector.Album               => "album",
        LimitSelector.MostPlayed          => "most often played",
        LimitSelector.LeastPlayed         => "least often played",
        LimitSelector.MostRecentlyPlayed  => "most recently played",
        LimitSelector.LeastRecentlyPlayed => "least recently played",
        LimitSelector.MostRecentlyAdded   => "most recently added",
        LimitSelector.LeastRecentlyAdded  => "least recently added",
        _ => selector.ToString(),
    };

    public static string Name(MatchMode mode) => mode switch
    {
        MatchMode.All => "all",
        MatchMode.Any => "any",
        _ => mode.ToString(),
    };
}
