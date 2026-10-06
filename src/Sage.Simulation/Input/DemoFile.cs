#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// A `.sagedemo` (docs/design/08 §7, issue #333): a recorded session, played back through PlayerInput.
//
//   "SAGEDEMO"  uint16 format
//   int32 length + UTF-8 JSON header      what made it: build, game, plugins, mods, actions, world, step,
//                                         and the world's tick count and time when it began
//   int32 count, then per file:           the save it starts from (header.json, world_*.json), embedded so
//     string name, int32 length, bytes    a demo is one file that can be passed around
//   then one record per simulated tick:
//     1  a command: float move x, y, view yaw, pitch; uint64 held, pressed, released (two words each)
//     2  the same command as the tick before (standing still is most of a demo)
//     3  no command that tick (nothing was sampled: a headless world, a world that had no player)
//   255 the end: int64 ticks, byte has-hash, uint64 world hash after the last tick
//
// A file without its end was cut short (a crash, a full disk): what is whole of it still plays, and the
// end's hash, which it does not have, is not checked.
internal static class DemoFormat
{
    public const string Extension = ".sagedemo";
    public const ushort Version = 1;
    public static ReadOnlySpan<byte> Magic => "SAGEDEMO"u8;

    public const byte Command = 1, Repeat = 2, NoCommand = 3, End = 255;

    // A file's start is a save; its header and world files are at most this big each (a guard against
    // reading a length out of a corrupt file and allocating it).
    public const int MaxEmbeddedFile = 512 * 1024 * 1024;
}

// What made a demo: playback is refused unless this build, game, plugins, mods and actions are the ones
// it was recorded with, because a command means nothing to a simulation that is not the same one.
internal sealed class DemoHeader
{
    public string Build = "";                  // engine version and build configuration
    public string Game = "";
    public List<SavedPlugin> Plugins = new();
    public List<SavedMod> Mods = new();
    public List<string> Actions = new();       // in registration order: a command's mask bits index it
    public string World = "";                  // the world the commands went to
    public string Scene = "";                  // the scene it was in when recording began (for people)
    public float Dt;                           // the step every command was simulated at
    public long Tick;                          // the world's tick count when recording began
    public double SimTime;                     // and its simulated seconds
    public string RecordedUtc = "";

    public static string CurrentBuild => $"{BuildInfo.EngineVersion} {BuildInfo.ConfigurationName}";

    public static DemoHeader Current(Engine engine, World world, float dt)
    {
        world.Resources.TryGet<ActiveScene>(out var scene);
        return new DemoHeader
        {
            Build = CurrentBuild,
            Game = UserPaths.GameId,
            Plugins = engine.Saves.CurrentPlugins().ToList(),
            Mods = engine.Saves.CurrentMods().ToList(),
            Actions = engine.Actions.All.Select(a => a.Name).ToList(),
            World = world.Name,
            Scene = scene is { Id.IsEmpty: false } ? scene.Id.ToString() : "",
            Dt = dt,
            Tick = world.Tick,
            SimTime = world.SimTime,
            RecordedUtc = DateTime.UtcNow.ToString("o"),
        };
    }

    // How this demo differs from what is running now, in words; empty when it can be played.
    public List<string> Mismatches(Engine engine)
    {
        var problems = new List<string>();
        if (Build != CurrentBuild) problems.Add($"recorded with build {Build}, this is {CurrentBuild}");
        if (!string.Equals(Game, UserPaths.GameId, StringComparison.OrdinalIgnoreCase))
            problems.Add($"recorded in game '{Game}', this is '{UserPaths.GameId}'");
        string Plugins(IEnumerable<SavedPlugin> list) => string.Join(", ", list.Select(p => $"{p.Id} {p.Version}"));
        string Mods(IEnumerable<SavedMod> list) => list.Any() ? string.Join(", ", list.Select(m => $"{m.Id} {m.Version}")) : "none";
        var plugins = engine.Saves.CurrentPlugins().ToList();
        if (Plugins(this.Plugins) != Plugins(plugins))
            problems.Add($"recorded with plugins [{Plugins(this.Plugins)}], these are [{Plugins(plugins)}]");
        var mods = engine.Saves.CurrentMods().ToList();
        if (Mods(this.Mods) != Mods(mods)) problems.Add($"recorded with mods [{Mods(this.Mods)}], these are [{Mods(mods)}]");
        var actions = engine.Actions.All.Select(a => a.Name).ToList();
        if (!Actions.SequenceEqual(actions, StringComparer.Ordinal))
            problems.Add("recorded with other input actions (a command's buttons would mean other actions)");
        return problems;
    }

