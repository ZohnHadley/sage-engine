#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Every RPG kit screen's drawing, headless (issue #355): each widget screen in
// tests/games/kit-screens/screens.txt is opened over the kit-screens test game at 1280x720 (the UI's design
// size), settled, and its render plan (UiRenderPlan: every rect, border, text, image and clip, in pixels and
// drawing order, with its colours) written out a line a command and compared with goldens/plan/<screen>.txt.
// So a change to a kit layout, style, view-model or the plan itself that changes what is drawn fails here,
// naming the first line that moved. The real client's drawing of the same screens is checked by
// tools/kit_screens_check.sh (r_framecheck, CI's Linux job).
//
// After a deliberate change, write the goldens again and look at the diff before committing it:
//   SAGE_UPDATE_GOLDENS=1 dotnet test tests/Sage.Tests --filter KitScreenGoldenTests
public class KitScreenGoldenTests
{
    public KitScreenGoldenTests() { _ = TestEnv.UserRoot; }

    internal static string Game => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "tests", "games", "kit-screens");
    private static string PlanGoldens => Path.Combine(Game, "goldens", "plan");
    private static bool Updating => Environment.GetEnvironmentVariable("SAGE_UPDATE_GOLDENS") == "1";

    internal static readonly Vector2 Viewport = new(1280f, 720f);

    // One line of screens.txt: the screen, how it opens (ui, tap, use) and its argument.
    internal readonly record struct ScreenLine(string Screen, string How, string Argument)
    {
        public string File => Screen.Replace(':', '_');
    }

    internal static List<ScreenLine> Screens()
    {
        var lines = new List<ScreenLine>();
        foreach (var raw in File.ReadAllLines(Path.Combine(Game, "screens.txt")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            lines.Add(new ScreenLine(parts[0], parts[1], parts.Length > 2 ? parts[2] : ""));
        }
        return lines;
    }

    public static IEnumerable<object[]> WidgetScreens() =>
        Screens().Where(s => s.How is "ui" or "tap").Select(s => new object[] { s.Screen, s.How == "ui" ? s.Argument : "" });

    private static HeadlessApp Boot()
    {
        // The client's input_map record type, so the controls screen shows the kit's keys as the client does.
        var app = HeadlessApp.ForGame(Game).WithEngineContent().OnRegistered(a => a.Records.Register<InputMapRecord>()).Boot();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");   // no saves: the slot screens say so
        return app;
    }

    [Theory]
    [MemberData(nameof(WidgetScreens))]
    public void AKitScreenDrawsWhatItsGoldenSays(string screen, string other)
    {
        using var app = Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        string plan = Plan(app, screen, other);
        string file = Path.Combine(PlanGoldens, screen.Replace(':', '_') + ".txt");

        if (Updating)
        {
            Directory.CreateDirectory(PlanGoldens);
            File.WriteAllText(file, plan);
            return;
        }
        Assert.True(File.Exists(file), $"{screen} has no golden at {file}: write it with SAGE_UPDATE_GOLDENS=1 (see the class comment)");
        string want = File.ReadAllText(file).Replace("\r\n", "\n");
        if (want == plan) return;

        var wantLines = want.Split('\n');
        var gotLines = plan.Split('\n');
        int at = 0;
        while (at < wantLines.Length && at < gotLines.Length && wantLines[at] == gotLines[at]) at++;
        string actual = Path.Combine(TestEnv.NewTempDir(), Path.GetFileName(file));
        File.WriteAllText(actual, plan);
        Assert.Fail($"{screen} draws differently from {file} from line {at + 1}:\n" +
                    $"  want: {(at < wantLines.Length ? wantLines[at] : "(the end)")}\n" +
                    $"  got:  {(at < gotLines.Length ? gotLines[at] : "(the end)")}\n" +
                    $"The whole plan is in {actual}; if the change is meant, write the goldens again (SAGE_UPDATE_GOLDENS=1).");
    }

    // Every screen record the kit ships is in the list, so a new one cannot go unchecked: it is a line in
    // screens.txt and its goldens.
    [Fact]
    public void EveryKitScreenIsInTheList()
    {
        using var app = Boot();
        var listed = Screens().Select(s => s.Screen).ToHashSet(StringComparer.Ordinal);
        var kit = app.Records.Ids("screen").Where(id => id.Namespace == Sage.Kits.Rpg.RpgKitModule.ContentNamespace)
            .Select(id => id.ToString()).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(kit);
        var missing = kit.Where(id => !listed.Contains(id)).ToList();
        Assert.True(missing.Count == 0, $"kit screens not in tests/games/kit-screens/screens.txt: {string.Join(", ", missing)}");
        foreach (var line in Screens())
            Assert.Contains(line.How, new[] { "ui", "tap", "use" });
    }

    // The plan of `screen` opened as `ui_open <screen> [other]` would open it, once its fade and focus have
    // settled: what the client's WidgetRenderer replays.
    internal static string Plan(HeadlessApp app, string screen, string other)
    {
        var world = app.World;
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(Viewport);
        var player = world.FindByName("player");
        Assert.False(player.IsNull, "the kit-screens game has a player");
        Entity about = default;
        if (other.Length > 0)
        {
            about = world.FindByName(other);
            Assert.False(about.IsNull, $"nothing is called '{other}'");
        }
        var id = app.Records.Resolve("screen", screen);
        Assert.False(id.IsEmpty, $"no screen '{screen}'");
        var layer = stack.Open(id, new UiBindContext(world, player, about));
        for (int i = 0; i < 3; i++) stack.Update(UiInput.Wait(0.5f));
        Assert.Same(layer, stack.Top);

        var styles = world.Resources.Get<UiStyles>();
        var text = world.Resources.Get<Localisation>();
        layer.Plan.Update(layer.Root, styles, layer.Pressed, layer.PreviousFocus, text.Version);
        return Describe(screen, layer.Plan, layer);
    }

    // A command a line: what, where, in which colours, and what it says or shows.
    internal static string Describe(string screen, UiRenderPlan plan, UiLayer layer)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# {screen}: its render plan at {N(Viewport.X)}x{N(Viewport.Y)}, a command a line (KitScreenGoldenTests, issue #355)\n");
        sb.Append(CultureInfo.InvariantCulture, $"# backdrop {Colour(layer.Backdrop)}, focus on {layer.Root.Focused?.Name ?? "nothing"}\n");
        foreach (ref readonly var c in plan.Commands)
        {
            sb.Append(c.Kind.ToString().ToLowerInvariant()).Append(' ');
            var r = c.Rect;
            sb.Append(N(r.X)).Append(',').Append(N(r.Y)).Append(' ').Append(N(r.Width)).Append('x').Append(N(r.Height));
            switch (c.Kind)
            {
                case UiDrawKind.Rect:
                    sb.Append(' ').Append(Colours(c));
                    break;
                case UiDrawKind.Border:
                    sb.Append(' ').Append(Colours(c)).Append(" width ").Append(N(c.Size));
                    break;
                case UiDrawKind.Text:
                    sb.Append(' ').Append(Colours(c)).Append(" scale ").Append(N(c.Size));
                    if (!c.Font.IsEmpty) sb.Append(" font ").Append(c.Font.ToString());
                    if (c.FontSize != 0f) sb.Append(" size ").Append(N(c.FontSize));
                    if (c.Ellipsis) sb.Append(" ellipsis at ").Append(N(c.EllipsisAt));
                    string line = c.Text == null ? "" : c.Text.Substring(c.Start, Math.Min(c.Length, c.Text.Length - c.Start));
                    sb.Append(" \"").Append(line.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")).Append('"');
                    break;
                case UiDrawKind.Image:
                    sb.Append(' ').Append(Colours(c)).Append(' ').Append(c.Texture.IsEmpty ? "(none)" : c.Texture.ToString());
                    if (c.Slice != default) sb.Append(" slice ").Append(N(c.Slice.Left)).Append(',').Append(N(c.Slice.Top)).Append(',')
                                               .Append(N(c.Slice.Right)).Append(',').Append(N(c.Slice.Bottom)).Append(" scale ").Append(N(c.Size));
                    if (c.Turned) sb.Append(" turned");
                    break;
            }
            if (c.Widget?.Name is { Length: > 0 } name) sb.Append(" @").Append(name);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Colours(in UiDrawCommand c) =>
        c.Blend && c.From != c.Colour ? $"{Colour(c.From)}>{Colour(c.Colour)}" : Colour(c.Colour);

    // #rrggbbaa, from a colour packed like Color.PackedValue (ABGR).
    private static string Colour(uint packed) =>
        string.Create(CultureInfo.InvariantCulture, $"#{packed & 0xFF:x2}{(packed >> 8) & 0xFF:x2}{(packed >> 16) & 0xFF:x2}{packed >> 24:x2}");

    // Two decimals at most: a layout's sums are the same everywhere to far better than that.
    private static string N(float v)
    {
        float rounded = MathF.Round(v, 2);
        if (rounded == 0f) rounded = 0f;   // no "-0"
        return rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
