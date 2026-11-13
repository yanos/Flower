using System;
using System.Collections.Generic;

namespace Flower.Services;

public static class RecentSearchHistory
{
    public const int MaxCount = 20;

    // Returns whether the stored history changed, so callers only persist and
    // notify when needed. Entries retain the latest spelling of a query.
    public static bool Remember(List<string> recent, string? query)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed) || (recent.Count > 0 && recent[0] == trimmed))
            return false;

        recent.RemoveAll(q => string.Equals(q, trimmed, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, trimmed);
        if (recent.Count > MaxCount)
            recent.RemoveRange(MaxCount, recent.Count - MaxCount);
        return true;
    }
}