    public JsonObject ToJson() => new()
    {
        ["build"] = Build,
        ["game"] = Game,
        ["plugins"] = new JsonArray(Plugins.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["version"] = p.Version }).ToArray()),
        ["mods"] = new JsonArray(Mods.Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["version"] = m.Version }).ToArray()),
        ["actions"] = new JsonArray(Actions.Select(a => (JsonNode)a).ToArray()),
        ["world"] = World,
        ["scene"] = Scene,
        ["dt"] = Dt,
        ["tick"] = Tick,
        ["simTime"] = SimTime,
        ["recordedUtc"] = RecordedUtc,
    };

    public static DemoHeader FromJson(JsonObject o)
    {
        string Text(string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : "";
        var header = new DemoHeader
        {
            Build = Text("build"), Game = Text("game"), World = Text("world"), Scene = Text("scene"), RecordedUtc = Text("recordedUtc"),
            Dt = o["dt"] is JsonValue dt && dt.TryGetValue(out float f) ? f : 0f,
            Tick = o["tick"] is JsonValue tick && tick.TryGetValue(out long t) ? t : 0,
            SimTime = o["simTime"] is JsonValue sim && sim.TryGetValue(out double d) ? d : 0,
        };
        if (o["plugins"] is JsonArray plugins)
            foreach (var p in plugins.OfType<JsonObject>())
                header.Plugins.Add(new SavedPlugin((string?)p["id"] ?? "", (string?)p["version"] ?? ""));
        if (o["mods"] is JsonArray mods)
            foreach (var m in mods.OfType<JsonObject>())
                header.Mods.Add(new SavedMod((string?)m["id"] ?? "", (string?)m["version"] ?? ""));
        if (o["actions"] is JsonArray actions)
            foreach (var a in actions)
                header.Actions.Add((string?)a ?? "");
        return header;
    }
}

// One tick of a demo: the command, or none.
internal readonly record struct DemoTick(bool HasCommand, PlayerCommand Command);

// A demo read whole: the header, the save it starts from, the ticks. `Complete` is false for a file cut
// short; `Hash` is then null, as it is for a recording whose world was gone when it stopped.
internal sealed class DemoFile
{
    public required DemoHeader Header { get; init; }
    public required List<(string Name, byte[] Bytes)> Start { get; init; }
    public required List<DemoTick> Ticks { get; init; }
    public bool Complete { get; init; }
    public ulong? Hash { get; init; }

    // Throws InvalidDataException, saying what is wrong, for a file that is not a demo or whose header or
    // start is cut short; a file whose ticks are cut short reads as far as its last whole tick.
    public static DemoFile Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        string name = Path.GetFileName(path);
        DemoHeader header;
        var start = new List<(string, byte[])>();
        try
        {
            Span<byte> magic = stackalloc byte[8];
            if (reader.Read(magic) != magic.Length || !magic.SequenceEqual(DemoFormat.Magic))
                throw new InvalidDataException($"{name} is not a demo (no SAGEDEMO at its start)");
            ushort version = reader.ReadUInt16();
            if (version != DemoFormat.Version)
                throw new InvalidDataException($"{name} is demo format {version}; this build reads format {DemoFormat.Version}");
            var json = Encoding.UTF8.GetString(ReadBlock(reader, name, "header"));
            header = DemoHeader.FromJson(JsonNode.Parse(json) as JsonObject
                                         ?? throw new InvalidDataException($"{name}: its header is not a JSON object"));
            int files = reader.ReadInt32();
            if (files is < 0 or > 4096) throw new InvalidDataException($"{name}: its start says it has {files} files");
            for (int i = 0; i < files; i++)
            {
                string file = reader.ReadString();
                if (file.Length == 0 || file != Path.GetFileName(file) || file.StartsWith('.'))
                    throw new InvalidDataException($"{name}: its start has a file named '{file}'");
                start.Add((file, ReadBlock(reader, name, file)));
            }
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"{name} is cut short before its first tick: nothing in it can be played");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{name}: its header is not JSON ({ex.Message})");
        }

        var ticks = new List<DemoTick>();
        var last = new DemoTick(false, default);
        bool complete = false;
        ulong? hash = null;
        try
        {
            while (true)
            {
                int tag = stream.ReadByte();
                if (tag < 0) break;   // no end: cut short
                switch ((byte)tag)
                {
                    case DemoFormat.Command:
                        last = new DemoTick(true, ReadCommand(reader));
                        ticks.Add(last);
                        break;
                    case DemoFormat.Repeat:
                        if (!last.HasCommand) throw new InvalidDataException($"{name}: tick {ticks.Count + 1} repeats a command it has not had");
                        ticks.Add(last);
                        break;
                    case DemoFormat.NoCommand:
                        last = new DemoTick(false, default);
                        ticks.Add(last);
                        break;
                    case DemoFormat.End:
                        long count = reader.ReadInt64();
                        bool hasHash = reader.ReadByte() != 0;
                        ulong value = reader.ReadUInt64();
                        if (count != ticks.Count)
                            throw new InvalidDataException($"{name}: its end says {count} ticks, and it has {ticks.Count}");
                        complete = true;
                        hash = hasHash ? value : null;
                        break;
                    default:
                        throw new InvalidDataException($"{name}: tick {ticks.Count + 1} has an unknown record ({tag})");
                }
                if (complete) break;
            }
        }
        catch (EndOfStreamException) { /* the last tick is cut short: the whole ones play */ }

        return new DemoFile { Header = header, Start = start, Ticks = ticks, Complete = complete, Hash = hash };
    }

    private static byte[] ReadBlock(BinaryReader reader, string name, string what)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > DemoFormat.MaxEmbeddedFile)
            throw new InvalidDataException($"{name}: '{what}' says it is {length} bytes");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }

    private static PlayerCommand ReadCommand(BinaryReader r) => new()
    {
        Move = new Vector2(r.ReadSingle(), r.ReadSingle()),
        ViewYaw = r.ReadSingle(),
        ViewPitch = r.ReadSingle(),
        Held = new ActionMask(r.ReadUInt64(), r.ReadUInt64()),
        Pressed = new ActionMask(r.ReadUInt64(), r.ReadUInt64()),
        Released = new ActionMask(r.ReadUInt64(), r.ReadUInt64()),
    };
}

