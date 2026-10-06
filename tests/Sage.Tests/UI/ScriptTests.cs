#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Non-Latin scripts and how complete a translation is (issue #345): CLDR plural rules, placeholders
// formatted per language, Arabic joined into its presentation forms, lines put in the order they are seen,
// ideographs that wrap without spaces, fonts that fall back character by character, screens mirrored
// for a right-to-left language, and `sage validate`'s report on every translation. The fonts are made
// here (TestFonts): boxes, each script's font with advances of its own, so which font measured a
// character shows in a width.
public class ScriptTests
{
    public ScriptTests() { _ = TestEnv.UserRoot; }

    private static readonly MonospaceTextMeasure Cells = new(6f, 9f);

    // Latin capitals and digits 600 units wide; Arabic (letters and their forms) 400; kana and a few
    // ideographs 1000.
    private static readonly int[] Latin = Enumerable.Range(' ', 95).ToArray();
    private static readonly int[] Arabic = Enumerable.Range(0x0621, 0x2A).Concat(Enumerable.Range(0xFE70, 0x8D)).Concat(Enumerable.Range(0x0660, 10)).ToArray();
    private static readonly int[] Japanese = Enumerable.Range(0x3000, 0x100).Concat("日本語設定持物".Select(c => (int)c)).ToArray();

    private static byte[] LatinFont() => TestFonts.Build("Latin", Latin, 600);
    private static byte[] ArabicFont() => TestFonts.Build("Arabic", Arabic.Append(' '), 400);
    private static byte[] JapaneseFont() => TestFonts.Build("Kana", Japanese, 1000);

    // ---- plural rules ---------------------------------------------------------------------------------

    [Fact]
    public void PluralFormsFollowTheCldrRuleOfEachLanguage()
    {
        PluralCategory Of(string language, double n) => PluralRules.Select(language, n);
        // Russian: one, few, many, and other for fractions.
        Assert.Equal(new[] { PluralCategory.One, PluralCategory.Few, PluralCategory.Many, PluralCategory.Many, PluralCategory.One, PluralCategory.Few, PluralCategory.Other },
                     new[] { 1, 2, 5, 11, 21, 22, 1.5 }.Select(n => Of("ru", n)));
        // Polish: 22 is few, 12 and 25 many.
        Assert.Equal(PluralCategory.Few, Of("pl", 22));
        Assert.Equal(PluralCategory.Many, Of("pl", 12));
        Assert.Equal(PluralCategory.Many, Of("pl", 25));
        // Arabic: all six.
        Assert.Equal(new[] { PluralCategory.Zero, PluralCategory.One, PluralCategory.Two, PluralCategory.Few, PluralCategory.Many, PluralCategory.Other },
                     new double[] { 0, 1, 2, 3, 11, 100 }.Select(n => Of("ar", n)));
        Assert.Equal(PluralCategory.Few, Of("ar", 103));
        // Czech: a fraction is many. Hebrew: two. Japanese: no plural. French: millions are many.
        Assert.Equal(PluralCategory.Many, Of("cs", 1.5));
        Assert.Equal(PluralCategory.Two, Of("he", 2));
        Assert.Equal(PluralCategory.Other, Of("ja", 1));
        Assert.Equal(PluralCategory.Many, Of("fr", 1000000));
        Assert.Equal(PluralCategory.One, Of("fr", 1.5));
        // Region subtags fall back to the language, except Portugal's own rule.
        Assert.Equal(PluralCategory.Few, Of("ru-RU", 3));
        Assert.Equal(PluralCategory.One, Of("pt-BR", 0));
        Assert.Equal(PluralCategory.Other, Of("pt_PT", 0));
        Assert.Equal(6, PluralRules.Uses("ar").Count);
        Assert.Equal(new[] { PluralCategory.Other }, PluralRules.Uses("ja"));
    }

