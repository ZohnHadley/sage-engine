#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace sage_engine;

// Asserts (docs/design/02-core-services-and-logging.md §4).
//   Assert.Dev(cond, msg)     dev builds only (compiled out of Shipping); logs + breaks into the debugger
//   Assert.Ensure(cond, msg)  all builds; logs once per call site, returns cond so the caller can recover
//   Assert.Check(cond, msg)   all builds; fatal: log, crash report, SageFatalException
//
// Note for test code using xUnit: inside `namespace sage_engine...`, `Assert` resolves to this class.
// Add `using Assert = Xunit.Assert;` after the file's namespace declaration.
public static class Assert
{
    private static readonly ConcurrentDictionary<(string, int), byte> EnsuredSites = new();

    [Conditional("SAGE_DEV")]
    public static void Dev(bool condition, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!condition)
            Fail("Assert.Dev", message, file, line);
    }

    public static bool Ensure(bool condition, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!condition && EnsuredSites.TryAdd((file, line), 0))
            Fail("Ensure", message, file, line);
        return condition;
    }

    public static void Check(bool condition, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!condition)
            Log.Fatal(LogCat.Core, $"Check failed: {message}\n{new StackTrace(1, true)}", file: file, line: line);
    }

    private static void Fail(string kind, string message, string file, int line)
    {
        Log.Error(LogCat.Core, $"{kind} failed: {message}\n{new StackTrace(2, true)}", file: file, line: line);
        if (BuildInfo.IsDevBuild && Debugger.IsAttached)
            Debugger.Break();
    }

    // Tests only.
    internal static void ResetEnsureSites() => EnsuredSites.Clear();
}
