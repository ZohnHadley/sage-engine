#nullable enable
using System;
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

[Collection(ProcessWideStateCollection.Name)]
public class ConsoleInputTests
{
    private enum Quality { Low, Medium, High }

    public ConsoleInputTests() { _ = TestEnv.UserRoot; }

    private static (ConsoleInput Input, CVarRegistry R) Make()
    {
        var r = new CVarRegistry();
        r.Register("sv_cheats", false, CVarFlags.None, "");
        r.Register("r_quality", Quality.Low, CVarFlags.None, "");
        r.Register("r_fog", true, CVarFlags.None, "");
        r.RegisterCommand("spawn", CVarFlags.None, "", _ => { });
        r.RegisterCommand("scene_load", CVarFlags.None, "", _ => { });
        r.RegisterCommand("scene_list", CVarFlags.None, "", _ => { });
        return (new ConsoleInput(r, () => new[] { "sage:bunny", "sage:barrel", "sandbox:yard" }), r);
    }

    [Fact]
    public void History_WalksBackAndForthAndKeepsTheDraft()
    {
        var (input, _) = Make();
        input.Submit("spawn a");
        input.Submit("");
        input.Submit("spawn b");
        input.Submit("spawn b");   // a repeat is not a new entry
        Assert.Equal(new[] { "spawn a", "spawn b" }, input.History);

        Assert.Equal("spawn b", input.Previous("half typed"));
        Assert.Equal("spawn a", input.Previous("spawn b"));
        Assert.Equal("spawn a", input.Previous("spawn a"));   // stays on the oldest
        Assert.Equal("spawn b", input.Next("spawn a"));
        Assert.Equal("half typed", input.Next("spawn b"));    // back to the draft
        Assert.Equal("half typed", input.Next("half typed"));
    }

    [Fact]
    public void History_IsBounded()
    {
        var (input, _) = Make();
        for (int i = 0; i < ConsoleInput.MaxHistory + 5; i++) input.Submit("cmd " + i);
        Assert.Equal(ConsoleInput.MaxHistory, input.History.Count);
        Assert.Equal("cmd 5", input.History[0]);
    }

    [Fact]
    public void Tab_CompletesCommandsAndCVars()
    {
        var (input, _) = Make();
        var (line, candidates) = input.Complete("sv_ch");
        Assert.Equal("sv_cheats ", line);          // a single match: finished, ready for the argument
        Assert.Single(candidates);

        (line, candidates) = input.Complete("sc");
        Assert.Equal("scene_l", line);             // several: extended to their common prefix
        Assert.Equal(new[] { "scene_list", "scene_load" }, candidates);

        (line, candidates) = input.Complete("zzz");
        Assert.Equal("zzz", line);
        Assert.Empty(candidates);
    }

    [Fact]
    public void Tab_CompletesCVarValuesAndRecordIds()
    {
        var (input, _) = Make();
        Assert.Equal("r_quality High", input.Complete("r_quality hi").Line);
        Assert.Contains("on", input.Complete("r_fog o").Candidates);

        var (line, candidates) = input.Complete("spawn sage:b");
        Assert.Equal("spawn sage:b", line);
        Assert.Equal(new[] { "sage:barrel", "sage:bunny" }, candidates);
        Assert.Equal("spawn sandbox:yard", input.Complete("spawn sand").Line);
    }

    [Fact]
    public void FrameLimiter_WaitsOutTheRestOfTheFrame()
    {
        Assert.Equal(0, FrameLimiter.Remaining(0, 10.0, 10.001));            // uncapped
        Assert.Equal(0.01, FrameLimiter.Remaining(50, 10.0, 10.01), 6);       // 20 ms frame, 10 ms used
        Assert.Equal(0, FrameLimiter.Remaining(50, 10.0, 10.05));             // already slower than the cap
    }

    [Fact]
    public void Autoexec_RunsAfterConfigAndWinsOverIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sage-autoexec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string config = Path.Combine(dir, "config.cfg"), auto = Path.Combine(dir, "autoexec.cfg");
            File.WriteAllText(config, "t_auto 1\n");
            File.WriteAllText(auto, "t_auto 2 // a comment\nt_other \"hello\"\n");
            using var app = SageApp.Create(new SageAppOptions { ConfigFile = config, AutoexecFile = auto });
            app.Register();
            var a = app.CVars.Register("t_auto", 0, CVarFlags.None, "");
            var b = app.CVars.Register("t_other", "", CVarFlags.None, "");
            app.Configure();
            Assert.Equal(2, a.Value);
            Assert.Equal("hello", b.Value);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Autoexec_MissingIsNotAnError()
    {
        using var app = SageApp.Create(new SageAppOptions { AutoexecFile = Path.Combine(Path.GetTempPath(), "no-such-autoexec.cfg") });
        app.Register();
        app.Configure();
    }
}
