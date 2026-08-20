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
