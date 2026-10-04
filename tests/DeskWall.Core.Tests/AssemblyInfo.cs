using Xunit;

// Surface holds process-wide COM factories and the tests touch the real desktop; run classes serially.
// DESKWALL_HOME and every temp file belong to this run alone: see tests/Shared/TestRun.cs.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
