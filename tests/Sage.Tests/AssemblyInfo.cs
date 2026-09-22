// Log, LogCat defaults, UserPaths and the crash reporter are process-wide, so tests that touch them
// must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
