using System;
using System.Collections.Generic;
using System.Linq;

using Material.Icons;

namespace Flower.ViewModels;

public enum HomeShelf
{
    ContinuePlaying,
    RecentlyPlayed,
    RecentlyAdded,
}

// The order Home's shelves come in, top to bottom - the user's, dragged into
// place in Settings on either head and kept as AppSettings.HomeShelfOrder.
// Every shelf is always somewhere in it: hiding one is its own setting's job
// (a limit of 0), so an order cannot lose a shelf the way the tab bar can
// leave a tab out.
public static class HomeShelfOrder
{
    public static readonly IReadOnlyList<HomeShelf> Default =
        [HomeShelf.ContinuePlaying, HomeShelf.RecentlyPlayed, HomeShelf.RecentlyAdded];

    // A saved order, cleaned: names that are no longer shelves and repeats are
    // dropped, and a shelf it does not name goes back in after the rest, in
    // its default place among them.
    public static IReadOnlyList<HomeShelf> Parse(IEnumerable<string>? saved)
    {
        if (saved == null)
            return Default;

        var order = new List<HomeShelf>();
        foreach (var name in saved)
        {
            if (Enum.TryParse<HomeShelf>(name, out var shelf) && Default.Contains(shelf) && !order.Contains(shelf))
                order.Add(shelf);
        }

        order.AddRange(Default.Where(shelf => !order.Contains(shelf)));
        return order;
    }

    public static IReadOnlyList<HomeShelf> Move(IReadOnlyList<HomeShelf> order, HomeShelf shelf, int index)
    {
        var moved = order.ToList();
        if (!moved.Remove(shelf))
            return order;

        moved.Insert(Math.Clamp(index, 0, moved.Count), shelf);
        return moved;
    }

    public static string Title(HomeShelf shelf) => shelf switch
    {
        HomeShelf.ContinuePlaying => "Continue Playing",
        HomeShelf.RecentlyPlayed => "Recently Played",
        _ => "Recently Added",
    };

    public static MaterialIconKind Icon(HomeShelf shelf) => shelf switch
    {
        HomeShelf.ContinuePlaying => MaterialIconKind.PlayCircleOutline,
        HomeShelf.RecentlyPlayed => MaterialIconKind.History,
        _ => MaterialIconKind.CalendarClock,
    };
}
