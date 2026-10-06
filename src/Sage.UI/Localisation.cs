#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.UI;

// Text by key (decision D3, docs/design/05 §3.7, issue #96). Content never writes a player-facing
// sentence inline; it writes a key, `@ns.key`, and this resolves it when the text is shown, in the
// language the `lang` cvar names:
//
//   strings/en/ui.json          { "inventory": { "title": "Inventory", "weight": "Weight {current} / {max}" },
//                                 "arrows": { "one": "{count} arrow", "other": "{count} arrows" } }
//   "@ui.inventory.title"   ->  "Inventory"
//   "@ui.arrows", count 3   ->  "3 arrows"
//
// - **Tables** are `strings/<lang>/*.json` in any mount; a file's path under the language is its
//   namespace (`strings/en/ui.json` is `ui`, `strings/en/items/weapons.json` is `items.weapons`) and
//   nested objects add to the key. Mounts are read in order and a later one's key wins, so a mod can
//   retranslate one line without copying a file.
// - **Placeholders** are `{name}`, filled by name (`{{` and `}}` are braces); one nobody filled stays as
//   written. **Plural forms** are an object of CLDR categories (`zero`, `one`, `two`, `few`, `many`,
//   `other`; `other` required), chosen by the `count` argument with the language's rule, and `zero`,
//   when a table has it, for a count of 0 in any language.
// - **A missing key shows the key itself** (`@ui.nope`) and logs one warning per key; `sage validate`
//   warns about every key content names that no table has (UiModule). A key missing from the current
//   language falls back to English before it is missing.
// - `@@` at the start is a literal `@`: "@@home" shows "@home".
// - `{action:Use}` is what the player presses for an action on the device in use (InputPrompts, issue
//   #352), in any text, with or without other placeholders; an argument of that name wins.
//
// Resolving a key allocates nothing: the tables are keyed by the text as content writes it, '@'
// included. Filling placeholders makes the string it returns.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class Localisation
{
    public const string DefaultLanguage = "en";

    private Dictionary<string, LocalisedText> _texts = new(StringComparer.Ordinal);
    private Dictionary<string, LocalisedText> _fallback = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private VirtualFileSystem? _vfs;
    [ThreadStatic] private static StringBuilder? _builder;

    // The language shown: what `lang` says, as long as it has tables (English otherwise).
    public string Language { get; private set; } = DefaultLanguage;

    // Which way the language's text runs (issue #345): its `language` record's `direction`, or, when it
    // has none or says auto, right to left for the scripts that are (Arabic, Hebrew, Persian, Urdu...).
    // Screens shown in a right-to-left language are mirrored (UiRoot.RightToLeft).
    public TextDirection Direction { get; private set; } = TextDirection.LeftToRight;

    public bool IsRightToLeft => Direction == TextDirection.RightToLeft;

    // The fonts the language's `language` record names: tried, in order, for a character a label's own
    // font lacks, and what a label with no font of its own is drawn in (UiFonts.Fallbacks).
    public IReadOnlyList<AssetPath> Fonts { get; private set; } = Array.Empty<AssetPath>();

    // How a {placeholder:format} is written in the language: "{gold:n0}" is "1,234" in English and
    // "1.234" in German. The invariant culture when the system knows no such language.
    public CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;

    // Moves whenever the tables or the language change, or what an `{action:...}` prompt shows (a rebind,
    // the device in use): what shows text re-resolves it then.
    public int Version => _tablesVersion + (Prompts?.Version ?? 0);

    // The input glyphs `{action:...}` shows; UiModule sets it. Without one, the placeholder stays as written.
    public InputPrompts? Prompts { get; internal set; }

    private int _tablesVersion;
    internal int TablesVersion => _tablesVersion;

    // Every key the current language has (its own and English's), with the '@'.
    public IEnumerable<string> Keys => _texts.Keys.Union(_fallback.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);

    public int Count => _texts.Count;

    // "@ns.key": a key, as opposed to text to show as it is ("@@" is not one).
    public static bool IsKey(string? text) => text is { Length: > 1 } && text[0] == '@' && text[1] != '@';

    // Whether a key ("@ui.title" or "ui.title") has text in the current language or English.
    public bool Has(string key) => Find(key.Length > 0 && key[0] == '@' ? key : "@" + key) != null;

    // Text to show for `text`: a key's text (a plural key's `other` form, placeholders as written), or
    // `text` itself when it is not a key ("@@" becoming "@"). A missing key is shown as it is, and said.
    public string Text(string text)
    {
        string shown = !IsKey(text) ? Literal(text) : Find(text) is { } entry ? entry.Other : Missing(text);
        if (Prompts == null || shown.IndexOf("{" + InputPrompts.Placeholder, StringComparison.Ordinal) < 0) return shown;
        var none = new ArrayArgs(Array.Empty<(string, object?)>());
        var builder = _builder ??= new StringBuilder(128);
        builder.Clear();
        Fill(builder, shown, ref none, Culture);
        return builder.ToString();
    }

    // A key's text for `count` things: its plural form, with {count} and the other placeholders filled.
    public string Text(string text, double count) => Format(text, ("count", count));

    // `text` (a key or not) with its {placeholders} filled from `args`; `count`, when given, chooses the
    // plural form. Allocates the string it returns: call it when the values change, not every frame.
    public string Format(string text, params (string Name, object? Value)[] args)
    {
        var values = new ArrayArgs(args);
        return Format(text, ref values);
    }

    // The plural category `count` is in, in the current language.
    public PluralCategory PluralOf(double count) => PluralRules.Select(Language, count);

    // ---- internals the binder uses ----------------------------------------------------------------

    internal LocalisedText? Find(string key) =>
        _texts.TryGetValue(key, out var entry) || _fallback.TryGetValue(key, out entry) ? entry : null;

    internal string Format<TArgs>(string text, ref TArgs args) where TArgs : IPlaceholderValues
    {
        string template;
        if (IsKey(text))
        {
            if (Find(text) is not { } entry) return Missing(text);
            template = args.TryGetNumber("count", out double count) ? entry.Form(PluralOf(count), count) : entry.Other;
        }
        else template = Literal(text);
        if (template.IndexOf('{') < 0 && template.IndexOf('}') < 0) return template;

        var builder = _builder ??= new StringBuilder(128);
        builder.Clear();
        Fill(builder, template, ref args, Culture);
        return builder.ToString();
    }

    private static string Literal(string text) => text.Length > 1 && text[0] == '@' && text[1] == '@' ? text[1..] : text;

    private string Missing(string key)
    {
        if (_warned.Add(key))
            Log.Warn(LogCat.UI, $"no text for '{key}' in '{Language}' (strings/{Language}/{Namespace(key)}.json); showing the key");
        return key;
    }

    private static string Namespace(string key)
    {
        int dot = key.IndexOf('.');
        return dot > 1 ? key[1..dot] : key.TrimStart('@');
    }

    // {name} → its value; {name:format} → its value formatted for the language ("n0", "p0", "d" for a
    // date: .NET's format strings, in Culture); {action:Name} → the input glyph (#352); {{ and }} → a
    // brace; an unfilled or unclosed placeholder stays as written.
    private void Fill<TArgs>(StringBuilder builder, string template, ref TArgs args, CultureInfo culture) where TArgs : IPlaceholderValues
    {
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c) { builder.Append(c); i++; continue; }
            if (c != '{') { builder.Append(c); continue; }
            int close = template.IndexOf('}', i + 1);
            if (close < 0) { builder.Append(template, i, template.Length - i); return; }
            // An argument named by the whole text wins ("action:Use" given as one); then an input glyph;
            // then name:format.
            var whole = template.AsSpan(i + 1, close - i - 1);
            int colon = whole.IndexOf(':');
            if (args.TryAppend(whole, ReadOnlySpan<char>.Empty, culture, builder)) { }
            else if (Prompts != null && whole.StartsWith(InputPrompts.Placeholder, StringComparison.Ordinal) && whole.Length > InputPrompts.Placeholder.Length)
                builder.Append(Prompts.Glyph(whole[InputPrompts.Placeholder.Length..].Trim().ToString()));
            else if (colon < 0 || !args.TryAppend(whole[..colon], whole[(colon + 1)..], culture, builder))
                builder.Append(template, i, close - i + 1);
            i = close;
        }
    }

    // ---- loading ----------------------------------------------------------------------------------

    // Reads `strings/<language>/**/*.json` (and English's, for what it lacks) from every mount. A
    // language no mount has falls back to English, and says so.
    public void Load(VirtualFileSystem vfs, string language)
    {
        _vfs = vfs;
        language = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim();
        var english = Read(vfs, DefaultLanguage);
        var texts = language == DefaultLanguage ? english : Read(vfs, language);
        if (language != DefaultLanguage && texts.Count == 0)
        {
            Log.Warn(LogCat.UI, $"lang '{language}': no mount has strings/{language}/; showing English");
            language = DefaultLanguage;
            texts = english;
        }
        Language = language;
        Culture = CultureOf(language);
        Direction = DirectionOf(language, TextDirection.Auto);
        Fonts = Array.Empty<AssetPath>();
        Languages = Available(vfs);
        _texts = texts;
        _fallback = language == DefaultLanguage ? new Dictionary<string, LocalisedText>(StringComparer.Ordinal) : english;
        _warned.Clear();
        _tablesVersion++;
    }

    // What the language's `language` record says about it (UiModule, after Load): its direction (Auto:
    // by its code) and its fonts.
    internal void Describe(TextDirection direction, IReadOnlyList<AssetPath> fonts)
    {
        Direction = DirectionOf(Language, direction);
        Fonts = fonts;
        _tablesVersion++;
    }

    // Right to left for the languages whose scripts are, by primary subtag; left to right otherwise.
    public static TextDirection DirectionOf(string language, TextDirection declared = TextDirection.Auto)
    {
        if (declared != TextDirection.Auto) return declared;
        int dash = language.IndexOfAny(new[] { '-', '_' });
        string primary = (dash < 0 ? language : language[..dash]).ToLowerInvariant();
        return primary is "ar" or "he" or "iw" or "fa" or "ur" or "yi" or "ps" or "sd" or "ug" or "ckb" or "dv" or "syr"
            ? TextDirection.RightToLeft : TextDirection.LeftToRight;
    }

    private static CultureInfo CultureOf(string language)
    {
        try { return CultureInfo.GetCultureInfo(language.Replace('_', '-')); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    // Every language some mount has tables for (the folders under strings/), sorted.
    public static IReadOnlyList<string> LanguagesIn(VirtualFileSystem vfs)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (path, _) in vfs.Enumerate(VirtualPath.Parse("strings"), "*.json", recursive: true))
        {
            var parts = path.Value.Split('/');
            if (parts.Length >= 3) found.Add(parts[1]);
        }
        return found.ToList();
    }

    // The languages some mount has strings for (`strings/<lang>/`), English always among them, in order:
    // what an options screen offers for `lang` (issue #339). Read at each Load.
    public IReadOnlyList<string> Languages { get; private set; } = new[] { DefaultLanguage };

    private static string[] Available(VirtualFileSystem vfs)
    {
        var found = new SortedSet<string>(LanguagesIn(vfs), StringComparer.OrdinalIgnoreCase) { DefaultLanguage };
        return found.ToArray();
    }

    // The same language again, from the same mounts: hot reload.
    public void Reload()
    {
        if (_vfs != null) Load(_vfs, Language);
    }

    // What a table says, by key with its '@', for tests and tools.
    internal static Dictionary<string, LocalisedText> Read(VirtualFileSystem vfs, string language)
    {
        var texts = new Dictionary<string, LocalisedText>(StringComparer.Ordinal);
        VirtualPath folder;
        try { folder = VirtualPath.Parse("strings/" + language); }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            Log.Error(LogCat.UI, $"lang '{language}' is not a folder name: {ex.Message}");
            return texts;
        }
        string prefix = folder.Value + "/";
        foreach (var (path, mount) in vfs.Enumerate(folder, "*.json", recursive: true))
        {
            string where = $"{mount.Name}:{path}";
            string relative = path.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Value[prefix.Length..] : path.Value;
            string ns = relative[..^".json".Length].Replace('/', '.');
            JsonNode? root;
            try
            {
                using var stream = mount.Open(path);
                root = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Log.Error(LogCat.UI, $"{where}: invalid JSON: {ex.Message}");
                continue;
            }
            if (root is not JsonObject table)
            {
                Log.Error(LogCat.UI, $"{where}: a string table is an object of keys to text");
                continue;
            }
            Flatten(table, "@" + ns, where, texts);
        }
        return texts;
    }

    private static void Flatten(JsonObject node, string prefix, string where, Dictionary<string, LocalisedText> into)
    {
        foreach (var (name, value) in node)
        {
            string key = prefix + "." + name;
            switch (value)
            {
                case JsonValue text when text.TryGetValue(out string? s):
                    into[key] = new LocalisedText(s, where);
                    break;
                case JsonObject forms when PluralRules.IsPlural(forms):
                    var entry = LocalisedText.Plural(forms, where, out string? problem);
                    if (entry == null) Log.Error(LogCat.UI, $"{where}: '{key[1..]}': {problem}");
                    else into[key] = entry;
                    break;
                case JsonObject nested:
                    Flatten(nested, key, where, into);
                    break;
                default:
                    Log.Error(LogCat.UI, $"{where}: '{key[1..]}' is {value?.GetValueKind().ToString().ToLowerInvariant() ?? "null"}: " +
                                         "a text is a string, plural forms an object of zero/one/two/few/many/other, and anything else an object of keys");
                    break;
            }
        }
    }
}

