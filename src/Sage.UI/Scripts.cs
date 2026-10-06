#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Sage.UI;

// Text in scripts other than Latin (issue #345): which way a language runs, the `language` record that
// says so and names its fonts, and what a line of Arabic, Hebrew, Japanese or Chinese needs before it is
// measured and drawn — Arabic letters joined into the forms they take in a word, a line's characters put
// in the order they are seen (the bidirectional algorithm, simplified), and the places a line of
// ideographs may break. Headless, so a test sees exactly what will be drawn.

// Which way text runs and a screen is laid out. Auto: as the language's code says (Localisation.DirectionOf).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum TextDirection { Auto, LeftToRight, RightToLeft }

// A language a game is translated into (issue #345): what the strings/<code>/ tables cannot say about
// themselves. The record's name is the language code (`ja`, `ar`; `code` when the name cannot be, as
// in `pt-BR`), matched to the `lang` cvar ignoring case and '-' against '_'.
//
//   { "type": "language", "id": "ar", "name": "العربية", "fonts": ["fonts/naskh.ttf"] }
//
// A language with no record is still shown: left to right unless its code is a right-to-left one, and
// in the fonts its styles name.
[Record("language", Plugin = UiModule.Id)]
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class LanguageRecord
{
    [Property(Tooltip = "The language's name in itself, for a language menu: \"日本語\", \"العربية\"")]
    public string Name = "";

    [Property(Tooltip = "The language code the lang cvar and strings/<code>/ use, when the record's name cannot be it (pt-BR); empty: the record's name")]
    public string Code = "";

    [Property(Tooltip = "Which way its text runs and its screens are laid out: Auto (from the code: ar, he, fa, ur... are right to left), LeftToRight or RightToLeft")]
    public TextDirection Direction;

    [AssetKind("font"), Property(Tooltip = "Fonts (.ttf or .otf) tried in order for a character a label's own font lacks, and what a label with no font of its own is drawn in, while the language is shown")]
    public List<AssetPath> Fonts = new();

    // The code this record is for: Code, or the record's name.
    internal string CodeFor(RecordId id) => Code.Length > 0 ? Code : id.Name;

    internal static bool SameCode(string a, string b) =>
        string.Equals(a.Replace('_', '-'), b.Replace('_', '-'), StringComparison.OrdinalIgnoreCase);
}

// What a line of text needs before it is drawn, by script.
internal static class Scripts
{
    // ---- Arabic joining ---------------------------------------------------------------------------

    // Lam followed by an alef is one ligature: isolated, then final at +1.
    private static readonly (char Alef, char Isolated)[] LamAlef =
        { ('\u0622', '\uFEF5'), ('\u0623', '\uFEF7'), ('\u0625', '\uFEF9'), ('\u0627', '\uFEFB') };

    // Each letter's presentation forms (Unicode's Arabic Presentation Forms-A/B): isolated, final,
    // initial, medial; a letter that joins only to the one before it (alef, dal, reh, waw...) has the
    // first two. Fonts that cover Arabic have these, which is what lets stb_truetype, which shapes
    // nothing, draw joined script.
    private static readonly Dictionary<char, char[]> Forms = BuildForms();
    private static readonly Dictionary<char, char> Bases = BuildBases();

