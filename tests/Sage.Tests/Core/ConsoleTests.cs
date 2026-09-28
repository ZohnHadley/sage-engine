#nullable enable
using System;
using System.IO;
using System.Linq;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

[Collection(ProcessWideStateCollection.Name)]
public class ConsoleTests
{
    private enum Quality { Low, Medium, High }

    public ConsoleTests() { _ = TestEnv.UserRoot; }

    private static CVarRegistry NewRegistry(out CVar<bool> cheats)
    {
        var r = new CVarRegistry();
        cheats = r.Register("sv_cheats", false, CVarFlags.None, "cheats");
        return r;
    }

    [Fact]
    public void Values_ParseAndValidate()
    {
        var r = NewRegistry(out _);
        var b = r.Register("t_bool", false, CVarFlags.None, "");
        var i = r.Register("t_int", 5, CVarFlags.None, "", 0, 10);
        var f = r.Register("t_float", 1.5f, CVarFlags.None, "", 0f, 100f);
        var e = r.Register("t_enum", Quality.Low, CVarFlags.None, "");
        var s = r.Register("t_string", "", CVarFlags.None, "");

        Assert.True(r.Execute("t_bool on; t_int 7; t_float 2.25; t_enum HIGH; t_string \"hello world\""));
        Assert.True(b.Value);
        Assert.Equal(7, i.Value);
        Assert.Equal(2.25f, f.Value);
        Assert.Equal(Quality.High, e.Value);
        Assert.Equal("hello world", s.Value);

        Assert.False(r.Execute("t_int 11"));      // out of range
        Assert.False(r.Execute("t_int abc"));
        Assert.False(r.Execute("t_enum Ultra"));
        Assert.False(r.Execute("t_float nan"));
        Assert.Equal(7, i.Value);
        Assert.Equal("1", b.ValueString);
        Assert.Equal("2.25", f.ValueString);
    }

    [Fact]
    public void SettingFromCode_ClampsToRange_AndChangedFiresOnlyOnChange()
    {
        var r = NewRegistry(out _);
        var i = r.Register("t_clamp", 5, CVarFlags.None, "", 0, 10);
        int changes = 0;
        i.Changed += _ => changes++;

        i.Value = 50;
        Assert.Equal(10, i.Value);
        i.Value = 10;
        Assert.Equal(1, changes);
        i.Reset();
        Assert.Equal(5, i.Value);
        Assert.True(i.IsDefault);
    }

    [Fact]
    public void Cheats_RequireSvCheats_ExceptFromCode()
    {
        var r = NewRegistry(out var cheats);
        var god = r.Register("god", false, CVarFlags.Cheat, "");
        bool ran = false;
        r.RegisterCommand("noclip", CVarFlags.Cheat, "", _ => ran = true);

        Assert.False(r.Execute("god 1"));
        Assert.False(r.Execute("noclip"));
        Assert.False(god.Value);
        Assert.False(ran);

        Assert.True(r.Execute("god 1", ExecSource.Code));
        cheats.Value = true;
        Assert.True(r.Execute("noclip"));
        Assert.True(ran);
    }

    [Fact]
    public void ReadOnly_CannotBeSetFromConsole()
    {
        var r = NewRegistry(out _);
        var ro = r.Register("t_ro", 1, CVarFlags.ReadOnly, "", 0, 5);
        Assert.False(r.Execute("t_ro 2"));
        Assert.Equal(1, ro.Value);
    }

    [Fact]
    public void DevOnly_IsRegisteredInDevBuilds_Duplicates_Throw()
    {
        var r = NewRegistry(out _);
        r.Register("t_dev", 0, CVarFlags.DevOnly, "", 0, 1);
        Assert.Equal(BuildInfo.IsDevBuild, r.Find("t_dev") != null);
        Assert.Throws<InvalidOperationException>(() => r.Register("sv_cheats", true, CVarFlags.None, ""));
        Assert.Throws<ArgumentException>(() => r.RegisterCommand("bad name", CVarFlags.None, "", _ => { }));
    }