// A CLDR plural category (https://cldr.unicode.org/index/cldr-spec/plural-rules).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum PluralCategory { Zero, One, Two, Few, Many, Other }

// One key's text: a string, or its plural forms.
internal sealed class LocalisedText
{
    private readonly string?[]? _forms;

    public LocalisedText(string text, string file)
    {
        Other = text;
        File = file;
    }

    private LocalisedText(string?[] forms, string file)
    {
        _forms = forms;
        Other = forms[(int)PluralCategory.Other]!;
        File = file;
    }

    public string Other { get; }
    public string File { get; }
    public bool IsPlural => _forms != null;

    // Every text it has: the one, or each plural form.
    public IEnumerable<string> Texts => _forms == null ? new[] { Other } : _forms.Where(f => f != null).Select(f => f!);

    public bool HasForm(PluralCategory category) => _forms != null && _forms[(int)category] != null;

    // The form for `category`, `zero` for a count of 0 when there is one, and `other` when it lacks it.
    public string Form(PluralCategory category, double count)
    {
        if (_forms == null) return Other;
        if (count == 0 && _forms[(int)PluralCategory.Zero] is { } zero) return zero;
        return _forms[(int)category] ?? Other;
    }

    public static LocalisedText? Plural(JsonObject forms, string file, out string? problem)
    {
        var texts = new string?[6];
        foreach (var (name, value) in forms)
        {
            if (!Enum.TryParse(name, ignoreCase: true, out PluralCategory category) || value is not JsonValue v || !v.TryGetValue(out string? s))
            {
                problem = $"plural form '{name}' must be one of zero, one, two, few, many, other, and a string";
                return null;
            }
            texts[(int)category] = s;
        }
        if (texts[(int)PluralCategory.Other] == null)
        {
            problem = "plural forms need 'other'";
            return null;
        }
        problem = null;
        return new LocalisedText(texts, file);
    }
}

