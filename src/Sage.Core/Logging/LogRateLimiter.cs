#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace sage_engine;

// Spam control for Log.Once / Log.Every (docs/design/02 §4).
internal static class LogRateLimiter
{
    private sealed class EveryState { public long LastTimestamp; public int Suppressed; }

    private static readonly ConcurrentDictionary<(LogCat, string), byte> OnceKeys = new();
    private static readonly ConcurrentDictionary<(LogCat, string), EveryState> EveryKeys = new();

    public static bool TryOnce(LogCat cat, string key) => OnceKeys.TryAdd((cat, key), 0);

    public static bool TryEvery(LogCat cat, string key, TimeSpan interval, out int suppressedSinceLast)
    {
        long now = Stopwatch.GetTimestamp();
        var state = EveryKeys.GetOrAdd((cat, key), _ => new EveryState { LastTimestamp = long.MinValue });
        lock (state)
        {
            if (state.LastTimestamp != long.MinValue &&
                Stopwatch.GetElapsedTime(state.LastTimestamp, now) < interval)
            {
                state.Suppressed++;
                suppressedSinceLast = 0;
                return false;
            }
            suppressedSinceLast = state.Suppressed;
            state.Suppressed = 0;
            state.LastTimestamp = now;
            return true;
        }
    }

    // Tests only.
    internal static void Reset()
    {
        OnceKeys.Clear();
        EveryKeys.Clear();
    }
}
