#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// An entity reference and a list: a component a save serialises on the tick (SaveCapture).
[Component("test:writer_link")]
public struct WriterLink : IComponent
{
    public Entity Target;
    public List<int> Marks;
}

// Saves, issue #285: compressed world files, a thumbnail the client hands over, titles and configurable
// slot names, quick-save keys heard while paused, a save written in the background, and the save-version
// report. (The 10k-entity measurement is SaveWriterMeasurements, below.)
public class SaveWriterTests
{
    public SaveWriterTests() { _ = TestEnv.UserRoot; }

    internal const string Content = """
    [
      { "type": "prefab", "id": "crate", "name": "crate", "components": { "test:quick_count": { "value": 1 } } },
      { "type": "scene", "id": "main", "place": [ { "prefab": "crate", "at": [1, 0, 0], "name": "crate" } ] }
    ]
    """;

    internal const float Dt = 1f / 60f;

    internal static HeadlessApp Boot()
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", Content);
        files.Mount("game", "game");
        var app = HeadlessApp.Gameplay().Mount(files).StartScene("game:main").Boot();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        app.World.RunFixed(Dt);
        return app;
    }

    private static ref QuickCount Count(World world) =>
        ref world.Get<QuickCount>(Assert.Single(world.QueryAll().Entities.ToEntityList().Where(e => e.Name == "crate").ToArray()));

    private static string Folder(SaveSystem saves, string slot) => Path.Combine(saves.Root, slot);

    // A save written in the background is the state of the tick it was taken on, and the same file a save
    // written there and then makes (issue #285, SaveCapture): the tick copies the entities' columns, and what
    // needs the world (an entity reference, a list the game may change) is serialised on the tick; the
    // writer builds the rest. What the game does after the snapshot — new values, a new name, a list grown,
    // a reference moved, an entity destroyed — is not in it.
    [Xunit.Fact]
    public void ABackgroundSaveIsTheTickItWasTakenOn()
    {
        using var app = Boot();
        var world = app.World;
        var saves = app.Engine.Saves;
        var crate = new RecordId("game", "crate");
        var crates = Enumerable.Range(0, 5).Select(i => world.Spawn(crate, new Vector3(i, 0, 2))).ToArray();
        for (int i = 0; i < crates.Length; i++) world.Get<QuickCount>(crates[i]).Value = 10 + i;
        world.Get<QuickCount>(crates[0]).Value = 1;   // as its prefab spawned it: left out of the diff
        var hero = world.Create(Transform.At(new Vector3(0, 0, 7)), "hero");
        world.Add(hero, new Persistent { Id = PersistentId.FromName("writer_hero") });
        world.Add(hero, new WriterLink { Target = crates[3], Marks = new List<int> { 4, 5 } });
        world.RunFixed(Dt);

        Assert.True(saves.Save("now"));   // written before it returns
        Assert.Equal(1, saves.LastTiming.SerialisedOnTick);   // the hero's link: it names an entity

        var gate = new ManualResetEventSlim(false);
        saves.WriterGate = gate;
        try
        {
            saves.QuickSave();
            Assert.True(saves.IsWriting);
            // After the snapshot: none of this is in the save.
            world.Get<QuickCount>(crates[1]).Value = 99;
            world.Get<QuickCount>(crates[0]).Value = 42;
            crates[2].Name = "renamed";
            world.Get<WriterLink>(hero).Marks.Add(6);
            world.Get<WriterLink>(hero).Target = crates[4];
            world.Get<Transform>(crates[3]).LocalPosition = new Vector3(50, 0, 50);
            world.Destroy(crates[4]);
            world.RunFixed(Dt);
        }
        finally
        {
            gate.Set();
            saves.WriterGate = null;
        }
        saves.WaitForWrites();

        static JsonNode Entities(SaveSystem saves, string slot) =>
            JsonNode.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(saves.Root, slot), "world_*"))))!["entities"]!;
        var background = Entities(saves, SaveSystem.QuickSlot);
        Assert.True(JsonNode.DeepEquals(Entities(saves, "now"), background),
                    $"between ticks: {Entities(saves, "now").ToJsonString()}\nin the background: {background.ToJsonString()}");
        var link = background.AsArray().Single(e => (string?)e!["id"] == PersistentId.FromName("writer_hero").ToString())!
            ["components"]!["test:writer_link"]!["data"]!;
        Assert.Equal(world.Get<Persistent>(crates[3]).Id.ToString(), (string?)link["Target"]);
        Assert.Equal("[4,5]", link["Marks"]!.ToJsonString());

        Assert.True(saves.Load(SaveSystem.QuickSlot));
        world.RunFixed(Dt);
        var loaded = world.QueryAll().Entities.ToEntityList().Where(e => e.Name == "crate" && world.Has<Sage.Simulation.FromPrefab>(e)
                                                                         && world.Get<Transform>(e).LocalPosition.Z == 2).ToArray();
        Assert.Equal(new[] { 1, 11, 12, 13, 14 }, loaded.Select(e => world.Get<QuickCount>(e).Value).OrderBy(v => v));
        var target = world.Get<WriterLink>(world.Resolve(PersistentId.FromName("writer_hero"))).Target;
        Assert.Equal(13, world.Get<QuickCount>(target).Value);
        Assert.Equal(new Vector3(3, 0, 2), world.Get<Transform>(target).LocalPosition);
    }

    // `save_compress 1`: the world files are gzip; the slot says so; it loads, lists its entities, and a save
    // written before (plain JSON) still loads with compression on.
    [Xunit.Fact]
    public void CompressedSavesRoundTripAndPlainOnesStillLoad()
    {
        using var app = Boot();
        var saves = app.Engine.Saves;
        Count(app.World).Value = 3;
        Assert.True(saves.Save("plain"));

        app.CVars.Execute("save_compress 1");
        Count(app.World).Value = 7;
        Assert.True(saves.Save("packed"));

        string folder = Folder(saves, "packed");
        string file = Assert.Single(Directory.GetFiles(folder, "world_*"));
        Assert.EndsWith(".json.gz", file, StringComparison.Ordinal);
        using (var gzip = new GZipStream(File.OpenRead(file), CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip))
            Assert.IsType<JsonArray>(JsonNode.Parse(reader.ReadToEnd())!["entities"]);
        Assert.Equal("gzip", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "header.json")))!["compression"]);
        Assert.True(saves.Slots.Single(s => s.Name == "packed").Compressed);
        Assert.False(saves.Slots.Single(s => s.Name == "plain").Compressed);
        Assert.True(saves.List().Single(s => s.Slot == "packed").Entities > 0);

        Count(app.World).Value = 0;
        Assert.True(saves.Load("packed"));
        Assert.Equal(7, Count(app.World).Value);
        Assert.True(saves.Load("plain"));
        Assert.Equal(3, Count(app.World).Value);
    }

    // The client's hook hands over the frame's pixels when the save is taken; the slot keeps them as a PNG
    // and the listing gives its path. No hook, or a hook that fails, is a save without one.
    [Xunit.Fact]
    public void TheSlotListingShowsTheThumbnailPath()
    {
        using var app = Boot();
        var saves = app.Engine.Saves;
        Assert.True(saves.Save("bare"));
        Assert.Null(saves.Slots.Single(s => s.Name == "bare").ThumbnailPath);

        // 4x2: red, green, blue, white / black, grey, translucent, yellow.
        byte[] rgba =
        {
            255, 0, 0, 255,   0, 255, 0, 255,   0, 0, 255, 255,   255, 255, 255, 255,
            0, 0, 0, 255,     128, 128, 128, 255,   10, 20, 30, 40,   255, 255, 0, 255,
        };
        int calls = 0;
        saves.Thumbnail = () => { calls++; return new SaveThumbnail(4, 2, rgba); };
        app.CVars.Execute("quicksave");   // in the background
        var slot = saves.Slots.Single(s => s.Name == SaveSystem.QuickSlot);
        Assert.Equal(1, calls);
        Assert.NotNull(slot.ThumbnailPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(Folder(saves, SaveSystem.QuickSlot)), SaveSystem.ThumbnailFile), slot.ThumbnailPath);
        var (width, height, pixels) = DecodePng(File.ReadAllBytes(slot.ThumbnailPath!));
        Assert.Equal((4, 2), (width, height));
        Assert.Equal(rgba, pixels);

        saves.Thumbnail = () => throw new InvalidOperationException("no device");
        Assert.True(saves.Save("failed_picture"));
        Assert.Null(saves.Slots.Single(s => s.Name == "failed_picture").ThumbnailPath);

        saves.Thumbnail = () => new SaveThumbnail(4, 4, new byte[3]);   // not 4 bytes a pixel
        Assert.True(saves.Save("bad_picture"));
        Assert.Null(saves.Slots.Single(s => s.Name == "bad_picture").ThumbnailPath);
    }

    // A thumbnail a client shrinks before it hands it over keeps its aspect.
    [Xunit.Fact]
    public void AThumbnailShrinksToAWidth()
    {
        var big = new SaveThumbnail(1280, 720, new byte[1280 * 720 * 4]);
        var small = big.Shrink(320);
        Assert.Equal((320, 180), (small.Width, small.Height));
        Assert.Equal(320 * 180 * 4, small.Rgba.Length);
        Assert.Same(small, small.Shrink(640));
    }

    // A title a menu shows (the folder keeps its name); the quick slot and the autosave prefix are cvars;
    // and a save the player named like an autosave is never overwritten by the rotation.
    [Xunit.Fact]
    public void SlotsHaveTitlesAndConfigurableNames()
    {
        using var app = Boot();
        var saves = app.Engine.Saves;
        Assert.True(saves.Save("slot3", SaveKind.Manual, "Before the dragon"));
        app.CVars.Execute("save slot4 At the gate");
        Assert.True(saves.Save("slot5"));
        Assert.Equal("Before the dragon", saves.Slots.Single(s => s.Name == "slot3").Title);
        Assert.Equal("At the gate", saves.Slots.Single(s => s.Name == "slot4").Title);
        Assert.Equal("slot5", saves.Slots.Single(s => s.Name == "slot5").Title);

        app.CVars.Execute("save_quick_slot hero_quick");
        Assert.Equal("hero_quick", saves.QuickSlotName);
        Count(app.World).Value = 4;
        saves.QuickSave();
        Assert.Equal(SaveKind.Quick, saves.Slots.Single(s => s.Name == "hero_quick").Kind);
        Count(app.World).Value = 9;
        saves.QuickLoad();
        Assert.Equal(4, Count(app.World).Value);

        app.CVars.Execute("save_autosave_prefix auto_");
        Assert.True(saves.Save("auto_2"));   // the player's own, under an autosave's name
        for (int i = 0; i < 4; i++) saves.Autosave();
        Assert.Equal(SaveKind.Manual, saves.Slots.Single(s => s.Name == "auto_2").Kind);
        var autos = saves.Slots.Where(s => s.Kind == SaveKind.Auto).Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(new[] { "auto_1", "auto_3", "auto_4" }, autos);
    }

    // F5 and F9 are heard while the world is paused: a paused world runs a held pass each real tick, and the
    // keys' system runs in it; the save and the load run at that pass's boundary. A slowed world's held pass
    // does not act on the same command twice.
    [Xunit.Fact]
    public void QuickSaveKeysWorkWhilePaused()
    {
        using var app = Boot();
        var world = app.World;
        var saves = app.Engine.Saves;
        var input = world.Resources.GetOrAdd(() => new PlayerInput());
        var quickSave = app.Engine.Actions.Get(SaveSystem.QuickSaveAction);
        var quickLoad = app.Engine.Actions.Get(SaveSystem.QuickLoadAction);

        void Press(ActionId action)
        {
            input.HasCommand = true;
            input.Command = new PlayerCommand { Tick = world.Tick + 1, Pressed = new ActionMask().With(action) };
            world.RunFixed(Dt);
            input.Command = default;
            input.HasCommand = false;
        }

        world.Paused = true;
        world.RunFixed(Dt);
        Count(world).Value = 5;
        Press(quickSave);
        Assert.True(saves.Exists(SaveSystem.QuickSlot));
        Count(world).Value = 8;
        Press(quickLoad);
        Assert.Equal(5, Count(world).Value);
        Assert.True(world.Paused);

        // Half speed: a step every other real tick, and a held pass between them with the step's command.
        world.Paused = false;
        Assert.True(saves.Delete(SaveSystem.QuickSlot));
        WorldTime.Of(world).Scale = 0.5;
        int saved;
        string slot = "half_" + Guid.NewGuid().ToString("N");   // the log is process-wide: this test's own line
        app.CVars.Execute($"save_quick_slot {slot}");
        using (var log = new CaptureSink())
        {
            input.HasCommand = true;
            input.Command = new PlayerCommand { Tick = world.Tick + 1, Pressed = new ActionMask().With(quickSave) };
            for (int i = 0; i < 4; i++) world.RunFixed(Dt);
            saves.WaitForWrites();
            saved = log.Entries.Count(e => e.Message.StartsWith($"Saved '{slot}'", StringComparison.Ordinal));
        }
        Assert.Equal(1, saved);
        Assert.True(saves.Exists(slot));
    }

    // Saves the game takes while it plays are snapshotted on the tick and written on another thread; a
    // load straight after waits for the write, and so does the listing.
    [Xunit.Fact]
    public void AQuickSaveIsWrittenOffTheTick()
    {
        using var app = Boot();
        var saves = app.Engine.Saves;
        var gate = new ManualResetEventSlim(false);
        saves.WriterGate = gate;
        try
        {
            Count(app.World).Value = 6;
            saves.QuickSave();
            Assert.True(saves.IsWriting);
            Assert.False(Directory.Exists(Folder(saves, SaveSystem.QuickSlot)));   // nothing written yet
            Count(app.World).Value = 2;                                              // after the snapshot
        }
        finally { gate.Set(); }
        Assert.True(saves.Exists(SaveSystem.QuickSlot));   // waits for it
        Assert.False(saves.IsWriting);
        Assert.True(saves.Load(SaveSystem.QuickSlot));
        Assert.Equal(6, Count(app.World).Value);           // the state when it was taken

        // `save_background 0`: written before QuickSave returns, gate or no gate.
        app.CVars.Execute("save_background 0");
        saves.WriterGate = new ManualResetEventSlim(false);
        Count(app.World).Value = 11;
        saves.QuickSave();
        Assert.False(saves.IsWriting);
        Count(app.World).Value = 0;
        Assert.True(saves.Load(SaveSystem.QuickSlot));
        Assert.Equal(11, Count(app.World).Value);
    }

    // The report reads a slot without loading it: the format, each component and resource id at its saved
    // version against this build's, and what a load does with it.
    [Xunit.Fact]
    public void TheSaveVersionReportSaysWhatALoadWouldUpgrade()
    {
        using var app = Boot();
        var saves = app.Engine.Saves;
        Assert.Null(saves.Report("missing"));
        Count(app.World).Value = 2;   // a crate as its prefab made it writes no count at all (a diff)
        Assert.True(saves.Save("now"));
        var report = saves.Report("now")!;
        Assert.True(report.IsCurrent, string.Join("\n", report.Lines()));
        Assert.True(report.CanLoad);
        Assert.Contains(report.Entries, e => e.Id == "test:quick_count" && e.Status == SaveVersionStatus.Current && e.Count == 1);

        // The same save as an older game, a newer one and another game wrote it.
        string file = Path.Combine(Folder(saves, "now"), "world_main.json");
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var crate = root["entities"]!.AsArray().Select(e => e!.AsObject()).First(e => e["components"]?["test:quick_count"] != null);
        var components = crate["components"]!.AsObject();
        components["test:quick_count"]!["version"] = 7;
        components["mod:mana"] = new JsonObject { ["version"] = 1, ["data"] = new JsonObject { ["value"] = 3 } };
        File.WriteAllText(file, root.ToJsonString());

        report = saves.Report("now")!;
        Assert.False(report.IsCurrent);
        var quick = Assert.Single(report.Entries, e => e.Id == "test:quick_count");
        Assert.Equal((7, 1, SaveVersionStatus.Newer), (quick.SavedVersion, quick.CurrentVersion!.Value, quick.Status));
        var mana = Assert.Single(report.Entries, e => e.Id == "mod:mana");
        Assert.Equal(SaveVersionStatus.Unknown, mana.Status);
        Assert.Null(mana.CurrentVersion);
        Assert.Contains("1 newer", report.Summary, StringComparison.Ordinal);
        Assert.Contains("1 unknown", report.Summary, StringComparison.Ordinal);
        Assert.Contains(report.Lines(), l => l.Contains("mod:mana", StringComparison.Ordinal) && l.Contains("kept as data", StringComparison.Ordinal));
        app.CVars.Execute("save_report now");
        app.CVars.Execute("save_report");
    }

    // A golden save from format 2 (component versions from before) reports a format upgrade, and a
    // component it has at an older version as an upgrade with its [Upgrade] steps.
    [Xunit.Fact]
    public void TheReportCountsUpgradeStepsForAnOlderComponent()
    {
        using var app = Boot();
        var saves = app.Engine.Saves;
        Assert.True(saves.Save("old"));
        string header = Path.Combine(Folder(saves, "old"), "header.json");
        var h = JsonNode.Parse(File.ReadAllText(header))!.AsObject();
        h["formatVersion"] = 3;
        File.WriteAllText(header, h.ToJsonString());
        string file = Path.Combine(Folder(saves, "old"), "world_main.json");
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        root.Remove("dormant");
        var first = root["entities"]!.AsArray().Select(e => e!.AsObject()).First(e => e["components"] is JsonObject);
        first["components"]!["test:chain"] = new JsonObject { ["version"] = 1, ["data"] = new JsonObject { ["First"] = 2 } };
        File.WriteAllText(file, root.ToJsonString());

        var report = saves.Report("old")!;
        Assert.True(report.FormatUpgrade);
        var entry = Assert.Single(report.Entries, e => e.Id == "test:chain");
        Assert.Equal(SaveVersionStatus.Upgrade, entry.Status);
        Assert.Equal((1, 3, 2), (entry.SavedVersion, entry.CurrentVersion!.Value, entry.UpgradeSteps));
        Assert.Contains("format 3 -> ", report.Summary, StringComparison.Ordinal);
    }

    // A PNG's size and pixels, read back: the chunks' CRCs checked, the IDAT inflated and unfiltered (the
    // writer uses filter 0 only).
    private static (int Width, int Height, byte[] Rgba) DecodePng(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        int at = 8, width = 0, height = 0;
        using var idat = new MemoryStream();
        while (at < png.Length)
        {
            int length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);
            uint crc = (uint)((png[at + 8 + length] << 24) | (png[at + 9 + length] << 16) | (png[at + 10 + length] << 8) | png[at + 11 + length]);
            Assert.Equal(Crc(png.AsSpan(at + 4, length + 4)), crc);
            if (type == "IHDR")
            {
                width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                Assert.Equal(8, data[8]);
                Assert.Equal(6, data[9]);
            }
            else if (type == "IDAT") idat.Write(data);
            at += 12 + length;
        }
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var rows = raw.ToArray();
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, rows[y * (width * 4 + 1)]);
            Array.Copy(rows, y * (width * 4 + 1) + 1, pixels, y * width * 4, width * 4);
        }
        return (width, height, pixels);
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}

