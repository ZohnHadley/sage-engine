#nullable enable
using System;
using System.IO;
using System.Linq;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

[Collection(ProcessWideStateCollection.Name)]
public class DiagnosticsTests
{
    public DiagnosticsTests() { _ = TestEnv.UserRoot; }

    private static bool EnsurePositive(int value) => sage_engine.Assert.Ensure(value > 0, "value must be positive");

    [Fact]
    public void Ensure_ReturnsCondition_AndLogsOncePerCallSite()
    {
        using var sink = new CaptureSink();
        int before = sink.Entries.Count(e => e.Message.StartsWith("Ensure failed: value must be positive"));

        Assert.True(EnsurePositive(1));
        Assert.False(EnsurePositive(-1));
        Assert.False(EnsurePositive(-2));   // same call site: not logged again

        int after = sink.Entries.Count(e => e.Message.StartsWith("Ensure failed: value must be positive"));
        Assert.True(after - before <= 1);
        Assert.True(after >= 1);   // logged the first time this call site ever failed
    }

    [Fact]
    public void Check_ThrowsFatal_AndWritesCrashReport()
    {
        var ex = Assert.Throws<SageFatalException>(() => sage_engine.Assert.Check(false, "invariant broken"));
        Assert.True(ex.Reported);
        Assert.NotNull(CrashReporter.LastReportPath);
        string report = File.ReadAllText(CrashReporter.LastReportPath!);
        Assert.Contains("Check failed: invariant broken", report);
    }

    [Fact]
    public void CrashReport_HasAllSections_EvenWhenOneFails()
    {
        CrashReporter.AddSection("Test section", () => "section body");
        CrashReporter.AddSection("Broken section", () => throw new InvalidOperationException("nope"));
        Log.Warn(LogCat.Core, TestEnv.Unique("line in the tail"));

        string? path = CrashReporter.Write(new InvalidOperationException("outer", new ArgumentException("inner")), "test crash");

        Assert.NotNull(path);
        Assert.StartsWith(Path.Combine(TestEnv.UserRoot, "logs"), path);
        string text = File.ReadAllText(path!);
        foreach (var header in new[] { "Summary", "Exception", "Build", "System", "Test section", "Broken section", "Log tail" })
            Assert.Contains($"==== {header} ====", text);
        Assert.Contains("Reason: test crash", text);
        Assert.Contains("inner", text);
        Assert.Contains("section body", text);
        Assert.Contains("(section failed: InvalidOperationException: nope)", text);
        Assert.Contains("line in the tail", text);
    }
}
