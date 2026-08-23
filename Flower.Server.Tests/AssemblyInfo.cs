using Xunit;

// Every test in this assembly either boots a real host or reads a store whose
// location comes from PlatformDataDirectory.Current - and that is a
// process-global (see Program.cs, which sets it during startup from
// configuration). Two hosts with different data directories running at once
// means whichever started last decides where *both* of them read trusted
// peers and Subsonic credentials from, so a request authenticated against one
// fixture's credential can be checked against another's empty store.
//
// Program.cs already noted this hazard for FlowerDb, which it works around by
// building its own path instead of using the global. The stores can't do the
// same - they are shared with the client, where the global is the right answer
// - so the assembly runs serially instead.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Same floor as Flower.Tests/TestSupport/AssemblySetup.cs, for the same reason:
// the classes here that redirect PlatformDataDirectory.Current capture and
// restore whatever it was, and what it *was* is null - the developer's own
// ~/Library/Application Support/Flower. Anything writing outside a pinned
// class's lifetime (a fire-and-forget save still in flight) therefore lands on
// real user data. Over in the client assembly that is exactly what happened,
// and it cost a settings.json and its only backup. Give this one a floor before
// it does the same.
internal static class ServerTestDataDirectory
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    public static void KeepEveryTestOutOfTheRealApplicationSupportDirectory()
    {
        Flower.Persistence.PlatformDataDirectory.Current =
            System.IO.Directory.CreateTempSubdirectory("flower-server-test-appdata").FullName;
    }
}
