#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace sage_engine;

// Crash reports (docs/design/02-core-services-and-logging.md §4.1). Written on Log.Fatal,
// Assert.Check failures and unhandled exceptions to <user>/logs/crash-YYYYMMDD-HHMMSS.txt.
// Never throws: each section is written independently, so one failing section still leaves the rest.
public static class CrashReporter
{
    private static readonly object Lock = new();
    private static readonly List<(string Name, Func<string> Provider)> Sections = new();
    private static bool _installed;

    public static string? LastReportPath { get; private set; }

    // Extra sections from other systems: "CVars" (host), "GPU" (client), later "Modules"/"Mods".
    public static void AddSection(string name, Func<string> provider)
    {
        lock (Lock) Sections.Add((name, provider));
    }

    public static void Install()
    {
        lock (Lock)
        {
            if (_installed) return;
            _installed = true;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is SageFatalException { Reported: true })
                return;
            Write(e.ExceptionObject as Exception, "Unhandled exception");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Log.Error(LogCat.Core, $"Unobserved task exception: {e.Exception}");
    }

    public static string? Write(Exception? exception, string reason)
    {
        try
        {
            Log.Flush(TimeSpan.FromSeconds(2));   // so the log tail below includes everything up to now
            var sb = new StringBuilder(16 * 1024);
            Section(sb, "Summary", () =>
                $"Reason: {reason}\nTime:   {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
            if (exception != null)
                Section(sb, "Exception", exception.ToString);
            Section(sb, "Build", () =>
                $"Engine:  {BuildInfo.EngineVersion}\nConfig:  {BuildInfo.Config}\nGame id: {UserPaths.GameId}\n" +
                $"Runtime: {RuntimeInformation.FrameworkDescription}");
            Section(sb, "System", SystemInfo);

            (string, Func<string>)[] extra;
            lock (Lock) extra = Sections.ToArray();
            foreach (var (name, provider) in extra)
                Section(sb, name, provider);

            Section(sb, "Log tail", LogTail);

            string dir = UserPaths.Logs;
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(dir, $"crash-{stamp}.txt");
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(dir, $"crash-{stamp}-{n}.txt");
            File.WriteAllText(path, sb.ToString());
            LastReportPath = path;

            try { System.Console.Error.WriteLine($"Crash report written to {path}"); } catch { }
            Log.Error(LogCat.Core, $"Crash report written to {path}");
            Log.Flush(TimeSpan.FromSeconds(2));
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void Section(StringBuilder sb, string name, Func<string> provider)
    {
        sb.Append("==== ").Append(name).Append(" ====\n");
        try { sb.Append(provider()); }
        catch (Exception ex) { sb.Append("(section failed: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(')'); }
        sb.Append("\n\n");
    }

    private static string SystemInfo()
    {
        var gc = GC.GetGCMemoryInfo();
        return $"OS:        {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})\n" +
               $"CPUs:      {Environment.ProcessorCount}\n" +
               $"Managed:   {GC.GetTotalMemory(false) / (1024 * 1024)} MB (heap {gc.HeapSizeBytes / (1024 * 1024)} MB)\n" +
               $"Working:   {Environment.WorkingSet / (1024 * 1024)} MB\n" +
               $"GC counts: gen0 {GC.CollectionCount(0)}, gen1 {GC.CollectionCount(1)}, gen2 {GC.CollectionCount(2)}";
    }

    private static string LogTail()
    {
        var entries = new List<LogEntry>(Log.Ring.Capacity);
        Log.Ring.Snapshot(entries);
        var sb = new StringBuilder(entries.Count * 100);
        foreach (var e in entries)
            sb.Append(LogFormatter.Format(e)).Append('\n');
        return sb.Length == 0 ? "(empty)" : sb.ToString();
    }
}