    [Fact]
    public void Statements_Quotes_AndComments()
    {
        Assert.Equal(new[] { "echo \"a; b\"", "x 1" }, CommandLine.SplitStatements("echo \"a; b\"; x 1 // y 2"));
        Assert.Equal(new[] { "a 1", "b 2" }, CommandLine.SplitStatements("a 1 // note\nb 2"));
        Assert.Equal(new[] { "bind", "a b", "c" }, CommandLine.Tokenize("bind \"a b\" c"));
        Assert.Equal(new[] { "x", "" }, CommandLine.Tokenize("x \"\""));
    }

    [Fact]
    public void Config_SaveAndExec_RoundTrip()
    {
        string path = Path.Combine(TestEnv.NewTempDir(), "config.cfg");
        var r1 = NewRegistry(out _);
        var a1 = r1.Register("t_arch", 3, CVarFlags.Archive, "", 0, 9);
        var s1 = r1.Register("t_name", "x", CVarFlags.Archive, "");
        r1.Register("t_temp", 0, CVarFlags.None, "", 0, 9).Value = 4;
        a1.Value = 8;
        s1.Value = "two words";
        r1.SaveArchived(path);

        string text = File.ReadAllText(path);
        Assert.Contains("t_arch \"8\"", text);
        Assert.DoesNotContain("t_temp", text);

        var r2 = NewRegistry(out _);
        var a2 = r2.Register("t_arch", 3, CVarFlags.Archive, "", 0, 9);
        var s2 = r2.Register("t_name", "x", CVarFlags.Archive, "");
        Assert.True(r2.ExecFile(path));
        Assert.Equal(8, a2.Value);
        Assert.Equal("two words", s2.Value);
    }

    [Fact]
    public void UnknownCommand_WarnsOnConsoleCategory()
    {
        var r = NewRegistry(out _);
        using var sink = new CaptureSink();
        string name = "nope_" + Guid.NewGuid().ToString("N");
        Assert.False(r.Execute(name));
        Assert.Contains(sink.Entries, e => e.Category == LogCat.Console && e.Level == LogLevel.Warn && e.Message.Contains(name));
    }

    [Fact]
    public void LaunchArgs_SplitCommandsAndOptions()
    {
        var args = LaunchArgs.Parse(new[] { "+developer", "1", "+log_level", "assets", "trace", "-game", "games/x", "+sim_offset", "-1", "+echo", "two words" });
        Assert.Equal(new[] { "developer 1", "log_level assets trace", "sim_offset -1", "echo \"two words\"" }, args.Commands);
        Assert.Equal("games/x", args.Options["game"]);
    }

    [Fact]
    public void CoreCVars_DeveloperSetsLogDefaults_LogLevelCommandOverrides()
    {
        var before = LogCat.DefaultLevel;
        try
        {
            var r = new CVarRegistry();
            var core = CoreCVars.Register(r, ownsProcessLog: true);

            core.Developer.Value = 1;
            Assert.Equal(LogLevel.Debug, LogCat.DefaultLevel);
            core.Developer.Value = 0;
            Assert.Equal(LogLevel.Info, LogCat.DefaultLevel);

            var cat = new LogCat("TestCmd" + Guid.NewGuid().ToString("N"));
            Assert.True(r.Execute($"log_level {cat.Name} error"));
            Assert.Equal(LogLevel.Error, cat.MinLevel);
            Assert.True(r.Execute($"log_level {cat.Name} default"));
            Assert.False(cat.IsOverridden);
            Assert.True(core.ConsoleAvailable);   // dev build
        }
        finally { LogCat.DefaultLevel = before; }
    }

    // Only the app that owns the process's log configures it (issue #11): a test, a tool or a second
    // app in the same process creates its cvars and leaves the log as the host set it.
    [Fact]
    public void AnAppThatDoesNotOwnTheLogLeavesItAlone()
    {
        var before = LogCat.DefaultLevel;
        try
        {
            LogCat.DefaultLevel = LogLevel.Warn;   // what "the host" chose
            using var app = SageApp.Create(new SageAppOptions { IncludeSimulationModules = false });
            Assert.False(app.Engine.Core.OwnsProcessLog);
            Assert.Equal(LogLevel.Warn, LogCat.DefaultLevel);

            app.Engine.Core.Developer.Value = 1;
            app.Engine.Core.Developer.Value = 0;
            Assert.Equal(LogLevel.Warn, LogCat.DefaultLevel);

            using var host = SageApp.Create(new SageAppOptions { IncludeSimulationModules = false, OwnsProcessLog = true });
            Assert.True(host.Engine.Core.OwnsProcessLog);
            Assert.NotEqual(LogLevel.Warn, LogCat.DefaultLevel);   // the owner applies its developer defaults
        }
        finally { LogCat.DefaultLevel = before; }
    }

