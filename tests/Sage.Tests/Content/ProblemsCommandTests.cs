#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The in-game problems list (issue #301, REDESIGN §4.2): the `problems` command and the list behind the
// dev overlay's badge, with no editor.
public class ProblemsCommandTests
{
    public ProblemsCommandTests() { _ = TestEnv.UserRoot; }

    private static string Repo => TestEnv.FolderAbove("Sage.sln");

    private const string Broken = """
        [
          { "type": "prefab", "id": "post", "components": { "transform": {} } },
          { "type": "placements", "id": "broken", "place": [ { "prefab": "post", "at": "nowhere" } ] }
        ]
        """;

    [Fact]
    public void ProblemsListsTheLoadErrorsWithFileAndLine()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", Broken);
        fixture.Mount("game", "sandbox");
        using var engine = HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build().Engine;
        using var log = new CaptureSink();

        Assert.True(engine.CVars.Execute("problems"));
        var lines = ConsoleLines(log);
        Assert.Equal("1 error, 0 warnings:", lines[0]);
        Assert.Single(lines, l => l.StartsWith("  error game:data/yard.json:3: placements sandbox:broken: "));
    }

    [Fact]
    public void ACleanLoadSaysSo_AndModConflictsAreWarnings()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", """[{ "type": "prefab", "id": "post", "components": { "transform": {} } }]""");
        fixture.Mount("game", "sandbox");
        using (var engine = HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build().Engine)
        using (var log = new CaptureSink())
        {
            Assert.True(engine.CVars.Execute("problems"));
            Assert.Equal(new[] { "no problems" }, ConsoleLines(log));
        }

        fixture.Write("mods/one", "data/patch.json", """[{ "type": "prefab", "id": "sandbox:post", "patch": true, "name": "One" }]""");
        fixture.Write("mods/two", "data/patch.json", """[{ "type": "prefab", "id": "sandbox:post", "patch": true, "name": "Two" }]""");
        fixture.Mount("mods/one", "one");
        fixture.Mount("mods/two", "two");
        using (var engine = HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build().Engine)
        {
            var problems = ContentProblems.Build(engine.Records, engine.Vfs);
            var conflict = Assert.Single(problems);
            Assert.False(conflict.IsError);
            Assert.Equal("mods/two", conflict.File);
            Assert.Contains("one, two; two won", conflict.Message);
            Assert.Equal("0 errors, 1 warning", ContentProblems.Summary(0, 1));
        }
    }

    // Done criterion: `problems` lists the same entries `sage validate` reports.
    [Fact]
    public void ProblemsListsTheSameEntriesAsValidate()
    {
        var mod = TestEnv.NewTempDir();
        Directory.CreateDirectory(Path.Combine(mod, "data"));
        File.WriteAllText(Path.Combine(mod, "data", "mod.json"), """
            [
              { "type": "item", "id": "rock", "sound": "sandbox:fire_burst", "sheet": "sandbox:sword_flat" },
              { "type": "sprite_sheet", "id": "rock_flat", "texture": "textures/rock.png" }
            ]
            """);

        IReadOnlyList<ContentProblem> listed = new List<ContentProblem>();
        List<string> printed = new();
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = Path.Combine(Repo, "games", "Sandbox"),
            EngineContentDirectory = Path.Combine(Repo, "engine_content"),
            AvailablePlugins = BasePlugins.All(),
            GameModule = new Sandbox.SandboxModule(),
            Mounts = new[] { (mod, "rockmod") },
            Inspect = engine =>
            {
                listed = ContentProblems.Build(engine.Records, engine.Vfs);
                using var log = new CaptureSink();
                engine.CVars.Execute("problems");
                printed = ConsoleLines(log);
            },
        });

        Assert.False(report.Ok);
        Assert.Equal(report.Errors.Count, listed.Count(p => p.IsError));
        Assert.Equal(report.Errors, listed.Where(p => p.IsError).Select(p => "Records: " + p.File + ":" + p.Line + ": " + p.Message)
            .Select(Strip).ToList(), new ErrorComparer());
        Assert.Equal($"{report.Errors.Count} errors, 0 warnings:", printed[0]);
        Assert.Equal(listed.Count + 1, printed.Count);
        Assert.All(listed.Where(p => p.IsError), p => Assert.True(p.Line > 0 && p.File.EndsWith(":data/mod.json", System.StringComparison.Ordinal) || p.File.EndsWith("data/mod.json", System.StringComparison.Ordinal)));
    }

    // The sink sees every test's log, and tests run in parallel (another test's console lines, an editor's
    // status, can land in it): keep this thread's, which is where the command ran.
    private static List<string> ConsoleLines(CaptureSink log)
    {
        int thread = System.Environment.CurrentManagedThreadId;
        return log.Entries.Where(e => e.Category == LogCat.Console && e.ThreadId == thread).Select(e => e.Message).ToList();
    }

    // validate's line is "Category: file:line:col: message"; the list has file and line apart, so compare
    // without the column.
    private static string Strip(string s) => s;

    private sealed class ErrorComparer : IEqualityComparer<string>
    {
        public bool Equals(string? validate, string? problem) => validate != null && problem != null && Norm(validate) == Norm(problem);
        public int GetHashCode(string s) => Norm(s).GetHashCode();
        private static string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"^(Records: [^:]+:[^:]+:\d+)(:\d+)?: ", "$1: ");
    }
}
