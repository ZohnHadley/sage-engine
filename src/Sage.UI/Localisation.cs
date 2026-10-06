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

    // Moves whenever the tables or the language change: what shows text re-resolves it then.
    public int Version { get; private set; }

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
        if (!IsKey(text)) return Literal(text);
        return Find(text) is { } entry ? entry.Other : Missing(text);
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
        Fill(builder, template, ref args);
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

    // {name} → its value; {{ and }} → a brace; an unfilled or unclosed placeholder stays as written.
    private static void Fill<TArgs>(StringBuilder builder, string template, ref TArgs args) where TArgs : IPlaceholderValues
    {
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c) { builder.Append(c); i++; continue; }
            if (c != '{') { builder.Append(c); continue; }
            int close = template.IndexOf('}', i + 1);
            if (close < 0) { builder.Append(template, i, template.Length - i); return; }
            var name = template.AsSpan(i + 1, close - i - 1);
            if (!args.TryAppend(name, builder)) builder.Append(template, i, close - i + 1);
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
        Languages = Available(vfs);
        _texts = texts;
        _fallback = language == DefaultLanguage ? new Dictionary<string, LocalisedText>(StringComparer.Ordinal) : english;
        _warned.Clear();
        Version++;
    }

    // The languages some mount has strings for (`strings/<lang>/`), English always among them, in order:
    // what an options screen offers for `lang` (issue #339). Read at each Load.
    public IReadOnlyList<string> Languages { get; private set; } = new[] { DefaultLanguage };

    private static string[] Available(VirtualFileSystem vfs)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { DefaultLanguage };
        foreach (var (path, _) in vfs.Enumerate(VirtualPath.Parse("strings"), "*.json", recursive: true))
        {
            var parts = path.Value.Split('/');
            if (parts.Length >= 3) found.Add(parts[1]);
        }
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

// Which plural form a count takes, per language. English's rule (one for exactly 1, other otherwise)
// is the default for a language not listed; add a language's rule here when its strings arrive.
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

    public static PluralCategory Select(string language, double count)
    {
        double n = Math.Abs(count);
        bool whole = n == Math.Floor(n);
        switch (Primary(language))
        {
            case "fr":   // 0 and 1 are singular
                return n < 2 ? PluralCategory.One : PluralCategory.Other;
            case "ja": case "ko": case "zh":   // no plural
                return PluralCategory.Other;
            default:     // en, de, nl, es, it, sv...: exactly one
                return whole && n == 1 ? PluralCategory.One : PluralCategory.Other;
        }
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
    bool TryAppend(ReadOnlySpan<char> name, StringBuilder builder);
    bool TryGetNumber(string name, out double value);
}

internal readonly struct ArrayArgs : IPlaceholderValues
{
    private readonly (string Name, object? Value)[] _args;

    public ArrayArgs((string Name, object? Value)[] args) { _args = args ?? Array.Empty<(string, object?)>(); }

    public bool TryAppend(ReadOnlySpan<char> name, StringBuilder builder)
    {
        foreach (var (key, value) in _args)
            if (name.Equals(key, StringComparison.Ordinal))
            {
                builder.Append(value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString());
                return true;
            }
        return false;
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