    // ---- `wait` and the deferred queue (02 §4.2) -------------------------------------------------
    //
    // A console script is only a script once it can pace itself. These pin the rules a script relies
    // on: order is order, a wait stops the *rest*, and an exec inside a script runs where it is
    // written rather than after everything else.

    private static (CVarRegistry Registry, System.Collections.Generic.List<string> Ran) Scripted()
    {
        var r = new CVarRegistry();
        var ran = new System.Collections.Generic.List<string>();
        r.RegisterCommand("wait", CVarFlags.None, "wait [seconds]", a =>
            a.Registry.Wait(a.Count > 0 ? float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture) : 0f));
        r.RegisterCommand("mark", CVarFlags.None, "mark <name>", a => ran.Add(a.Count > 0 ? a[0] : ""));
        return (r, ran);
    }

    [Xunit.Fact]
    public void StatementsWithoutAWaitAllRunAtOnce()
    {
        var (r, ran) = Scripted();
        r.Execute("mark a; mark b; mark c");
        Assert.Equal(new[] { "a", "b", "c" }, ran);
        Assert.False(r.HasPendingStatements);
    }

    [Xunit.Fact]
    public void AWaitStopsTheRestUntilItsTimeIsUp()
    {
        var (r, ran) = Scripted();
        r.Execute("mark a; wait 0.5; mark b");

        Assert.Equal(new[] { "a" }, ran);
        Assert.True(r.HasPendingStatements);

        r.Pump(0.2f);
        Assert.Equal(new[] { "a" }, ran);     // not yet

        r.Pump(0.4f);
        Assert.Equal(new[] { "a", "b" }, ran);
        Assert.False(r.HasPendingStatements);
    }

    // No argument means "next frame", which is the common case: let one tick happen between two
    // commands. Any pump at all is enough, however small.
    [Xunit.Fact]
    public void AWaitWithNoArgumentIsOneFrame()
    {
        var (r, ran) = Scripted();
        r.Execute("mark a; wait; mark b");
        Assert.Equal(new[] { "a" }, ran);

        r.Pump(1f / 60f);
        Assert.Equal(new[] { "a", "b" }, ran);
    }

    [Xunit.Fact]
    public void SeveralWaitsRunInOrder()
    {
        var (r, ran) = Scripted();
        r.Execute("mark a; wait 0.1; mark b; wait 0.1; mark c");

        Assert.Equal(new[] { "a" }, ran);
        r.Pump(0.2f);
        Assert.Equal(new[] { "a", "b" }, ran);   // one wait at a time, not both at once
        r.Pump(0.2f);
        Assert.Equal(new[] { "a", "b", "c" }, ran);
    }

    // A script that execs another expects the inner one to run *here*. Without the front-insert, the
    // inner statements would land after the rest of the outer script and silently reorder it.
    [Xunit.Fact]
    public void AScriptRunFromInsideAScriptRunsWhereItIsWritten()
    {
        var (r, ran) = Scripted();
        r.RegisterCommand("inner", CVarFlags.None, "inner", a => a.Registry.Execute("mark x; mark y"));
        r.Execute("mark a; inner; mark b");

        Assert.Equal(new[] { "a", "x", "y", "b" }, ran);
    }

    [Xunit.Fact]
    public void AnInnerScriptsWaitHoldsUpTheOuterOne()
    {
        var (r, ran) = Scripted();
        r.RegisterCommand("inner", CVarFlags.None, "inner", a => a.Registry.Execute("mark x; wait 0.1; mark y"));
        r.Execute("mark a; inner; mark b");

        Assert.Equal(new[] { "a", "x" }, ran);
        r.Pump(0.2f);
        Assert.Equal(new[] { "a", "x", "y", "b" }, ran);
    }

    [Xunit.Fact]
    public void PendingStatementsCanBeDropped()
    {
        var (r, ran) = Scripted();
        r.Execute("mark a; wait 10; mark b");
        r.ClearPending();
        r.Pump(20f);

        Assert.Equal(new[] { "a" }, ran);
        Assert.False(r.HasPendingStatements);
    }
}
