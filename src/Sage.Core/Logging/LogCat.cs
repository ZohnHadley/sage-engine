#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace Sage.Core;

public enum LogLevel { Trace, Debug, Info, Warn, Error, Fatal }

// A log category (docs/design/02-core-services-and-logging.md §3.2), like Unreal log categories or
// Source spew groups. Each category has its own minimum level, changeable at runtime with
// `log_level <cat> <level>`. Categories that were never set explicitly follow the default level,
// which the `developer` cvar switches between Info and Debug.
public sealed class LogCat
{
    // Declared before the category fields below: static initializers run in textual order.
    // The registry of names is the process's (a category is a name, the same in every app); the levels
    // are each app's (Logger, issue #49).
    private static readonly object RegistryLock = new();
    private static readonly List<LogCat> Registry = new();

    public static readonly LogCat Core = new("Core");
    public static readonly LogCat Host = new("Host");
    public static readonly LogCat Modules = new("Modules");
    public static readonly LogCat VFS = new("VFS");
    public static readonly LogCat Assets = new("Assets");
    public static readonly LogCat Records = new("Records");
    public static readonly LogCat Shaders = new("Shaders");
    public static readonly LogCat Render = new("Render");
    public static readonly LogCat Input = new("Input");
    public static readonly LogCat World = new("World");
    public static readonly LogCat Events = new("Events");
    public static readonly LogCat Physics = new("Physics");
    public static readonly LogCat Audio = new("Audio");
    public static readonly LogCat Animation = new("Animation");
    public static readonly LogCat UI = new("UI");
    public static readonly LogCat Streaming = new("Streaming");
    public static readonly LogCat Level = new("Level");        // .map import and brush geometry (15 §3)
    public static readonly LogCat Save = new("Save");
    public static readonly LogCat AI = new("AI");
    public static readonly LogCat Gameplay = new("Gameplay");
    public static readonly LogCat Editor = new("Editor");
    public static readonly LogCat Mods = new("Mods");
    public static readonly LogCat Console = new("Console");   // console echo and command output

    public string Name { get; }

    // Where this category's level sits in each Logger's table.
    internal int Index { get; }

    // Games and mods define their own categories the same way:
    //   public static readonly LogCat Quests = new("Quests");
    public LogCat(string name)
    {
        Name = name;
        lock (RegistryLock)
        {
            Index = Registry.Count;
            Registry.Add(this);
        }
    }

    // The level, the override flag and the default below are the current app's
    // (AppEnvironment.Current.Logger): `log_level` in one app leaves another's alone.
    public LogLevel MinLevel
    {
        get => Logger.Current.GetLevel(this);
        set => Logger.Current.SetLevel(this, value);
    }

    // True when MinLevel was set explicitly (log_level) rather than following the default.
    public bool IsOverridden => Logger.Current.IsOverridden(this);

    public bool IsEnabled(LogLevel level) => Logger.Current.IsEnabled(this, level);

    // Makes this category follow the default level again.
    public void ResetToDefault() => Logger.Current.ResetLevel(this);

    public override string ToString() => Name;

    public static LogLevel DefaultLevel
    {
        get => Logger.Current.DefaultLevel;
        set => Logger.Current.DefaultLevel = value;
    }

    public static IReadOnlyList<LogCat> All
    {
        get { lock (RegistryLock) return Registry.ToArray(); }
    }

    public static LogCat? Find(string name)
    {
        lock (RegistryLock)
        {
            foreach (var cat in Registry)
                if (string.Equals(cat.Name, name, StringComparison.OrdinalIgnoreCase))
                    return cat;
        }
        return null;
    }
}

public readonly struct LogField
{
    public readonly string? Key;
    public readonly object? Value;

    // Note: Value is boxed, which allocates. Use fields on Warn and above or dev-only paths,
    // not on per-frame Info logs (docs/design/02 §4).
    public LogField(string key, object? value) { Key = key; Value = value; }

    public bool IsEmpty => Key is null;
}

public readonly record struct LogEntry(
    DateTime TimestampUtc,
    double SessionSeconds,
    long Frame,
    long Tick,
    int ThreadId,
    string? ThreadName,
    LogCat Category,
    LogLevel Level,
    string Message,
    LogField Field1,
    LogField Field2,
    LogField Field3,
    string? File,
    int Line);