// Which plural form a count takes, per language: the CLDR rules (https://cldr.unicode.org, plurals.xml)
// for the languages games ship in, by primary subtag ("pt-PT" is the one region with a rule of its own).
// A language not listed takes English's (one for exactly 1, other otherwise). The operands are CLDR's:
// n the absolute count, i its integer digits, v how many fraction digits it shows and f those digits
// (a count is a double, so 1.5 has v 1 and f 5, and 2.0 is the integer 2).
internal static class PluralRules
{
    private static readonly string[] Categories = { "zero", "one", "two", "few", "many", "other" };

    public static bool IsPlural(JsonObject node)
    {
        bool other = false;
        foreach (var (name, _) in node)
        {
            if (Array.IndexOf(Categories, name.ToLowerInvariant()) < 0) return false;
            if (string.Equals(name, "other", StringComparison.OrdinalIgnoreCase)) other = true;
        }
        return other;
    }

    private readonly record struct Operands(double N, long I, int V, long F);

    private const PluralCategory Zero = PluralCategory.Zero, One = PluralCategory.One, Two = PluralCategory.Two,
                                 Few = PluralCategory.Few, Many = PluralCategory.Many, Other = PluralCategory.Other;

    private sealed record Rule(Func<Operands, PluralCategory> Select, PluralCategory[] Uses);

