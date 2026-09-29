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
    private static readonly object RegistryLock = new();
    private static readonly List<LogCat> Registry = new();
    private static LogLevel _defaultLevel = LogLevel.Info;

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

    private volatile int _minLevel;
    private bool _explicit;

    public string Name { get; }

    // Games and mods define their own categories the same way:
    //   public static readonly LogCat Quests = new("Quests");
    public LogCat(string name)
    {
        Name = name;
        lock (RegistryLock)
        {
            _minLevel = (int)_defaultLevel;
            Registry.Add(this);
        }
    }

    public LogLevel MinLevel
    {
        get => (LogLevel)_minLevel;
        set { lock (RegistryLock) { _minLevel = (int)value; _explicit = true; } }
    }

    // True when MinLevel was set explicitly (log_level) rather than following the default.
    public bool IsOverridden { get { lock (RegistryLock) return _explicit; } }

    public bool IsEnabled(LogLevel level) => (int)level >= _minLevel;

    // Makes this category follow the default level again.
    public void ResetToDefault()
    {
        lock (RegistryLock) { _explicit = false; _minLevel = (int)_defaultLevel; }
    }

    public override string ToString() => Name;

    public static LogLevel DefaultLevel
    {
        get { lock (RegistryLock) return _defaultLevel; }
        set
        {
            lock (RegistryLock)
            {
                _defaultLevel = value;
                foreach (var cat in Registry)
                    if (!cat._explicit)
                        cat._minLevel = (int)value;
            }
        }
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
