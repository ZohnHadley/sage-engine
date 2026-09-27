// Test classes run in parallel (issue #11). Two kinds of test must not, and each has its own
// non-parallel collection (TestSupport.cs): tests that change process-wide state on purpose (log
// levels, the rate limiter, the crash reporter), and tests that measure time or allocation, which
// other tests running beside them would skew.
[assembly: CollectionBehavior(DisableTestParallelization = false)]
