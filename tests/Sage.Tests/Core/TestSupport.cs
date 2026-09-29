#nullable enable

namespace Sage.Tests;

// Tests that change process-wide state on purpose — the default log level, per-category levels, the
// log's rate limiter, the crash reporter's sections — and would change it under a test running beside
// them. Run alone, after the parallel ones.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideStateCollection { public const string Name = "Process-wide state"; }

// Tests that measure time or allocation. Physics shares its work between the test's thread and Bepu's
// workers, so how much of it (and its allocations) lands on the measured thread depends on what else
// the machine is doing; a measurement is only a measurement when nothing else is running.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MeasurementsCollection { public const string Name = "Measurements"; }

// TestEnv, CaptureSink, MountFixture, EventProbe and HeadlessApp live in tests/Sage.Testing, where a
// game's tests can use them too.

// Counts how often it is formatted, to prove disabled log calls format nothing.
internal sealed class FormatProbe
{
    public int Count;
    public override string ToString() { Count++; return "probe"; }
}
