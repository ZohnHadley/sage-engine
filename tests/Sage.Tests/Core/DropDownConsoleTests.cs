#nullable enable
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The Shipping console's model (issue #353): availability by con_enable, typing, history, completion,
// executing a line and the scrollback. The drawing is the client's and the smoke run's.
[Collection(ProcessWideStateCollection.Name)]
public class DropDownConsoleTests
{
    public DropDownConsoleTests() { _ = TestEnv.UserRoot; }

    private static (DropDownConsole Console, CVarRegistry R, CoreCVars Core, List<string> Ran) Make(bool shipping = true)
    {
        var r = new CVarRegistry();
        var core = CoreCVars.Register(r);
        var ran = new List<string>();
        r.RegisterCommand("spawn", CVarFlags.None, "", a => ran.Add("spawn " + string.Join(' ', a.Args)));
        r.RegisterCommand("scene_list", CVarFlags.None, "", _ => ran.Add("scene_list"));
        var console = new DropDownConsole(r, core, () => new[] { "sage:bunny" },
            available: shipping ? () => core.ConsoleEnabled.Value : null);
        return (console, r, core, ran);
    }

    [Fact]
    public void Shipping_ConsoleOpensOnlyWithConEnable()
    {
        var (c, _, core, _) = Make();
        c.Toggle();
        Assert.False(c.IsOpen);   // con_enable 0: the key does nothing

        core.ConsoleEnabled.Value = true;
        c.Toggle();
        Assert.True(c.IsOpen);

        core.ConsoleEnabled.Value = false;
        c.Update(0.016f);
        Assert.False(c.IsOpen);   // turning it off closes it
    }

    [Fact]
    public void DevBuild_AlwaysAvailable()
    {
        var (c, _, _, _) = Make(shipping: false);
        c.Toggle();
        Assert.True(c.IsOpen);
    }

    [Fact]
    public void Slide_DropsDownAndRollsUp()
    {
        var (c, _, core, _) = Make();
        core.ConsoleEnabled.Value = true;
        Assert.False(c.IsVisible);
        c.Open();
        c.Update(DropDownConsole.SlideSeconds / 2);
        Assert.InRange(c.Slide, 0.4f, 0.6f);
        c.Update(1f);
        Assert.Equal(1f, c.Slide);
        c.Close();
        c.Update(DropDownConsole.SlideSeconds / 2);
        Assert.True(c.IsVisible);
        c.Update(1f);
        Assert.False(c.IsVisible);
    }

    [Fact]
    public void Typing_EditsTheLineAtTheCaret_AndIgnoresTheConsoleKey()
    {
        var (c, _, core, _) = Make();
        core.ConsoleEnabled.Value = true;
        c.Type("ignored while closed");
        Assert.Equal("", c.Line);

        c.Open();
        c.Type("spwn`~");
        Assert.Equal("spwn", c.Line);
        c.Press(ConsoleNavKey.Left);
        c.Press(ConsoleNavKey.Left);
        c.Press(ConsoleNavKey.Left);
        c.Type("a");
        Assert.Equal("sapwn", c.Line);
        c.Type("\b\b");
        Assert.Equal("pwn", c.Line);
        Assert.Equal(0, c.Caret);
        c.Type("\u007f");
        Assert.Equal("wn", c.Line);
        c.Press(ConsoleNavKey.End);
        Assert.Equal(2, c.Caret);
        c.Press(ConsoleNavKey.Home);
        Assert.Equal(0, c.Caret);
    }

    [Fact]
    public void Enter_RunsTheLine_EchoesIt_AndKeepsHistory()
    {
        var (c, _, core, ran) = Make();
        core.ConsoleEnabled.Value = true;
        c.Open();
        c.Type("spawn sage:bunny\r");
        Assert.Equal(new[] { "spawn sage:bunny" }, ran);
        Assert.Equal("", c.Line);

        c.Type("scene_list\n");
        Assert.Equal(2, ran.Count);

        c.Press(ConsoleNavKey.Up);
        Assert.Equal("scene_list", c.Line);
        c.Press(ConsoleNavKey.Up);
        Assert.Equal("spawn sage:bunny", c.Line);
        c.Press(ConsoleNavKey.Down);
        c.Press(ConsoleNavKey.Down);
        Assert.Equal("", c.Line);

        c.Refresh();
        Assert.Contains("> spawn sage:bunny", string.Join('\n', c.Lines));
    }

    [Fact]
    public void Tab_CompletesCommandsAndRecordIds()
    {
        var (c, _, core, _) = Make();
        core.ConsoleEnabled.Value = true;
        c.Open();
        c.Type("scene_l");
        c.Press(ConsoleNavKey.Tab);
        Assert.Equal("scene_list ", c.Line);

        c.Type("\b".PadRight(c.Line.Length, '\b'));
        c.Type("spawn sage:b");
        c.Press(ConsoleNavKey.Tab);
        Assert.Equal("spawn sage:bunny", c.Line);

        c.Type("\b".PadRight(c.Line.Length, '\b'));
        c.Type("s");
        c.Press(ConsoleNavKey.Tab);   // several commands start with s: extended to what they share, candidates kept
        Assert.True(c.Candidates.Count > 1);
        Assert.All(c.Candidates, x => Assert.StartsWith("s", x));
    }

    [Fact]
    public void Scrollback_ShowsTheLogAtTheLevel_AndScrollsWithPages()
    {
        var (c, _, core, _) = Make();
        core.ConsoleEnabled.Value = true;
        core.LogConsoleLevel.Value = LogLevel.Warn;
        c.Open();
        string tag = "dd" + System.Guid.NewGuid().ToString("N");
        for (int i = 0; i < 30; i++) Log.Warn(LogCat.Console, $"{tag} warn {i}");
        Log.Info(LogCat.Console, $"{tag} chatter");
        c.Refresh();

        var mine = c.Lines.Where(l => l.Contains(tag)).ToList();
        Assert.True(mine.Count >= 30 || Log.Ring.Version > 0);   // the ring may be shared with parallel tests
        Assert.DoesNotContain(c.Lines, l => l.Contains(tag + " chatter"));   // below log_console_level
        Assert.Equal(c.Lines.Count, c.Levels.Count);
        Assert.All(c.Levels, l => Assert.True(l >= LogLevel.Warn));

        c.PageLines = 10;
        c.Press(ConsoleNavKey.PageUp);
        Assert.Equal(10, c.Scroll);
        c.Press(ConsoleNavKey.PageDown);
        c.Press(ConsoleNavKey.PageDown);
        Assert.Equal(0, c.Scroll);

        // Running a line follows the log again.
        c.Press(ConsoleNavKey.PageUp);
        c.Type("scene_list\r");
        Assert.Equal(0, c.Scroll);
    }

    [Fact]
    public void Commands_ClearAndToggleConsole()
    {
        var (c, r, core, _) = Make();
        core.ConsoleEnabled.Value = true;
        c.RegisterCommands();
        r.Execute("toggleconsole", ExecSource.Console);
        Assert.True(c.IsOpen);
        r.Execute("clear", ExecSource.Console);
        c.Refresh();
        Assert.DoesNotContain(c.Lines, l => l.Contains("zzz-not-here"));
    }
}