    private static Dictionary<char, char[]> BuildForms()
    {
        var forms = new Dictionary<char, char[]>();
        void Dual(char letter, int first) => forms[letter] = new[] { (char)first, (char)(first + 1), (char)(first + 2), (char)(first + 3) };
        void Right(char letter, int first) => forms[letter] = new[] { (char)first, (char)(first + 1) };
        forms['\u0621'] = new[] { '\uFE80' };
        Right('\u0622', 0xFE81); Right('\u0623', 0xFE83); Right('\u0624', 0xFE85); Right('\u0625', 0xFE87);
        Dual('\u0626', 0xFE89); Right('\u0627', 0xFE8D); Dual('\u0628', 0xFE8F); Right('\u0629', 0xFE93);
        Dual('\u062A', 0xFE95); Dual('\u062B', 0xFE99); Dual('\u062C', 0xFE9D); Dual('\u062D', 0xFEA1);
        Dual('\u062E', 0xFEA5); Right('\u062F', 0xFEA9); Right('\u0630', 0xFEAB); Right('\u0631', 0xFEAD);
        Right('\u0632', 0xFEAF); Dual('\u0633', 0xFEB1); Dual('\u0634', 0xFEB5); Dual('\u0635', 0xFEB9);
        Dual('\u0636', 0xFEBD); Dual('\u0637', 0xFEC1); Dual('\u0638', 0xFEC5); Dual('\u0639', 0xFEC9);
        Dual('\u063A', 0xFECD); Dual('\u0641', 0xFED1); Dual('\u0642', 0xFED5); Dual('\u0643', 0xFED9);
        Dual('\u0644', 0xFEDD); Dual('\u0645', 0xFEE1); Dual('\u0646', 0xFEE5); Dual('\u0647', 0xFEE9);
        Right('\u0648', 0xFEED); Right('\u0649', 0xFEEF); Dual('\u064A', 0xFEF1);
        // Persian and Urdu letters.
        Dual('\u067E', 0xFB56); Dual('\u0686', 0xFB7A); Right('\u0698', 0xFB8A); Dual('\u06A9', 0xFB8E);
        Dual('\u06AF', 0xFB92); Dual('\u06CC', 0xFBFC);
        return forms;
    }

    private static Dictionary<char, char> BuildBases()
    {
        var bases = new Dictionary<char, char>();
        foreach (var (letter, forms) in Forms)
            foreach (char form in forms) bases[form] = letter;
        foreach (var (alef, isolated) in LamAlef) { bases[isolated] = alef; bases[(char)(isolated + 1)] = alef; }
        return bases;
    }

    // A presentation form's letter (for a font that has the letter but not its forms); 0 for anything else.
    public static char BaseOf(int codepoint) =>
        codepoint is >= 0xFB50 and <= 0xFEFF && Bases.TryGetValue((char)codepoint, out char letter) ? letter : '\0';

    private static bool Transparent(char c) => c is >= '\u064B' and <= '\u065F' or '\u0670' or >= '\u06D6' and <= '\u06ED';

    // Joins to the letter after it: a dual-joining letter, or tatweel.
    private static bool JoinsForward(char c) => c == '\u0640' || Forms.TryGetValue(c, out var f) && f.Length == 4;

    // Joins to the letter before it.
    private static bool JoinsBack(char c) => c == '\u0640' || Forms.TryGetValue(c, out var f) && f.Length >= 2;

    public static bool NeedsShaping(string text)
    {
        foreach (char c in text)
            if (c is >= '\u0621' and <= '\u06FF') return true;
        return false;
    }

