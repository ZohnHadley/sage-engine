#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Real text (issue #338): TrueType fonts read and measured headlessly, rasterised into glyph atlases,
// chosen per ui_style with a size, and labels that wrap, clip or end in "…". The font is the engine's own
// `fonts/sage.ttf` (engine_content/tools/make_font.py): proportional, an em of nine pixels, its line
// height its size, and each glyph as wide as its ink plus a pixel — 'i' 4, 'm' and 'A' 6, a space 3.
public class FontTests
{
    public FontTests() { _ = TestEnv.UserRoot; }

    private static readonly string EngineFont = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content", "fonts", "sage.ttf");

    private static TrueTypeFont Font() => TrueTypeFont.Load(File.ReadAllBytes(EngineFont), "fonts/sage.ttf");

    private static readonly MonospaceTextMeasure Cells = new(6f, 9f);

    private static UiRoot Root()
    {
        var root = new UiRoot(Cells);
        root.SetViewport(UiRoot.DefaultDesignSize);
        return root;
    }

    private static UiDrawCommand[] Texts(UiRenderPlan plan) => plan.Commands.ToArray().Where(c => c.Kind == UiDrawKind.Text).ToArray();

    private static string Line(in UiDrawCommand text) => text.Text!.Substring(text.Start, text.Length) + (text.Ellipsis ? TextLayout.Ellipsis : "");

    [Fact]
    public void ATrueTypeFontMeasuresWithItsOwnProportionalMetrics()
    {
        using var font = Font();
        Assert.Equal(9f, font.LineHeight(9f), 3);
        Assert.Equal(18f, font.LineHeight(18f), 3);
        Assert.Equal(8f, font.Ascent(9f), 3);
        Assert.Equal(4f, font.Advance('i', 9f), 3);
        Assert.Equal(6f, font.Advance('m', 9f), 3);
        Assert.Equal(3f, font.Advance(' ', 9f), 3);
        Assert.True(font.HasGlyph('A'));
        Assert.True(font.HasGlyph('…'));
        Assert.False(font.HasGlyph('ж'));   // not in it: measured as its missing-glyph box, never thrown

        var measure = new FontTextMeasure(font, 18f);
        Assert.Equal(18f, measure.LineHeight, 3);
        Assert.Equal(new Vector2((4f + 6f) * 2f, 18f * 2f), measure.Measure("im\nm", 1f));   // the widest line, two lines
        Assert.Equal(measure.Measure("im", 0.5f).X, measure.Width("im", 0.5f), 3);
        Assert.True(measure.Width("iiii", 1f) < measure.Width("mmmm", 1f));
        Assert.Throws<InvalidDataException>(() => TrueTypeFont.Load(new byte[64], "junk.ttf"));
    }

    [Fact]
    public void TheGlyphAtlasRasterisesEachCharacterOnceAndKeepsItsPlaceWhenItGrows()
    {
        using var font = Font();
        var atlas = new GlyphAtlas(font, 18);
        var a = atlas.Get('A');
        Assert.True(a.Width > 0 && a.Height > 0);
        Assert.True(a.OffsetY < 0);   // above the baseline
        int inked = 0;
        for (int y = a.Y; y < a.Y + a.Height; y++)
            for (int x = a.X; x < a.X + a.Width; x++)
                if (atlas.Pixels[y * GlyphAtlas.Width + x] > 128) inked++;
        Assert.True(inked > a.Width * a.Height / 4, $"only {inked} pixels of 'A' are inked");
        Assert.True(atlas.DirtyTop <= a.Y && atlas.DirtyBottom >= a.Y + a.Height);

        int version = atlas.Version;
        Assert.Equal(a, atlas.Get('A'));
        Assert.Equal(version, atlas.Version);                     // asked again: a lookup
        Assert.Equal(0, atlas.Get(' ').Width);                    // nothing to draw
        atlas.MarkClean();
        Assert.True(atlas.DirtyTop >= atlas.DirtyBottom);

        // Big glyphs fill the first 128 rows; the image doubles and 'A' is where it was, inked as it was.
        var big = new GlyphAtlas(font, 90);
        var first = big.Get('A');
        byte[] before = Copy(big, first);
        for (char c = '!'; c <= '~'; c++) big.Get(c);
        Assert.True(big.Height > 128);
        Assert.Equal(first, big.Get('A'));
        Assert.Equal(before, Copy(big, first));

        static byte[] Copy(GlyphAtlas atlas, AtlasGlyph glyph)
        {
            var pixels = new byte[glyph.Width * glyph.Height];
            for (int y = 0; y < glyph.Height; y++)
                Array.Copy(atlas.Pixels, (glyph.Y + y) * GlyphAtlas.Width + glyph.X, pixels, y * glyph.Width, glyph.Width);
            return pixels;
        }
    }