// Writes a demo as it is recorded: the header and start at once, then a tick at a time, so a recording a
// crash interrupts is still a demo of what came before (DemoFile reads it as cut short).
internal sealed class DemoWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private DemoTick _last;
    private bool _any;

    public long Ticks { get; private set; }
    public string Path { get; }

    public DemoWriter(string path, DemoHeader header, IReadOnlyList<(string Name, byte[] Bytes)> start)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
        _writer = new BinaryWriter(_stream, Encoding.UTF8);
        _writer.Write(DemoFormat.Magic);
        _writer.Write(DemoFormat.Version);
        var json = Encoding.UTF8.GetBytes(header.ToJson().ToJsonString());
        _writer.Write(json.Length);
        _writer.Write(json);
        _writer.Write(start.Count);
        foreach (var (name, bytes) in start)
        {
            _writer.Write(name);
            _writer.Write(bytes.Length);
            _writer.Write(bytes);
        }
    }

    public void Write(in DemoTick tick)
    {
        Ticks++;
        if (!tick.HasCommand)
        {
            _writer.Write(DemoFormat.NoCommand);
        }
        else if (_any && _last.HasCommand && Same(_last.Command, tick.Command))
        {
            _writer.Write(DemoFormat.Repeat);
        }
        else
        {
            var c = tick.Command;
            _writer.Write(DemoFormat.Command);
            _writer.Write(c.Move.X);
            _writer.Write(c.Move.Y);
            _writer.Write(c.ViewYaw);
            _writer.Write(c.ViewPitch);
            _writer.Write(c.Held.Bits); _writer.Write(c.Held.High);
            _writer.Write(c.Pressed.Bits); _writer.Write(c.Pressed.High);
            _writer.Write(c.Released.Bits); _writer.Write(c.Released.High);
        }
        _last = tick;
        _any = true;
    }

    // The end: how many ticks, and the world's hash after the last of them (null when there is no world).
    public void Finish(ulong? hash)
    {
        _writer.Write(DemoFormat.End);
        _writer.Write(Ticks);
        _writer.Write((byte)(hash.HasValue ? 1 : 0));
        _writer.Write(hash ?? 0UL);
        Dispose();
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }

    // Bit for bit: a repeat must replay exactly what was sampled (-0 and NaN included).
    private static bool Same(in PlayerCommand a, in PlayerCommand b) =>
        BitEq(a.Move.X, b.Move.X) && BitEq(a.Move.Y, b.Move.Y) && BitEq(a.ViewYaw, b.ViewYaw) && BitEq(a.ViewPitch, b.ViewPitch)
        && a.Held == b.Held && a.Pressed == b.Pressed && a.Released == b.Released;

    private static bool BitEq(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
}
