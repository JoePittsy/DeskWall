using Xunit;

// Surface holds process-wide COM factories and the tests touch the real desktop; run classes serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