    [Fact]
    public void ALongLineWrapsAtSpacesToItsWidthAndTheLabelMeasuresEveryLine()
    {
        var root = Root();
        var column = root.Content.Add(new Stack());
        var label = column.Add(new Label("the quick brown fox jumps") { Wrap = true, MaxWidth = 60f });   // ten cells
        root.Layout();

        Assert.Equal(new Vector2(54f, 27f), label.DesiredSize);   // "the quick" / "brown fox" / "jumps"
        var plan = new UiRenderPlan();
        plan.Update(root, new UiStyles());
        var texts = Texts(plan);
        Assert.Equal(new[] { "the quick", "brown fox", "jumps" }, texts.Select(t => Line(t)));
        Assert.Equal(new[] { 0f, 9f, 18f }, texts.Select(t => t.Rect.Y - label.ContentRect.Y));
        Assert.All(texts, t => Assert.Same(label.Text, t.Text));   // slices of the one string, nothing copied

        // A word longer than the width is cut between characters; '\n' still starts a line.
        label.Text = "abcdefghijklmnop\nxy";
        root.Layout();
        plan.Update(root, new UiStyles());
        Assert.Equal(new[] { "abcdefghij", "klmnop", "xy" }, Texts(plan).Select(t => Line(t)));
        Assert.Equal(new Vector2(60f, 27f), label.DesiredSize);

        // Not wrapped, the same text is one line as wide as it is (what a label always did).
        label.Wrap = false;
        label.MaxWidth = 0f;
        root.Layout();
        Assert.Equal(new Vector2(96f, 18f), label.DesiredSize);
    }

    [Fact]
    public void AWrappedLabelFitsTheRoomItsParentOffersAndWhatFollowsGoesBelowIt()
    {
        var root = Root();
        // A column offered ten cells: the screen's width less a margin.
        var column = root.Content.Add(new Stack { Margin = new Thickness(0f, 0f, UiRoot.DefaultDesignSize.X - 60f, 0f) });
        var label = column.Add(new Label("one two three four") { Wrap = true });
        var under = column.Add(new Label("after"));
        root.Layout();

        Assert.Equal(new Vector2(60f, 18f), label.DesiredSize);   // "one two" / "three four": exactly ten
        Assert.True(label.Rect.Width <= 60f);
        Assert.Equal(label.Rect.Bottom, under.Rect.Y, 3);         // the next widget is below every line
    }

    [Fact]
    public void EllipsisEndsWhatDoesNotFitAndClipCutsAtTheLabel()
    {
        var root = Root();
        var column = root.Content.Add(new Stack());
        var cut = column.Add(new Label("abcdefghij") { Overflow = TextOverflow.Ellipsis, MaxWidth = 30f });   // five cells
        var clipped = column.Add(new Label("abcdefghij") { Overflow = TextOverflow.Clip, MaxWidth = 30f });
        root.Layout();
        Assert.Equal(new Vector2(30f, 9f), cut.DesiredSize);
        Assert.Equal(new Vector2(30f, 9f), clipped.DesiredSize);

        var plan = new UiRenderPlan();
        plan.Update(root, new UiStyles());
        var commands = plan.Commands.ToArray();
        var first = commands.Single(c => c.Kind == UiDrawKind.Text && c.Widget == cut);
        Assert.Equal("abcd…", Line(first));            // four cells and the dots: five
        Assert.Equal(24f, first.EllipsisAt, 3);        // the dots go after the four

        // Clip: the whole text, between a push of the label's rect and a pop.
        int at = Array.FindIndex(commands, c => c.Kind == UiDrawKind.Text && c.Widget == clipped);
        Assert.Equal("abcdefghij", Line(commands[at]));
        Assert.Equal(UiDrawKind.PushClip, commands[at - 1].Kind);
        Assert.Equal(clipped.ContentRect, commands[at - 1].Rect);
        Assert.Equal(UiDrawKind.PopClip, commands[at + 1].Kind);

        // Wrapped and too tall for its rect: the lines that fit, the last ending in "…".
        var lines = new System.Collections.Generic.List<TextLine>();
        var size = TextLayout.Break("aaa bbb ccc ddd", Cells, 1f, 24f, 18f, wrap: true, TextOverflow.Ellipsis, lines);
        Assert.Equal(new[] { "aaa", "bbb…" }, lines.Select(l => "aaa bbb ccc ddd".Substring(l.Start, l.Length) + (l.Ellipsis ? "…" : "")));
        Assert.Equal(new Vector2(24f, 18f), size);
    }

