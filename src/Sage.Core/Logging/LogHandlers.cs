#nullable enable
using System;
using System.Runtime.CompilerServices;

namespace Sage.Core;

// Interpolated string handlers (docs/design/02 §4). When the category/level is disabled, the
// handler's constructor sets shouldAppend = false and C# skips every Append call, so a disabled
// `Log.Debug(cat, $"...{x}...")` formats nothing and allocates nothing.

// Compile-time level markers, so one generic handler type serves every level.
public interface ILogLevelMarker { static abstract LogLevel Level { get; } }

public static class LogLevels
{
    public struct Trace : ILogLevelMarker { public static LogLevel Level => LogLevel.Trace; }
    public struct Debug : ILogLevelMarker { public static LogLevel Level => LogLevel.Debug; }
    public struct Info : ILogLevelMarker { public static LogLevel Level => LogLevel.Info; }
    public struct Warn : ILogLevelMarker { public static LogLevel Level => LogLevel.Warn; }
    public struct Error : ILogLevelMarker { public static LogLevel Level => LogLevel.Error; }
    public struct Fatal : ILogLevelMarker { public static LogLevel Level => LogLevel.Fatal; }
}

[InterpolatedStringHandler]
public ref struct LogHandler<TLevel> where TLevel : struct, ILogLevelMarker
{
    private DefaultInterpolatedStringHandler _inner;
    internal readonly bool Enabled;

    public LogHandler(int literalLength, int formattedCount, LogCat cat, out bool shouldAppend)
    {
        Enabled = cat.IsEnabled(TLevel.Level);
        shouldAppend = Enabled;
        _inner = Enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);
    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

    internal string ToStringAndClear() => _inner.ToStringAndClear();
}

// For Log.Write (level chosen at runtime) and the rate-limited Log.Once / Log.Every. The
// rate-limit check also happens in the constructor, so a suppressed message is never formatted.
[InterpolatedStringHandler]
public ref struct LogDynamicHandler
{
    private DefaultInterpolatedStringHandler _inner;
    internal readonly bool Enabled;
    internal readonly int Suppressed;   // Log.Every: messages skipped since the last one written

    public LogDynamicHandler(int literalLength, int formattedCount, LogCat cat, LogLevel level, out bool shouldAppend)
    {
        Enabled = cat.IsEnabled(level);
        Suppressed = 0;
        shouldAppend = Enabled;
        _inner = Enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public LogDynamicHandler(int literalLength, int formattedCount, LogCat cat, LogLevel level, string key, out bool shouldAppend)
    {
        Enabled = cat.IsEnabled(level) && LogRateLimiter.TryOnce(cat, key);
        Suppressed = 0;
        shouldAppend = Enabled;
        _inner = Enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public LogDynamicHandler(int literalLength, int formattedCount, LogCat cat, LogLevel level, string key, TimeSpan interval, out bool shouldAppend)
    {
        Suppressed = 0;
        Enabled = cat.IsEnabled(level) && LogRateLimiter.TryEvery(cat, key, interval, out Suppressed);
        shouldAppend = Enabled;
        _inner = Enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);
    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

    internal string ToStringAndClear() => _inner.ToStringAndClear();
}
