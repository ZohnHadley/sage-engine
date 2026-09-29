#nullable enable
using System;
using System.Globalization;

namespace sage_engine;

[Flags]
public enum CVarFlags
{
    None = 0,
    Archive = 1,    // saved to config.cfg on shutdown
    Cheat = 2,      // only changeable (from console, config or launch args) while sv_cheats is 1
    DevOnly = 4,    // exists only in dev builds (Debug/Development); not registered in Shipping
    ReadOnly = 8,   // shown, but can't be changed from the console
    // Replicated comes with multiplayer (docs/design/02 §12).
}

// A named, flagged runtime variable (docs/design/02-core-services-and-logging.md §4.2).
public abstract class CVar
{
    protected CVar(string name, string help, CVarFlags flags)
    {
        Name = name;
        Help = help;
        Flags = flags;
    }

    public string Name { get; }
    public string Help { get; }
    public CVarFlags Flags { get; }
    public abstract string TypeName { get; }
    public abstract string ValueString { get; }
    public abstract string DefaultString { get; }
    public abstract bool IsDefault { get; }

    // Raised on the thread that changed the value (the main thread for console input).
    public event Action<CVar>? Changed;

    public abstract bool TrySet(string text, out string? error);
    public abstract void Reset();

    protected void RaiseChanged() => Changed?.Invoke(this);

    public override string ToString() => $"{Name} = {ValueString}";
}

// T is bool, int, float, string or an enum.
public sealed class CVar<T> : CVar where T : notnull
{
    private T _value;
    private readonly T? _min;
    private readonly T? _max;
    private readonly bool _hasRange;

    internal CVar(string name, T defaultValue, CVarFlags flags, string help, bool hasRange = false, T? min = default, T? max = default)
        : base(name, help, flags)
    {
        if (!IsSupported)
            throw new NotSupportedException($"CVar type {typeof(T).Name} is not supported (bool, int, float, string, enums).");
        Default = defaultValue;
        _value = defaultValue;
        _hasRange = hasRange;
        _min = min;
        _max = max;
    }

    public T Default { get; }

    // Setting from code clamps to the range (with a warning) and raises Changed when the value changes.
    public T Value
    {
        get => _value;
        set
        {
            T v = value;
            if (_hasRange && !InRange(v))
            {
                v = Clamp(v);
                Log.Warn(LogCat.Console, $"{Name}: {Format(value)} is out of range [{Format(_min!)}..{Format(_max!)}]; clamped to {Format(v)}");
            }
            if (Equals(_value, v)) return;
            _value = v;
            RaiseChanged();
        }
    }

    public override string TypeName => typeof(T).IsEnum ? typeof(T).Name : typeof(T) == typeof(float) ? "float" : typeof(T).Name.ToLowerInvariant() switch
    {
        "boolean" => "bool",
        "int32" => "int",
        var other => other,
    };

    public override string ValueString => Format(_value);
    public override string DefaultString => Format(Default);
    public override bool IsDefault => Equals(_value, Default);

    public override void Reset() => Value = Default;

    public override bool TrySet(string text, out string? error)
    {
        if (!TryParse(text, out T parsed))
        {
            error = $"{Name}: '{text}' is not a valid {TypeName}";
            return false;
        }
        if (_hasRange && !InRange(parsed))
        {
            error = $"{Name}: {text} is out of range [{Format(_min!)}..{Format(_max!)}]";
            return false;
        }
        error = null;
        Value = parsed;
        return true;
    }

    private static bool IsSupported =>
        typeof(T) == typeof(bool) || typeof(T) == typeof(int) || typeof(T) == typeof(float) ||
        typeof(T) == typeof(string) || typeof(T).IsEnum;

    private bool InRange(T v) => Comparer(v, _min!) >= 0 && Comparer(v, _max!) <= 0;
    private T Clamp(T v) => Comparer(v, _min!) < 0 ? _min! : Comparer(v, _max!) > 0 ? _max! : v;
    private static int Comparer(T a, T b) => System.Collections.Generic.Comparer<T>.Default.Compare(a, b);

    private static string Format(T v) => v switch
    {
        bool b => b ? "1" : "0",
        float f => f.ToString("0.######", CultureInfo.InvariantCulture),
        IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static bool TryParse(string text, out T value)
    {
        text = text.Trim();
        object? result = null;
        if (typeof(T) == typeof(bool))
        {
            switch (text.ToLowerInvariant())
            {
                case "1": case "true": case "on": case "yes": result = true; break;
                case "0": case "false": case "off": case "no": result = false; break;
            }
        }
        else if (typeof(T) == typeof(int))
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) result = i;
        }
        else if (typeof(T) == typeof(float))
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)) result = f;
        }
        else if (typeof(T) == typeof(string))
        {
            result = text;
        }
        else if (typeof(T).IsEnum)
        {
            if (Enum.TryParse(typeof(T), text, ignoreCase: true, out object? e) && Enum.IsDefined(typeof(T), e!)) result = e;
        }

        if (result is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }
}