    [Fact]
    public void AStyleNamesAFontAndASizeAndItsLabelsAreLaidOutInIt()
    {
        var fixture = new MountFixture();
        fixture.Write("uitest", "data/ui.json", """
            [
              { "type": "ui_style", "id": "body", "font": "fonts/sage.ttf", "fontSize": 18 },
              { "type": "ui_style", "id": "big", "fontSize": 18 },
              { "type": "ui_style", "id": "lost", "font": "fonts/nothing.ttf" },
              { "type": "ui_layout", "id": "page", "style": "body",
                "nodes": {
                  "window": { "widget": "stack", "anchors": "center" },
                  "word":   { "widget": "label", "parent": "window", "text": "mim" },
                  "line":   { "widget": "label", "parent": "window", "text": "mm mm mm", "wrap": true, "maxWidth": 60 },
                  "cells":  { "widget": "label", "parent": "window", "text": "mim", "style": "big" },
                  "lost":   { "widget": "label", "parent": "window", "text": "mim", "style": "lost" }
                } },
              { "type": "screen", "id": "page", "layout": "page" }
            ]
            """);
        fixture.Write("uitest", "strings/en/uitest.json", "{}");
        Directory.CreateDirectory(Path.Combine(fixture.Dir("uitest"), "fonts"));
        File.Copy(EngineFont, Path.Combine(fixture.Dir("uitest"), "fonts", "sage.ttf"));
        fixture.Mount("uitest", "uitest");
        using var capture = new CaptureSink();
        using var app = UiRecordTests.Boot(fixture);

        var styles = app.World.Resources.Get<UiStyles>();
        Assert.Equal(18f, styles.Get("uitest:body").FontSize);
        var stack = app.World.Resources.Get<UiScreenStack>();
        Assert.Same(app.World.Resources.Get<UiFonts>(), stack.Fonts);
        var layer = stack.Open(new RecordId("uitest", "page"));
        layer.Root.Layout();
        var content = layer.Content;

        // In the TTF at an 18-unit em: m 12, i 8, m 12; a line is 18.
        var word = content.Find<Label>("word")!;
        Assert.Equal(new Vector2(32f, 18f), word.DesiredSize);
        Assert.Equal(AssetPath.Intern("fonts/sage.ttf"), word.Font);

        // "mm mm" is 12+12+6+12+12 = 54 of the 60: two lines.
        Assert.Equal(new Vector2(54f, 36f), content.Find<Label>("line")!.DesiredSize);

        // The engine font at 18: its 6×9 cells doubled.
        Assert.Equal(new Vector2(36f, 18f), content.Find<Label>("cells")!.DesiredSize);

        // A font that is not there is said once, and text in it is laid out in the engine font.
        Assert.Equal(new Vector2(18f, 9f), content.Find<Label>("lost")!.DesiredSize);
        Assert.Single(capture.Entries, e => e.Message.Contains("font 'fonts/nothing.ttf'"));

        // The plan carries the font and size to the client.
        var plan = new UiRenderPlan();
        plan.Update(layer.Root, styles);
        var text = plan.Commands.ToArray().First(c => c.Kind == UiDrawKind.Text && c.Widget == word);
        Assert.Equal(word.Font, text.Font);
        Assert.Equal(18f, text.FontSize);
    }