    // Arabic letters in `text` in the form each takes beside its neighbours, lam-alef as one ligature.
    // Anything else is kept as it is; text with no Arabic is returned itself.
    public static string Shape(string text)
    {
        if (!NeedsShaping(text)) return text;
        var shaped = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!Forms.TryGetValue(c, out var forms)) { shaped.Append(c); continue; }
            bool back = Previous(text, i) is { } p && JoinsForward(p);
            if (c == '\u0644' && NextIndex(text, i) is int n && n == i + 1 && Ligature(text[n]) is char isolated)
            {
                shaped.Append(back ? (char)(isolated + 1) : isolated);
                i = n;
                continue;
            }
            bool forward = forms.Length == 4 && NextIndex(text, i) is int next && JoinsBack(text[next]);
            int form = forms.Length < 2 ? 0 : back && forward ? 3 : back ? 1 : forward ? 2 : 0;
            shaped.Append(forms[Math.Min(form, forms.Length - 1)]);
        }
        return shaped.ToString();
    }

    private static char? Ligature(char alef)
    {
        foreach (var (a, isolated) in LamAlef)
            if (a == alef) return isolated;
        return null;
    }

    private static char? Previous(string text, int i)
    {
        for (int k = i - 1; k >= 0; k--)
            if (!Transparent(text[k])) return text[k];
        return null;
    }

    private static int? NextIndex(string text, int i)
    {
        for (int k = i + 1; k < text.Length; k++)
            if (!Transparent(text[k])) return k;
        return null;
    }

    // ---- bidirectional text -------------------------------------------------------------------------

    // The bidirectional character types the reordering uses (UAX #9, without explicit embeddings).
    private enum Bidi : byte { L, R, AL, EN, AN, ES, ET, CS, NSM, WS, ON }

    private static Bidi TypeOf(char c)
    {
        if (c is >= '0' and <= '9' || c is >= '\u06F0' and <= '\u06F9') return Bidi.EN;
        if (c is >= '\u0660' and <= '\u0669' or '\u066B' or '\u066C') return Bidi.AN;
        if (c is >= '\u0591' and <= '\u05BD' or >= '\u064B' and <= '\u065F' or '\u0670' or >= '\u06D6' and <= '\u06ED') return Bidi.NSM;
        if (c is >= '\u0590' and <= '\u05FF' or >= '\u07C0' and <= '\u085F' or >= '\uFB1D' and <= '\uFB4F') return Bidi.R;
        if (c is >= '\u0600' and <= '\u06FF' or >= '\u0700' and <= '\u08FF' or >= '\uFB50' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF') return Bidi.AL;
        if (c is '+' or '-') return Bidi.ES;
        if (c is '#' or '$' or '%' or '°' or '€' or '£' or '¥' or '¢') return Bidi.ET;
        if (c is ',' or '.' or ':' or '/' or '\u00A0') return Bidi.CS;
        if (char.IsWhiteSpace(c)) return Bidi.WS;
        if (char.IsLetter(c) || char.IsSurrogate(c) || char.IsLetterOrDigit(c)) return Bidi.L;
        return Bidi.ON;
    }

    // Whether a character is written right to left (Hebrew, Arabic and the scripts like them).
    public static bool IsRightToLeft(char c) => TypeOf(c) is Bidi.R or Bidi.AL;

    // Whether a line has anything written right to left: only then is it reordered.
    public static bool HasRightToLeft(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
            if (c >= '\u0590' && TypeOf(c) is Bidi.R or Bidi.AL or Bidi.AN) return true;
        return false;
    }

    // The direction a paragraph takes from its first strong character; `fallback` when it has none.
    public static bool ParagraphIsRightToLeft(ReadOnlySpan<char> text, bool fallback)
    {
        foreach (char c in text)
            switch (TypeOf(c))
            {
                case Bidi.L: return false;
                case Bidi.R: case Bidi.AL: return true;
            }
        return fallback;
    }

    // One line in the order it is seen, left to right (UAX #9 rules W1–W7, N1–N2, I1–I2 and L2, with
    // mirrored brackets, L4): Arabic and Hebrew runs reversed, numbers and Latin inside them kept
    // reading left to right. `rightToLeft` is the paragraph's direction. Allocates the string it returns.
    public static string Reorder(ReadOnlySpan<char> line, bool rightToLeft)
    {
        int n = line.Length;
        if (n == 0) return "";
        var types = new Bidi[n];
        for (int i = 0; i < n; i++) types[i] = TypeOf(line[i]);
        Bidi sos = rightToLeft ? Bidi.R : Bidi.L;

        // W1: a mark takes the type of what it is on.
        for (int i = 0; i < n; i++)
            if (types[i] == Bidi.NSM) types[i] = i == 0 ? sos : types[i - 1];
        // W2: European digits after Arabic letters are Arabic numbers. W3: AL is R.
        Bidi strong = sos;
        for (int i = 0; i < n; i++)
        {
            if (types[i] is Bidi.L or Bidi.R or Bidi.AL) strong = types[i];
            else if (types[i] == Bidi.EN && strong == Bidi.AL) types[i] = Bidi.AN;
        }
        for (int i = 0; i < n; i++) if (types[i] == Bidi.AL) types[i] = Bidi.R;
        // W4: one separator between two numbers of a kind joins them.
        for (int i = 1; i < n - 1; i++)
        {
            if (types[i] == Bidi.ES && types[i - 1] == Bidi.EN && types[i + 1] == Bidi.EN) types[i] = Bidi.EN;
            else if (types[i] == Bidi.CS && types[i - 1] == types[i + 1] && types[i - 1] is Bidi.EN or Bidi.AN) types[i] = types[i - 1];
        }
        // W5: terminators beside European digits are digits.
        for (int i = 0; i < n; i++)
        {
            if (types[i] != Bidi.ET) continue;
            int end = i;
            while (end < n && types[end] == Bidi.ET) end++;
            bool digit = i > 0 && types[i - 1] == Bidi.EN || end < n && types[end] == Bidi.EN;
            if (digit) for (int k = i; k < end; k++) types[k] = Bidi.EN;
            i = end - 1;
        }
        // W6: other separators and terminators are neutral. W7: digits in a left-to-right context are L.
        for (int i = 0; i < n; i++) if (types[i] is Bidi.ES or Bidi.ET or Bidi.CS) types[i] = Bidi.ON;
        strong = sos;
        for (int i = 0; i < n; i++)
        {
            if (types[i] is Bidi.L or Bidi.R) strong = types[i];
            else if (types[i] == Bidi.EN && strong == Bidi.L) types[i] = Bidi.L;
        }
        // N1, N2: a run of neutrals between two of the same direction takes it, else the paragraph's.
        for (int i = 0; i < n; i++)
        {
            if (types[i] is not (Bidi.WS or Bidi.ON)) continue;
            int end = i;
            while (end < n && types[end] is Bidi.WS or Bidi.ON) end++;
            Bidi before = i == 0 ? sos : Direction(types[i - 1]);
            Bidi after = end == n ? sos : Direction(types[end]);
            Bidi resolved = before == after ? before : sos;
            for (int k = i; k < end; k++) types[k] = resolved;
            i = end - 1;
        }
        // I1, I2: levels.
        int baseLevel = rightToLeft ? 1 : 0;
        var levels = new byte[n];
        byte highest = 0;
        for (int i = 0; i < n; i++)
        {
            int level = baseLevel;
            if (baseLevel == 0) level += types[i] == Bidi.R ? 1 : types[i] is Bidi.EN or Bidi.AN ? 2 : 0;
            else level += types[i] is Bidi.L or Bidi.EN or Bidi.AN ? 1 : 0;
            levels[i] = (byte)level;
            if (level > highest) highest = (byte)level;
        }
        // L2: from the highest level down to the lowest odd one, reverse every run at it or above.
        var chars = line.ToArray();
        for (int i = 0; i < n; i++)
            if ((levels[i] & 1) != 0) chars[i] = Mirror(chars[i]);
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        for (int level = highest; level >= 1; level--)
            for (int i = 0; i < n; i++)
            {
                if (levels[order[i]] < level) continue;
                int end = i;
                while (end < n && levels[order[end]] >= level) end++;
                Array.Reverse(order, i, end - i);
                i = end;
            }
        var visual = new char[n];
        for (int i = 0; i < n; i++) visual[i] = chars[order[i]];
        // A surrogate pair reversed is two halves the wrong way round: put each back.
        for (int i = 0; i + 1 < n; i++)
            if (char.IsLowSurrogate(visual[i]) && char.IsHighSurrogate(visual[i + 1])) { (visual[i], visual[i + 1]) = (visual[i + 1], visual[i]); i++; }
        return new string(visual);
    }

    private static Bidi Direction(Bidi type) => type is Bidi.L ? Bidi.L : Bidi.R;   // EN and AN count as R (N1)

    private static char Mirror(char c) => c switch
    {
        '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', '}' => '{', '<' => '>', '>' => '<',
        '«' => '»', '»' => '«', '‹' => '›', '›' => '‹', _ => c,
    };

    // ---- line breaks ---------------------------------------------------------------------------------

    // Ideographs, kana, hangul and their punctuation: written without spaces, a line may break between
    // any two of them.
    public static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    // What may not start a line (closing brackets and stops, small kana, the long-vowel mark) and what
    // may not end one (opening brackets): Japanese kinsoku, simplified.
    private static bool NoStart(char c) =>
        "、。，．・：；？！ー」』）】〕〉》｝］ゝゞぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ々〜.,!?:;)]}…%".IndexOf(c) >= 0;

    private static bool NoEnd(char c) => "「『（【〔〈《｛［([{".IndexOf(c) >= 0;

    // Whether a line may break between `left` and `right`, neither a space: between two characters of
    // which one is CJK, unless the rules above forbid it.
    public static bool CanBreakBetween(char left, char right) =>
        (IsCjk(left) || IsCjk(right)) && !NoStart(right) && !NoEnd(left) && !char.IsHighSurrogate(left);
}