    private static readonly Rule English = new(o => o.I == 1 && o.V == 0 ? One : Other, new[] { One, Other });
    private static readonly Rule None = new(_ => Other, new[] { Other });
    private static readonly Rule ExactlyOne = new(o => o.N == 1 ? One : Other, new[] { One, Other });
    private static readonly Rule ZeroOrOne = new(o => o.I == 0 || o.N == 1 ? One : Other, new[] { One, Other });
    // fr, es, it, pt, ca: "1 000 000 de", a many for whole millions.
    private static bool Millions(Operands o) => o.V == 0 && o.I != 0 && o.I % 1000000 == 0;
    private static readonly Rule Romance = new(o => o.I == 1 && o.V == 0 ? One : Millions(o) ? Many : Other, new[] { One, Many, Other });
    private static readonly Rule Spanish = new(o => o.N == 1 ? One : Millions(o) ? Many : Other, new[] { One, Many, Other });
    private static readonly Rule French = new(o => o.I is 0 or 1 ? One : Millions(o) ? Many : Other, new[] { One, Many, Other });
    private static readonly Rule Portuguese = new(o => o.I is 0 or 1 ? One : Millions(o) ? Many : Other, new[] { One, Many, Other });
    private static readonly Rule EastSlavic = new(o =>
        o.V != 0 ? Other
        : o.I % 10 == 1 && o.I % 100 != 11 ? One
        : o.I % 10 is >= 2 and <= 4 && o.I % 100 is not (>= 12 and <= 14) ? Few
        : Many, new[] { One, Few, Many, Other });
    private static readonly Rule Polish = new(o =>
        o.V != 0 ? Other
        : o.I == 1 ? One
        : o.I % 10 is >= 2 and <= 4 && o.I % 100 is not (>= 12 and <= 14) ? Few
        : Many, new[] { One, Few, Many, Other });
    private static readonly Rule Czech = new(o =>
        o.V != 0 ? Many : o.I == 1 ? One : o.I is >= 2 and <= 4 ? Few : Other, new[] { One, Few, Many, Other });
    private static readonly Rule SouthSlavic = new(o =>
    {
        long x = o.V == 0 ? o.I : o.F;
        return x % 10 == 1 && x % 100 != 11 ? One
             : x % 10 is >= 2 and <= 4 && x % 100 is not (>= 12 and <= 14) ? Few
             : Other;
    }, new[] { One, Few, Other });
    private static readonly Rule Slovenian = new(o =>
        o.V != 0 ? Few : (o.I % 100) switch { 1 => One, 2 => Two, 3 or 4 => Few, _ => Other }, new[] { One, Two, Few, Other });
    private static readonly Rule Lithuanian = new(o =>
        o.F != 0 ? Many
        : o.I % 10 == 1 && o.I % 100 is not (>= 11 and <= 19) ? One
        : o.I % 10 >= 2 && o.I % 100 is not (>= 11 and <= 19) ? Few
        : Other, new[] { One, Few, Many, Other });
    private static readonly Rule Latvian = new(o =>
        o.V == 0 && (o.I % 10 == 0 || o.I % 100 is >= 11 and <= 19) ? Zero
        : o.V == 0 ? (o.I % 10 == 1 && o.I % 100 != 11 ? One : Other)
        : o.F % 10 == 1 && o.F % 100 != 11 ? One : Other, new[] { Zero, One, Other });
    private static readonly Rule Romanian = new(o =>
        o.I == 1 && o.V == 0 ? One
        : o.V != 0 || o.N == 0 || o.I % 100 is >= 1 and <= 19 ? Few
        : Other, new[] { One, Few, Other });
    private static readonly Rule Arabic = new(o =>
        o.V != 0 ? Other
        : o.I switch { 0 => Zero, 1 => One, 2 => Two, _ => (o.I % 100) switch { >= 3 and <= 10 => Few, >= 11 and <= 99 => Many, _ => Other } },
        new[] { Zero, One, Two, Few, Many, Other });
    private static readonly Rule Hebrew = new(o =>
        o.I == 1 && o.V == 0 || o.I == 0 && o.V != 0 ? One : o.I == 2 && o.V == 0 ? Two : Other, new[] { One, Two, Other });
    private static readonly Rule Irish = new(o =>
        o.V != 0 ? Other : o.I switch { 1 => One, 2 => Two, >= 3 and <= 6 => Few, >= 7 and <= 10 => Many, _ => Other },
        new[] { One, Two, Few, Many, Other });
    private static readonly Rule Welsh = new(o =>
        o.V != 0 ? Other : o.I switch { 0 => Zero, 1 => One, 2 => Two, 3 => Few, 6 => Many, _ => Other },
        new[] { Zero, One, Two, Few, Many, Other });
    private static readonly Rule Icelandic = new(o =>
        o.V == 0 ? (o.I % 10 == 1 && o.I % 100 != 11 ? One : Other) : One, new[] { One, Other });

