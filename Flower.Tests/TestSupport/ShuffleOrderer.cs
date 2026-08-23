using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;
using Xunit.Sdk;
using Xunit.v3;

[assembly: TestCaseOrderer(typeof(Flower.Tests.TestSupport.ShuffleOrderer))]
[assembly: TestCollectionOrderer(typeof(Flower.Tests.TestSupport.ShuffleCollectionOrderer))]

namespace Flower.Tests.TestSupport;

// Runs the suite in a seeded random order when FLOWER_TEST_SEED is set to a
// non-zero integer, and leaves the order exactly as it was otherwise. Inert by
// default, so this costs a normal run nothing.
//
// It exists because the suite runs PerAssembly isolation (see TestAppBuilder),
// which means Avalonia's Application, locator and Dispatcher are built once and
// shared by every test rather than rebuilt around each one. The fair objection
// to that is "then a different order could give a different result", and this
// is how to check rather than assume:
//
//   for seed in 11 22 33 44 55 66 77 88 99; do
//     FLOWER_TEST_SEED=$seed dotnet test Flower.Tests/Flower.Tests.csproj \
//       --filter 'Category!=RequiresLibVLC'
//   done
//
// Worth running after anything that touches shared test state. The first ten
// seeds it was run against turned up one failure, and it was not shared Avalonia
// state - it was CurrentlyPlayingControlViewModelTests asserting an order that
// two independent Task.Runs never promised.
internal static class Shuffle
{
    public static readonly int Seed =
        int.TryParse(Environment.GetEnvironmentVariable("FLOWER_TEST_SEED"), out var s) ? s : 0;

    public static IReadOnlyCollection<T> Order<T>(IReadOnlyCollection<T> items, int salt)
    {
        if (Seed == 0)
            return items;

        var rng = new Random(Seed + salt);
        return [.. items.OrderBy(_ => rng.Next())];
    }
}

public sealed class ShuffleOrderer : ITestCaseOrderer
{
    public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases)
        where TTestCase : notnull, ITestCase => Shuffle.Order(testCases, 1);
}

public sealed class ShuffleCollectionOrderer : ITestCollectionOrderer
{
    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : notnull, ITestCollection => Shuffle.Order(testCollections, 2);
}
