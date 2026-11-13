using Flower.Services;

namespace Flower.Tests.ViewModels;

public class RecentSearchHistoryTests
{
    [Fact]
    public void The_latest_search_goes_first_and_a_repeat_moves_rather_than_doubles()
    {
        var recent = new List<string>();
        RecentSearchHistory.Remember(recent, "radiohead");
        RecentSearchHistory.Remember(recent, "bjork");
        Assert.True(RecentSearchHistory.Remember(recent, "  Radiohead "));

        Assert.Equal(new[] { "Radiohead", "bjork" }, recent);
        Assert.False(RecentSearchHistory.Remember(recent, "Radiohead"));
        Assert.False(RecentSearchHistory.Remember(recent, "   "));
        Assert.False(RecentSearchHistory.Remember(recent, null));
        Assert.Equal(new[] { "Radiohead", "bjork" }, recent);
    }

    [Fact]
    public void Only_so_many_are_kept()
    {
        var recent = new List<string>();
        for (var i = 0; i < RecentSearchHistory.MaxCount + 5; i++)
        {
            RecentSearchHistory.Remember(recent, $"query {i}");
        }

        Assert.Equal(RecentSearchHistory.MaxCount, recent.Count);
        Assert.Equal($"query {RecentSearchHistory.MaxCount + 4}", recent[0]);
        Assert.Equal("query 5", recent[^1]);
    }
}
