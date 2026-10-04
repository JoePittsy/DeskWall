using Xunit;

// Same as the Core tests: classes run serially, and DESKWALL_HOME is this run's own (tests/Shared/TestRun.cs).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
