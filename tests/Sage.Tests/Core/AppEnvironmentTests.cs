#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Per-app log, user folder and crash report sections (issue #49): two apps in one process — an editor
// and the game it plays — each have their own, and the statics (Log, LogCat's levels, UserPaths,
// CrashReporter) mean the current app's. None of these touch the process's, so they run in parallel.
public class AppEnvironmentTests
{
    public AppEnvironmentTests() { _ = TestEnv.UserRoot; }

    private static readonly LogCat Cat = new("TestTwoApps");

    [Fact]
    public void TwoAppsInOneProcess_LogToSeparateSinks_WithSeparateLevels()
    {
        using var a = HeadlessApp.Bare().Boot("a");
        using var b = HeadlessApp.Bare().Boot("b");
        Assert.NotSame(a.App.Environment.Logger, b.App.Environment.Logger);
        using var seenByA = new CaptureSink(a.App.Environment.Logger);
        using var seenByB = new CaptureSink(b.App.Environment.Logger);

        // log_level in one app leaves the other's alone.
        Assert.True(a.CVars.Execute($"log_level {Cat.Name} warn"));
        Assert.True(b.CVars.Execute($"log_level {Cat.Name} debug"));
        Assert.Equal(LogLevel.Warn, a.App.Environment.Logger.GetLevel(Cat));
        Assert.Equal(LogLevel.Debug, b.App.Environment.Logger.GetLevel(Cat));

        foreach (var app in new[] { a, b })
            using (app.App.Environment.Enter())
            {
                Assert.Equal(app.App.Environment.Logger.GetLevel(Cat), Cat.MinLevel);   // the static reads the current app's
                Log.Info(Cat, $"info from {app.World.Name}");
                Log.Warn(Cat, $"warn from {app.World.Name}");
                Log.Warn(Cat, "the same line in both");   // collapsed per app, never across apps
            }

        var inA = seenByA.Entries.Where(e => e.Category == Cat).Select(e => e.Message).ToList();
        var inB = seenByB.Entries.Where(e => e.Category == Cat).Select(e => e.Message).ToList();
        Assert.Equal(new[] { "warn from a", "the same line in both" }, inA);   // a's Info is below its level
        Assert.Equal(new[] { "info from b", "warn from b", "the same line in both" }, inB);
    }

    [Fact]
    public void EachAppsWorldTicksIntoItsOwnLog_WithItsOwnTickCounter()
    {
        using var a = HeadlessApp.Bare().Boot("a");
        using var b = HeadlessApp.Bare().Boot("b");
        using var seenByA = new CaptureSink(a.App.Environment.Logger);
        using var seenByB = new CaptureSink(b.App.Environment.Logger);

        for (int i = 0; i < 5; i++) a.World.RunFixed(1f / 60f);
        b.World.RunFixed(1f / 60f);
        // b was created last, so it is current here: a line logged now is b's, stamped with b's tick.
        Log.Warn(Cat, "after the ticks");

        var line = Assert.Single(seenByB.Entries, e => e.Message == "after the ticks");
        Assert.Equal(1, line.Tick);
        Assert.DoesNotContain(seenByA.Entries, e => e.Message == "after the ticks");
    }

    [Fact]
    public void OnceIsOncePerApp()
    {
        using var a = HeadlessApp.Bare().Boot("a");
        using var b = HeadlessApp.Bare().Boot("b");
        using var seenByA = new CaptureSink(a.App.Environment.Logger);
        using var seenByB = new CaptureSink(b.App.Environment.Logger);

        foreach (var app in new[] { a, b, a, b })
            using (app.App.Environment.Enter())
                Log.Once(Cat, LogLevel.Warn, "key", "once per app");

        Assert.Single(seenByA.Entries, e => e.Message == "once per app");
        Assert.Single(seenByB.Entries, e => e.Message == "once per app");
    }

    [Fact]
    public void AnAppsLinesAlsoReachTheLogItWasMadeUnder()
    {
        using var outer = new CaptureSink();   // the test's own log, current from here on
        using var app = HeadlessApp.Bare().Boot("inner");
        using var inner = new CaptureSink();   // made while the app is current: the app's

        Log.Warn(Cat, "from the app");
        using (AppEnvironment.Current.Parent!.Enter())
            Log.Warn(Cat, "from outside it");

        Assert.Contains(outer.Entries, e => e.Message == "from the app");
        Assert.Contains(outer.Entries, e => e.Message == "from outside it");
        Assert.Contains(inner.Entries, e => e.Message == "from the app");
        Assert.DoesNotContain(inner.Entries, e => e.Message == "from outside it");
    }

    [Fact]
    public async Task TheCurrentAppFlowsIntoTasksItStarts()
    {
        using var app = HeadlessApp.Bare().Boot("tasks");
        using var seen = new CaptureSink(app.App.Environment.Logger);
        using (AppEnvironment.Process.Enter())   // not the app, here
            await Task.Run(() => Log.Warn(Cat, "from elsewhere"));
        using (app.App.Environment.Enter())
            await Task.Run(() => Log.Warn(Cat, "from a task"));
        Assert.DoesNotContain(seen.Entries, e => e.Message == "from elsewhere");
        Assert.Contains(seen.Entries, e => e.Message == "from a task");
    }

    [Fact]
    public void UserFolderAndCrashSections_ArePerApp()
    {
        using var a = HeadlessApp.Bare().Boot("a");
        using var b = HeadlessApp.Bare().Boot("b");
        string folderA = TestEnv.NewTempDir();
        a.App.Environment.SetUserFolder("game-a", folderA);

        using (a.App.Environment.Enter())
        {
            Assert.Equal(folderA, UserPaths.Root);
            Assert.Equal("game-a", UserPaths.GameId);
            CrashReporter.AddSection("Only a's", () => "a's section");
        }
        using (b.App.Environment.Enter())
        {
            Assert.Equal(TestEnv.UserRoot, UserPaths.Root);   // b keeps the process's folder
            Assert.Equal(AppEnvironment.Process.GameId, UserPaths.GameId);
            Log.Warn(Cat, "b's last words");
            string? path = CrashReporter.Write(null, "b crashed");
            Assert.NotNull(path);
            Assert.Equal(path, CrashReporter.LastReportPath);
            string text = File.ReadAllText(path!);
            Assert.DoesNotContain("Only a's", text);
            Assert.Contains("b's last words", text);
        }
        using (a.App.Environment.Enter())
        {
            Assert.Null(CrashReporter.LastReportPath);
            string? path = CrashReporter.Write(null, "a crashed");
            Assert.StartsWith(Path.Combine(folderA, "logs"), path);
            string text = File.ReadAllText(path!);
            Assert.Contains("==== Only a's ====", text);
            Assert.Contains("Game id: game-a", text);
            Assert.DoesNotContain("b's last words", text);
        }
    }
}