    [Fact]
    public void WrapOnAWidgetWithoutTextIsAnErrorAtItsLine()
    {
        using var capture = new CaptureSink();
        var fixture = new MountFixture();
        fixture.Write("uibad", "data/ui.json", """
            [
              { "type": "ui_layout", "id": "bad",
                "nodes": { "box": { "widget": "box", "wrap": true } } }
            ]
            """);
        fixture.Mount("uibad", "uibad");
        using var app = UiRecordTests.Boot(fixture);
        Assert.Contains(capture.Entries, e => e.Message.StartsWith("uibad:data/ui.json:3:", StringComparison.Ordinal)
                                              && e.Message.Contains("a box has no text to wrap or cut"));
    }

    [Fact]
    public void AFontThatCannotBeReadMeasuresInTheEngineFont()
    {
        using var capture = new CaptureSink();
        var fonts = new UiFonts(path => path.ToString() == "fonts/junk.ttf" ? new byte[100] : null);
        Assert.Same(Cells, fonts.Measure(AssetPath.Intern("fonts/junk.ttf"), 0f, Cells));
        Assert.Same(Cells, fonts.Measure(AssetPath.Intern("fonts/none.otf"), 9f, Cells));
        Assert.Same(Cells, fonts.Measure(AssetPath.Intern("textures/grid.png"), 0f, Cells));   // a grid atlas measures as the engine font
        Assert.Equal(18f, fonts.Measure(AssetPath.None, 18f, Cells).LineHeight);             // at another size, scaled
        Assert.Null(fonts.Get(AssetPath.Intern("fonts/junk.ttf")));
        Assert.Contains(capture.Entries, e => e.Message.Contains("font 'fonts/junk.ttf'"));
        Assert.Contains(capture.Entries, e => e.Message.Contains("font 'fonts/none.otf': there is no such file"));
    }
}

// A frame of a screen in a TrueType font, wrapped, allocates nothing once its glyphs are known (02 §4.6).
[Collection(MeasurementsCollection.Name)]
public class FontAllocationTests
{
    public FontAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void LayingOutAndPlanningWrappedTrueTypeTextAllocatesNothing()
    {
        string path = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content", "fonts", "sage.ttf");
        byte[] data = File.ReadAllBytes(path);
        var fonts = new UiFonts(_ => data);
        var root = new UiRoot(new MonospaceTextMeasure(6f, 9f)) { Fonts = fonts };
        root.SetViewport(UiRoot.DefaultDesignSize);
        var column = root.Content.Add(new Stack());
        var font = AssetPath.Intern("fonts/sage.ttf");
        var line = column.Add(new Label("A long dialogue line that has to wrap inside its box, several times over.")
            { Font = font, FontSize = 18f, Wrap = true, MaxWidth = 200f });
        var cut = column.Add(new Label("And one cut short with an ellipsis") { Font = font, Overflow = TextOverflow.Ellipsis, MaxWidth = 80f });
        var button = column.Add(new Button("OK") { Font = font });
        var other = column.Add(new Button("Cancel") { Font = font });
        var styles = new UiStyles();
        var plan = new UiRenderPlan();
        var atlas = new GlyphAtlas(fonts.Get(font)!, 18);
        foreach (char c in line.Text + cut.Text) atlas.Get(c);
        for (int i = 0; i < 4; i++) { root.Focus(i % 2 == 0 ? button : other); root.Layout(); plan.Update(root, styles); }

        int frame = 0;
        AllocationProbe.AssertNone(300, () =>
        {
            root.Focus(frame % 2 == 0 ? button : other);   // a rebuild of the plan every frame
            line.MaxWidth = frame % 2 == 0 ? 200f : 240f;   // and the wrapped label laid out again
            root.Layout();
            plan.Update(root, styles);
            foreach (char c in line.Text) atlas.Get(c);
            frame++;
        });
        Assert.True(plan.Builds > 300);
        Assert.True(line.DesiredSize.Y > 18f);
    }
}