// The issue's done criterion: a 10k-entity save does not stall a frame. What a frame pays is the snapshot
// on the tick, which copies component columns (SaveCapture); the write (building the entities' JSON,
// compressing, the files) runs on the writer's thread. Proved without an absolute wall-clock bound, which a shared CI runner would make flaky: the
// writer is held shut while the tick that asked for the save runs, so that tick provably did not wait for
// the write, and the save is complete and loads once the writer is let go; and the snapshot is compared
// with a whole save of the same world, not with a number of milliseconds. The split is logged.
[Xunit.Collection(MeasurementsCollection.Name)]
public class SaveWriterMeasurements
{
    public SaveWriterMeasurements() { _ = TestEnv.UserRoot; }

    private const int Entities = 10_000;

    private sealed class AskOnce : ISystem
    {
        private readonly Action _ask;
        public bool Armed;
        public AskOnce(Action ask) { _ask = ask; }
        public void Run(in SystemContext ctx)
        {
            if (!Armed) return;
            Armed = false;
            _ask();
        }
    }

    [Xunit.Fact]
    public void ATenThousandEntitySaveDoesNotStallAFrame()
    {
        using var app = SaveWriterTests.Boot();
        var world = app.World;
        var saves = app.Engine.Saves;
        app.CVars.Execute("save_compress 1");
        var crate = new RecordId("game", "crate");
        for (int i = 0; i < Entities; i++)
            world.Spawn(crate, new Vector3(i % 100, 0, i / 100));
        world.RunFixed(SaveWriterTests.Dt);

        // Once first, so what is measured is a save in a game that has saved before, not the serialiser's
        // first look at each type.
        Assert.True(saves.Save("warm"));
        Assert.True(saves.Save("warm"));   // the second: no first-call costs
        var warm = saves.LastTiming;
        Console.WriteLine($"a whole save, written on the tick: snapshot {warm.Snapshot.TotalMilliseconds:F1} ms, write {warm.Write.TotalMilliseconds:F1} ms");

        var ask = new AskOnce(() => saves.QuickSave());
        world.AddSystem(ask, Phase.Gameplay);

        // A full collection first, so the tick measured does not pay for the garbage earlier tests left
        // (on a CI runner the snapshot once came to a third of the write; a collection mid-snapshot is the
        // likely cause, and the message below says whether one ran).
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int gen2 = GC.CollectionCount(2);

        var gate = new ManualResetEventSlim(false);
        saves.WriterGate = gate;
        var tick = Stopwatch.StartNew();
        try
        {
            ask.Armed = true;
            world.RunFixed(SaveWriterTests.Dt);   // asks mid-tick; snapshotted at the tick's end
            tick.Stop();
            Assert.True(saves.IsWriting);         // the tick returned with the write still to do
            Assert.False(Directory.Exists(Path.Combine(saves.Root, SaveSystem.QuickSlot)));
        }
        finally
        {
            gate.Set();
            saves.WriterGate = null;
        }

        saves.WaitForWrites();
        var timing = saves.LastTiming;
        Assert.True(timing.Write > TimeSpan.Zero);
        // The tick built no JSON: it copied the crates' component columns (SaveCapture), and serialised
        // nothing, since nothing in a crate needs the world; the entities' trees, the diffs against the
        // prefab, compressing and writing were all the writer's. So what the tick paid is a small part of
        // what the save cost (about a thirtieth when measured; a fifth leaves a slow runner its headroom).
        Assert.Equal(0, timing.SerialisedOnTick);
        Assert.True(timing.Snapshot * 5 < timing.Write,
                    $"snapshot {timing.Snapshot.TotalMilliseconds:F1} ms, write {timing.Write.TotalMilliseconds:F1} ms; " +
                    $"gen-2 collections since the measured tick began: {GC.CollectionCount(2) - gen2}");
        var slot = saves.Slots.Single(s => s.Name == SaveSystem.QuickSlot);
        Assert.True(slot.Compressed);
        Assert.True(saves.List().Single(s => s.Slot == SaveSystem.QuickSlot).Entities > Entities);
        Console.WriteLine($"10k-entity save: tick {tick.Elapsed.TotalMilliseconds:F1} ms (snapshot {timing.Snapshot.TotalMilliseconds:F1} ms), " +
                          $"write {timing.Write.TotalMilliseconds:F1} ms on the writer");

        Assert.True(saves.Load(SaveSystem.QuickSlot));
        world.RunFixed(SaveWriterTests.Dt);
        Assert.True(world.QueryAll().Entities.ToEntityList().Count(e => e.Name == "crate") > Entities);
    }
}
