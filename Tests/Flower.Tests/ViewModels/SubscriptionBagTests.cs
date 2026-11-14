using Flower.Services;

namespace Flower.Tests.ViewModels;

public class SubscriptionBagTests
{
    [Fact]
    public void A_subscription_bag_unsubscribes_once_however_often_it_is_disposed()
    {
        var source = new EventSource();
        var bag = new SubscriptionBag();
        var seen = 0;

        bag.Add<System.EventHandler>((_, _) => seen++,
            h => source.Fired += h, h => source.Fired -= h);

        source.Raise();
        Assert.Equal(1, seen);

        bag.Dispose();
        bag.Dispose();

        source.Raise();
        Assert.Equal(1, seen);
        Assert.Equal(0, bag.Count);
    }

    private sealed class EventSource
    {
        public event System.EventHandler? Fired;
        public void Raise() => Fired?.Invoke(this, System.EventArgs.Empty);
    }
}