    private static readonly Dictionary<string, Rule> Rules = Build();

    private static Dictionary<string, Rule> Build()
    {
        var rules = new Dictionary<string, Rule>(StringComparer.Ordinal);
        void Add(Rule rule, params string[] languages) { foreach (var l in languages) rules[l] = rule; }
        Add(English, "en", "de", "nl", "sv", "nb", "nn", "no", "da", "fi", "et", "el", "bg", "hu", "eo", "af", "sq", "gl", "ur", "sw");
        Add(Romance, "it", "ca", "pt-pt");
        Add(Spanish, "es");
        Add(French, "fr");
        Add(Portuguese, "pt");
        Add(ExactlyOne, "tr", "az", "ka", "kk", "ky", "mn", "uz", "eu", "ta", "te", "ml", "ne", "ps");
        Add(ZeroOrOne, "hi", "bn", "fa", "gu", "kn", "zu", "am", "as");
        Add(None, "ja", "ko", "zh", "yue", "th", "vi", "id", "ms", "lo", "my", "km", "jv", "bo");
        Add(EastSlavic, "ru", "uk", "be");
        Add(Polish, "pl");
        Add(Czech, "cs", "sk");
        Add(SouthSlavic, "hr", "sr", "bs", "mk");
        Add(Slovenian, "sl");
        Add(Lithuanian, "lt");
        Add(Latvian, "lv");
        Add(Romanian, "ro", "mo");
        Add(Arabic, "ar");
        Add(Hebrew, "he", "iw");
        Add(Irish, "ga");
        Add(Welsh, "cy");
        Add(Icelandic, "is");
        return rules;
    }

