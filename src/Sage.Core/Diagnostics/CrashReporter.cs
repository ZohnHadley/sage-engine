#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Sage.Core;

// Crash reports (docs/design/02-core-services-and-logging.md §4.1). Written on Log.Fatal,
// Assert.Check failures and unhandled exceptions to <user>/logs/crash-YYYYMMDD-HHMMSS.txt.
// Never throws: each section is written independently, so one failing section still leaves the rest.
public static class CrashReporter
{
    private static readonly object Lock = new();
    private static bool _installed;

    // The current app's last report (AppEnvironment.Current, issue #49).
    public static string? LastReportPath => AppEnvironment.Current.LastCrashReport;

    // Extra sections from other systems: "CVars" (host), "GPU" (client), later "Modules"/"Mods". They
    // belong to the current app: a crash in it reports its own.
    public static void AddSection(string name, Func<string> provider) => AppEnvironment.Current.AddCrashSection(name, provider);

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

    // Written for the current app: its sections, its log's tail, into its user folder.
    public static string? Write(Exception? exception, string reason)
    {
        var app = AppEnvironment.Current;
        try
        {
            app.Logger.Flush(TimeSpan.FromSeconds(2));   // so the log tail below includes everything up to now
            var sb = new StringBuilder(16 * 1024);
            Section(sb, "Summary", () =>
                $"Reason: {reason}\nTime:   {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
            if (exception != null)
                Section(sb, "Exception", exception.ToString);
            Section(sb, "Build", () =>
                $"Engine:  {BuildInfo.EngineVersion}\nConfig:  {BuildInfo.Config}\nGame id: {app.GameId}\n" +
                $"Runtime: {RuntimeInformation.FrameworkDescription}");
            Section(sb, "System", SystemInfo);

            foreach (var (name, provider) in app.CrashSections())
                Section(sb, name, provider);

            Section(sb, "Log tail", () => LogTail(app.Logger));

            string dir = Path.Combine(app.UserRoot, "logs");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(dir, $"crash-{stamp}.txt");
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(dir, $"crash-{stamp}-{n}.txt");
            File.WriteAllText(path, sb.ToString());
            app.LastCrashReport = path;

            try { System.Console.Error.WriteLine($"Crash report written to {path}"); } catch { }
            app.Logger.Enqueue(LogCat.Core, LogLevel.Error, $"Crash report written to {path}", default, default, default, null, 0);
            app.Logger.Flush(TimeSpan.FromSeconds(2));
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

    private static string LogTail(Logger logger)
    {
        var entries = new List<LogEntry>(logger.Ring.Capacity);
        logger.Ring.Snapshot(entries);
        var sb = new StringBuilder(entries.Count * 100);
        foreach (var e in entries)
            sb.Append(LogFormatter.Format(e)).Append('\n');
        return sb.Length == 0 ? "(empty)" : sb.ToString();
    }
}