    [Fact]
    public void APlaceholderWithAFormatIsWrittenTheLanguagesWay()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "strings/en/ui.json", """{ "gold": "{gold:n0} gold", "plain": "{gold} gold", "day": "{when:yyyy-MM-dd}", "bad": "{gold:q9}" }""");
        fixture.Write("game", "strings/de/ui.json", """{ "gold": "{gold:n0} Gold" }""");
        fixture.Mount("game", "game");
        var text = new Localisation();
        text.Load(fixture.Vfs, "en");
        Assert.Equal("1,234 gold", text.Format("@ui.gold", ("gold", 1234)));
        Assert.Equal("1234.5 gold", text.Format("@ui.plain", ("gold", 1234.5)));   // no format: as written, whatever the language
        Assert.Equal("2026-10-06", text.Format("@ui.day", ("when", new DateTime(2026, 10, 6))));
        Assert.Equal("1234", text.Format("@ui.bad", ("gold", 1234)));          // a format the value does not take: as it is

        text.Load(fixture.Vfs, "de");
        Assert.Equal("de", text.Culture.Name);
        Assert.Equal("1.234 Gold", text.Format("@ui.gold", ("gold", 1234)));
        Assert.Equal(TextDirection.LeftToRight, text.Direction);
    }

    // ---- Arabic, Hebrew and bidirectional text ------------------------------------------------------

    [Fact]
    public void ArabicLettersTakeTheFormTheirNeighboursGiveThem()
    {
        // ب ي ت: initial, medial, final.
        Assert.Equal("\uFE91\uFEF4\uFE96", Scripts.Shape("بيت"));
        // س ل ا م: seen initial, lam-alef one ligature (final: it follows seen), meem alone after alef.
        Assert.Equal("\uFEB3\uFEFC\uFEE1", Scripts.Shape("سلام"));
        // Alef joins only backwards: د ا each stand alone.
        Assert.Equal("\uFEA9\uFE8D", Scripts.Shape("دا"));
        // A word on its own between spaces, Latin untouched, and text with no Arabic is itself.
        Assert.Equal("a \uFE8F b", Scripts.Shape("a ب b"));
        const string latin = "plain";
        Assert.Same(latin, Scripts.Shape(latin));
        Assert.Equal('ب', Scripts.BaseOf(0xFE91));
        Assert.Equal('\0', Scripts.BaseOf('A'));
    }

    [Fact]
    public void ALineIsReorderedIntoTheOrderItIsSeen()
    {
        Assert.Equal("םולש", Scripts.Reorder("שלום", rightToLeft: true));
        // Numbers inside right-to-left text still read left to right.
        Assert.Equal("דג 12 בא", Scripts.Reorder("אב 12 גד", rightToLeft: true));
        Assert.Equal("ab 3.14", Scripts.Reorder("ab 3.14", rightToLeft: false));
        // A Hebrew word in an English line is turned round where it is; the line is not.
        Assert.Equal("I said םולש!", Scripts.Reorder("I said שלום!", rightToLeft: false));
        // Brackets in right-to-left text are mirrored, so they still open towards their contents.
        Assert.Equal("(םולש)", Scripts.Reorder("(שלום)", rightToLeft: true));
        // English inside an Arabic line stays readable.
        Assert.Equal("NPC \uFE8F", Scripts.Reorder("\uFE8F NPC", rightToLeft: true));

        Assert.True(Scripts.ParagraphIsRightToLeft("123 שלום", fallback: false));   // the first strong character decides
        Assert.False(Scripts.ParagraphIsRightToLeft("Hi שלום", fallback: true));
        Assert.True(Scripts.ParagraphIsRightToLeft("123", fallback: true));
        Assert.False(Scripts.HasRightToLeft("Hello 123"));
        Assert.Equal(TextDirection.RightToLeft, Localisation.DirectionOf("ar-EG"));
        Assert.Equal(TextDirection.RightToLeft, Localisation.DirectionOf("he"));
        Assert.Equal(TextDirection.LeftToRight, Localisation.DirectionOf("ja"));
        Assert.Equal(TextDirection.LeftToRight, Localisation.DirectionOf("ar", TextDirection.LeftToRight));
    }

    // ---- line breaks without spaces ---------------------------------------------------------------------

    [Fact]
    public void JapaneseWrapsBetweenCharactersButNeverStartsALineWithAStop()
    {
        const string text = "これはテストです。";
        var lines = new List<TextLine>();
        TextLayout.Break(text, Cells, 1f, 24f, float.PositiveInfinity, wrap: true, TextOverflow.Visible, lines);   // four characters a line
        Assert.Equal(new[] { "これはテ", "ストで", "す。" }, lines.Select(l => text.Substring(l.Start, l.Length)));

        // Latin still breaks only at spaces, and a Latin word beside Japanese is kept whole.
        const string mixed = "日本 Sage";
        TextLayout.Break(mixed, Cells, 1f, 30f, float.PositiveInfinity, wrap: true, TextOverflow.Visible, lines);
        Assert.Equal(new[] { "日本", "Sage" }, lines.Select(l => mixed.Substring(l.Start, l.Length)));
        Assert.False(Scripts.CanBreakBetween('a', 'b'));
        Assert.True(Scripts.CanBreakBetween('日', '本'));
        Assert.False(Scripts.CanBreakBetween('（', '日'));
    }

    // ---- fonts that fall back -----------------------------------------------------------------------

    [Fact]
    public void ACharacterTheFontLacksIsMeasuredAndRasterisedInTheFirstFallbackThatHasIt()
    {
        using var latin = TrueTypeFont.Load(LatinFont(), "latin.ttf");
        using var kana = TrueTypeFont.Load(JapaneseFont(), "kana.ttf");
        using var arabic = TrueTypeFont.Load(ArabicFont(), "arabic.ttf");
        Assert.True(latin.HasGlyph('A') && !latin.HasGlyph('あ'));

        var chain = new FontChain(latin, new[] { kana, arabic });
        Assert.Same(latin, chain.Resolve('A').Font);
        Assert.Same(kana, chain.Resolve('あ').Font);
        Assert.Same(arabic, chain.Resolve(0xFE91).Font);
        Assert.False(chain.Covers('ж'));

        // At an em of 10: 'A' 6, 'あ' 10, '\uFE91' 4.
        var measure = new FontTextMeasure(chain, 10f);
        Assert.Equal(20f, measure.Width("Aあ\uFE91", 1f), 3);
        Assert.Equal(latin.LineHeight(10f), measure.LineHeight, 3);

        // A presentation form no font has is drawn as its letter, from the font that has that.
        using var lettersOnly = TrueTypeFont.Load(TestFonts.Build("Letters", new[] { 0x0628 }, 300), "letters.ttf");
        var plain = new FontChain(latin, new[] { lettersOnly });
        Assert.Equal((lettersOnly, 0x0628), plain.Resolve(0xFE91));
        Assert.True(plain.Covers(0xFE91));

        // The glyph atlas of the fallback draws it.
        var atlas = new GlyphAtlas(kana, 20);
        var glyph = atlas.Get('あ');
        Assert.True(glyph.Width > 10 && glyph.Height > 10);
        Assert.True(atlas.Pixels[(glyph.Y + glyph.Height / 2) * GlyphAtlas.Width + glyph.X + glyph.Width / 2] > 128);
    }

    [Fact]
    public void ALanguagesFontsAreBehindEveryStyleFontAndDrawLabelsThatHaveNone()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["fonts/latin.ttf"] = LatinFont(), ["fonts/kana.ttf"] = JapaneseFont(),
            ["fonts/sage.ttf"] = File.ReadAllBytes(Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content", "fonts", "sage.ttf")),
        };
        var fonts = new UiFonts(path => files.TryGetValue(path.ToString(), out var data) ? data : null);
        var latin = AssetPath.Intern("fonts/latin.ttf");

        // No language fonts: the engine font's cells for a label with none, as before.
        Assert.Same(Cells, fonts.Measure(AssetPath.None, 0f, Cells));
        Assert.Single(fonts.Chain(latin)!.Fonts);
        Assert.Null(fonts.Chain(AssetPath.None));

        int version = fonts.Version;
        fonts.SetFallbacks(new[] { AssetPath.Intern("fonts/kana.ttf") });
        Assert.NotEqual(version, fonts.Version);
        var styled = fonts.Chain(latin)!;
        Assert.Equal(new[] { "fonts/latin.ttf", "fonts/kana.ttf" }, styled.Fonts.Select(f => f.Name));
        // A label with no font: the language's, then the engine's own letters.
        var bare = fonts.Chain(AssetPath.None)!;
        Assert.Equal(new[] { "fonts/kana.ttf", "fonts/sage.ttf" }, bare.Fonts.Select(f => f.Name));
        var measure = fonts.Measure(AssetPath.None, 9f, Cells);
        Assert.Equal(9f * 2, measure.Width("日本", 1f), 3);

        // A root whose fonts have fallbacks measures a label with no font in them.
        var root = new UiRoot(Cells) { Fonts = fonts };
        root.SetViewport(UiRoot.DefaultDesignSize);
        var label = root.Content.Add(new Label("日本語"));
        root.Layout();
        Assert.Equal(new Vector2(27f, 9f * kanaLine(files)), label.DesiredSize);

        static float kanaLine(Dictionary<string, byte[]> files) => TrueTypeFont.Load(files["fonts/kana.ttf"], "k").LineHeight(1f);
    }

    // ---- mirrored layout ------------------------------------------------------------------------------

    [Fact]
    public void ARightToLeftTreeIsMirrored()
    {
        var root = new UiRoot(Cells);
        root.SetViewport(UiRoot.DefaultDesignSize);
        var row = root.Content.Add(new Stack(Orientation.Row) { Spacing = 4f, Padding = new Thickness(10f, 0f, 0f, 0f) });
        var first = row.Add(new Button("One"));
        var second = row.Add(new Button("Two"));
        var bar = row.Add(new Bar { MinSize = new Vector2(100f, 8f), Value = 0.25f });
        var check = row.Add(new Checkbox("Box"));
        var label = root.Content.Add(new Label("Hi") { MinSize = new Vector2(100f, 9f), Anchors = Anchors.TopLeft, Margin = new Thickness(0f, 50f, 0f, 0f) });
        root.Layout();
        float firstX = first.Rect.X;
        Assert.Equal(10f, firstX);

        root.RightToLeft = true;
        root.Layout();
        float width = root.Size.X;
        // The row starts at the right, its padding on the right, and runs leftwards.
        Assert.Equal(width - 10f - first.Rect.Width, first.Rect.X);
        Assert.True(second.Rect.Right < first.Rect.X);
        Assert.Equal(width - 10f, row.ContentRect.Right);
        // A bar fills from the right; a checkbox's box is on its right, its text to the left of it.
        Assert.Equal(bar.ContentRect.Right, bar.FillRect.Right);
        Assert.Equal(25f, bar.FillRect.Width, 3);
        Assert.Equal(check.ContentRect.Right, check.BoxRect.Right);
        Assert.True(check.TextArea.Right < check.BoxRect.X);
        // A label anchored top-left is at the top right, its text aligned to its right.
        Assert.Equal(width - 100f, label.Rect.X);
        var plan = new UiRenderPlan();
        plan.Update(root, new UiStyles());
        var hi = plan.Commands.ToArray().Single(c => c.Kind == UiDrawKind.Text && c.Widget == label);
        Assert.Equal(width, hi.Rect.Right, 3);

        // Navigation goes by what is on screen: Right from the second button is the first.
        root.Focus(second);
        root.Navigate(UiNavigation.Right);
        Assert.Same(first, root.Focused);

        // A slider moves the way it is drawn: Left is more.
        var slider = row.Add(new Slider { MinSize = new Vector2(100f, 8f), Max = 10f, Step = 1f });
        root.Layout();
        root.Focus(slider);
        root.Update(new UiInput { Navigate = UiNavigation.Left });
        Assert.Equal(1f, slider.Value);
        Assert.True(slider.KnobRect.Right > slider.ContentRect.Right - slider.ContentRect.Width * 0.2f);
    }

    [Fact]
    public void ARightToLeftLabelIsDrawnJoinedAndReorderedALineAtATime()
    {
        var root = new UiRoot(Cells) { RightToLeft = true };
        root.SetViewport(UiRoot.DefaultDesignSize);
        var label = root.Content.Add(new Label("بيت 12\nسلام") { MinSize = new Vector2(200f, 0f) });
        var ellipsis = root.Content.Add(new Label("بيت بيت بيت بيت") { MaxWidth = 50f, Overflow = TextOverflow.Ellipsis, Margin = new Thickness(0f, 40f, 0f, 0f) });
        var field = root.Content.Add(new TextBox { Text = "بيت", Margin = new Thickness(0f, 80f, 0f, 0f) });
        var plan = new UiRenderPlan();
        plan.Update(root, new UiStyles());
        string Shown(UiDrawCommand c) => c.Text!.Substring(c.Start, c.Length);
        var lines = plan.Commands.ToArray().Where(c => c.Kind == UiDrawKind.Text && c.Widget == label).ToArray();
        Assert.Equal(new[] { "12 \uFE96\uFEF4\uFE91", "\uFEE1\uFEFC\uFEB3" }, lines.Select(Shown));
        Assert.Equal("\uFE91\uFEF4\uFE96 12\n\uFEB3\uFEFC\uFEE1", label.Shown);   // joined, still in reading order

        // A line cut short ends at its left: the "…" is there, in the line.
        var cut = plan.Commands.ToArray().Single(c => c.Kind == UiDrawKind.Text && c.Widget == ellipsis);
        Assert.StartsWith(TextLayout.Ellipsis, Shown(cut));
        Assert.False(cut.Ellipsis);

        // What is typed is shown as typed.
        Assert.Equal("بيت", field.Shown);
    }

    // ---- a translated game, end to end -------------------------------------------------------------------

    private const string Records = """
        [
          { "type": "language", "id": "ar", "name": "العربية", "fonts": ["fonts/arabic.ttf"] },
          { "type": "language", "id": "ja", "name": "日本語", "fonts": ["fonts/kana.ttf"] },
          { "type": "ui_style", "id": "body", "font": "fonts/latin.ttf", "fontSize": 10 },
          { "type": "ui_layout", "id": "page", "style": "body",
            "nodes": {
              "window": { "widget": "stack", "anchors": "topLeft", "direction": "row", "spacing": 2 },
              "title":  { "widget": "label", "parent": "window", "text": "@ui.title" },
              "count":  { "widget": "label", "parent": "window", "text": "@ui.arrows", "args": { "count": "arrows" } }
            } },
          { "type": "screen", "id": "page", "layout": "page", "viewModel": "script_page" }
        ]
        """;

    public sealed class Page : IViewModel
    {
        public int Arrows = 3;
    }

    private static void WriteGame(string folder, bool complete)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "game.json"), """{ "name": "Scripts", "id": "scripts", "mounts": ["content"] }""");
        // Validated as a game: no C#, so the screen has no view-model and its count no argument.
        WriteContent(Path.Combine(folder, "content"), complete, Records.Replace("\"viewModel\": \"script_page\"", "\"viewModel\": \"\"")
                                                                     .Replace(", \"args\": { \"count\": \"arrows\" }", ""));
    }

    private static void WriteContent(string content, bool complete, string records = Records)
    {
        void Write(string relative, string text)
        {
            string file = Path.Combine(content, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        Write("data/ui.json", records);
        Directory.CreateDirectory(Path.Combine(content, "fonts"));
        File.WriteAllBytes(Path.Combine(content, "fonts", "latin.ttf"), LatinFont());
        File.WriteAllBytes(Path.Combine(content, "fonts", "arabic.ttf"), ArabicFont());
        File.WriteAllBytes(Path.Combine(content, "fonts", "kana.ttf"), JapaneseFont());
        Write("strings/en/ui.json", """
            { "title": "Items", "arrows": { "one": "{count} arrow", "other": "{count} arrows" }, "gold": "{gold:n0} gold" }
            """);
        Write("strings/ja/ui.json", """
            { "title": "持物", "arrows": { "other": "{count}本" }, "gold": "{gold:n0}" }
            """);
        Write("strings/ar/ui.json", complete ? """
            { "title": "بيت", "arrows": { "zero": "لا سهام", "one": "سهم", "two": "سهمان", "few": "{count} سهام", "many": "{count} سهما", "other": "{count} سهم" }, "gold": "{gold:n0} ذهب" }
            """ : """
            { "titel": "بيت", "arrows": { "one": "سهم", "other": "{count} سهم" }, "gold": "{amount} ذهب ж" }
            """);
    }

    [Fact]
    public void AJapaneseAndAnArabicTableRenderInTheirOwnFontsAndArabicIsMirrored()
    {
        var content = new MountFixture();
        WriteContent(content.Dir("scripts"), complete: true);
        content.Mount("scripts", "scripts");
        using var app = HeadlessApp.Bare().With(new UiModule()).Mount(content)
            .OnRegistered(a => a.Engine.Vocabularies.Of<IViewModel>().Register("script_page", typeof(Page), () => new Page()))
            .Boot("scripts");
        var text = app.World.Resources.Get<Localisation>();
        var fonts = app.World.Resources.Get<UiFonts>();
        var stack = app.World.Resources.Get<UiScreenStack>();
        var styles = app.World.Resources.Get<UiStyles>();

        app.CVars.Execute("lang ja");
        Assert.Equal("ja", text.Language);
        Assert.Equal(new[] { AssetPath.Intern("fonts/kana.ttf") }, text.Fonts);
        Assert.Equal(text.Fonts, fonts.Fallbacks);
        var layer = stack.Open(new RecordId("scripts", "page"));
        stack.Update(default);
        var title = layer.Content.Find<Label>("title")!;
        Assert.Equal("持物", title.Text);
        Assert.Equal("3本", layer.Content.Find<Label>("count")!.Text);
        Assert.Equal(20f, title.DesiredSize.X, 3);        // two ideographs, an em each, from the language's font behind the style's
        Assert.False(layer.Root.RightToLeft);

        app.CVars.Execute("lang ar");
        stack.Update(default);
        Assert.Equal(TextDirection.RightToLeft, text.Direction);
        Assert.True(layer.Root.RightToLeft);
        title = layer.Content.Find<Label>("title")!;
        Assert.Equal("بيت", title.Text);
        Assert.Equal(12f, title.DesiredSize.X, 3);        // three Arabic forms, 4 each
        Assert.Equal("3 سهام", layer.Content.Find<Label>("count")!.Text);   // Arabic's few form for 3
        // The title is the first in its row: at the right of the screen, the second to its left.
        Assert.True(title.Rect.X > layer.Content.Find<Label>("count")!.Rect.X);
        var plan = new UiRenderPlan();
        plan.Update(layer.Root, styles);
        var drawn = plan.Commands.ToArray().Single(c => c.Kind == UiDrawKind.Text && c.Widget == title);
        Assert.Equal("\uFE96\uFEF4\uFE91", drawn.Text!.Substring(drawn.Start, drawn.Length));
        var chain = fonts.Chain(drawn.Font)!;
        Assert.Equal("fonts/arabic.ttf", chain.Resolve(0xFE91).Font.Name);
        Assert.Equal("fonts/latin.ttf", chain.Resolve('3').Font.Name);

        app.CVars.Execute("lang en");
        stack.Update(default);
        Assert.False(layer.Root.RightToLeft);
        Assert.Empty(fonts.Fallbacks);
    }

    [Fact]
    public void ValidateReportsWhatATranslationLacks()
    {
        string complete = TestEnv.NewTempDir();
        WriteGame(complete, complete: true);
        var report = Validate(complete);
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("strings/ja") || w.Contains("strings/ar") || w.Contains("language"));

        string partial = TestEnv.NewTempDir();
        WriteGame(partial, complete: false);
        report = Validate(partial);
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        var ar = report.Warnings.Where(w => w.Contains("strings/ar")).ToList();
        Assert.Contains(ar, w => w.Contains("strings/ar: 1 of 3 key(s) have no 'ar' text and show in English: @ui.title"));
        Assert.Contains(ar, w => w.Contains("'ui.titel' is in strings/ar but not strings/en") && w.Contains("ui.title"));
        Assert.Contains(ar, w => w.Contains("'ui.gold' names {amount}, which the English text does not"));
        Assert.Contains(ar, w => w.Contains("'ui.gold' leaves out {gold}"));
        Assert.Contains(ar, w => w.Contains("'ui.arrows' has no zero, two, few, many form(s)"));
        Assert.Contains(ar, w => w.Contains("1 character(s) no font has") && w.Contains("U+0436"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("strings/ja"));
    }

    private static ValidationReport Validate(string game) => ContentValidation.Run(new ValidateOptions
    {
        GameDirectory = game,
        EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "engine_content"),
        AvailablePlugins = BasePlugins.All(),
    });
}