    public static PluralCategory Select(string language, double count) => RuleOf(language).Select(Operate(count));

    // The categories a language's counts fall in: the forms a plural text in it should have.
    public static IReadOnlyList<PluralCategory> Uses(string language) => RuleOf(language).Uses;

    private static Rule RuleOf(string language)
    {
        string code = language.Replace('_', '-').ToLowerInvariant();
        if (Rules.TryGetValue(code, out var rule)) return rule;
        return Rules.TryGetValue(Primary(code), out rule) ? rule : English;
    }

    private static Operands Operate(double count)
    {
        double n = Math.Abs(count);
        if (double.IsNaN(n) || double.IsInfinity(n)) return new Operands(0, 0, 0, 0);
        long i = n >= long.MaxValue ? long.MaxValue : (long)Math.Floor(n);
        if (n == Math.Floor(n)) return new Operands(n, i, 0, 0);
        // The fraction as it would be written: the shortest digits that round-trip.
        string text = n.ToString("R", CultureInfo.InvariantCulture);
        int dot = text.IndexOf('.');
        if (dot < 0 || text.IndexOf('E') >= 0) return new Operands(n, i, 1, 0);
        string digits = text[(dot + 1)..];
        long f = digits.Length <= 18 ? long.Parse(digits, CultureInfo.InvariantCulture) : 0;
        return new Operands(n, i, digits.Length, f);
    }

    private static string Primary(string language)
    {
        int dash = language.IndexOfAny(new[] { '-', '_' });
        return (dash < 0 ? language : language[..dash]).ToLowerInvariant();
    }
}

// Values for {placeholders}, by name, without allocating where the caller does not.
internal interface IPlaceholderValues
{
    // Appends the value of `name`: as it is (invariant) when `format` is empty, else formatted with it in `culture`.
    bool TryAppend(ReadOnlySpan<char> name, ReadOnlySpan<char> format, IFormatProvider culture, StringBuilder builder);
    bool TryGetNumber(string name, out double value);
}

internal readonly struct ArrayArgs : IPlaceholderValues
{
    private readonly (string Name, object? Value)[] _args;

    public ArrayArgs((string Name, object? Value)[] args) { _args = args ?? Array.Empty<(string, object?)>(); }

    public bool TryAppend(ReadOnlySpan<char> name, ReadOnlySpan<char> format, IFormatProvider culture, StringBuilder builder)
    {
        foreach (var (key, value) in _args)
            if (name.Equals(key, StringComparison.Ordinal))
            {
                builder.Append(value is IFormattable f
                    ? format.IsEmpty ? f.ToString(null, CultureInfo.InvariantCulture) : Formatted(f, format, culture)
                    : value?.ToString());
                return true;
            }
        return false;
    }

    // A value written with a format string in a language; a format the value does not take shows it as it is.
    internal static string Formatted(IFormattable value, ReadOnlySpan<char> format, IFormatProvider culture)
    {
        try { return value.ToString(format.ToString(), culture); }
        catch (FormatException) { return value.ToString(null, CultureInfo.InvariantCulture); }
    }

    public bool TryGetNumber(string name, out double value)
    {
        foreach (var (key, v) in _args)
            if (key == name && v is IConvertible c && v is not string)
            {
                value = c.ToDouble(CultureInfo.InvariantCulture);
                return true;
            }
        value = 0;
        return false;
    }
}
