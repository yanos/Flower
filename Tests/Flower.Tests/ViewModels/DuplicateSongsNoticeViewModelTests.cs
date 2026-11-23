using Flower.Persistence;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// The banner about songs held twice: said once per duplicate, whichever way
// it is answered, and again only for one it has not mentioned.
public class DuplicateSongsNoticeViewModelTests
{
    private sealed class Host
    {
        public AppSettings Settings { get; } = new();
        public int Shows { get; private set; }
        public DuplicateSongsNoticeViewModel Notice { get; }

        public Host() => Notice = new DuplicateSongsNoticeViewModel(Settings, () => { }, () => Shows++);
    }

    [Fact]
    public void Nothing_to_say_without_duplicates()
    {
        var host = new Host();

        host.Notice.Update([]);

        Assert.False(host.Notice.IsOpen);
    }

    [Fact]
    public void Dismissing_it_lasts_until_a_new_duplicate_turns_up()
    {
        var host = new Host();
        host.Notice.Update(["a", "b"]);
        Assert.True(host.Notice.IsOpen);

        host.Notice.DismissCommand.Execute(null);
        host.Notice.Update(["a", "b"]);
        Assert.False(host.Notice.IsOpen);

        host.Notice.Update(["a"]);
        Assert.False(host.Notice.IsOpen);

        host.Notice.Update(["a", "c"]);
        Assert.True(host.Notice.IsOpen);
    }

    [Fact]
    public void Showing_them_opens_the_page_and_answers_it()
    {
        var host = new Host();
        host.Notice.Update(["a"]);

        host.Notice.ShowCommand.Execute(null);

        Assert.Equal(1, host.Shows);
        Assert.False(host.Notice.IsOpen);
    }
}
