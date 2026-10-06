#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Sage.Simulation;

// Demos (docs/design/08 §7, issue #333): a session recorded as one PlayerCommand per tick and played back
// through PlayerInput, so that what the player did happens again, the same, in a simulation that is the
// same. HL-style: `record <name>`, `stop`, `playdemo <name>`; files are `user://demos/<name>.sagedemo`.
//
// **It starts from a save.** A command is only what the player asked for; what it does depends on the world
// it reaches. So `record` saves every world into the demo and *loads that save back before the first tick*:
// the recording and every playback begin from the same loaded state, not one from a live world and the
// other from its save (what a load does not keep — a projectile in flight, a footstep's timer — would
// otherwise differ in the first tick and the two would part ways). There is no world seed to record
// instead: the engine's randomness is seeded from the world's own state, which the save carries. The tick
// count, which a save does not keep and a system may read (a beat), is in the header and set on playback.
//
// **What it cannot make the same** is what a save does not carry and the simulation reads anyway: runtime
// entity ids (spawn order), state a system keeps only in memory, and anything written at frame rate (a
// camera rig's transform). Recorded from launch (`+record`), those match too; later in a session they
// may not, and the hash check at the end is what says so.
//
// **Same build, same game, same mods**, or it is refused: a demo is a list of button bits and stick values,
// and the same bits mean other actions, other code and other content in another build. The header says
// what made it and playback says, all at once, everything that differs.
//
// **It proves itself.** `stop` writes the world hash (WorldHash) after the last tick; playback, at its last
// tick, takes the hash again and says whether they agree. A demo that no longer replays to its hash is a
// change in the simulation's behaviour, which is what a regression test wants to hear.
//
// Recording and playback are the simulation's (DemoSystem, Commands phase, ahead of everything that reads
// the command): the host samples as it always does, and in playback the command it sampled is replaced.
// A headless world, which has no host to sample, plays a demo the same way.
internal sealed class Demos
{
    private readonly Engine _engine;
    private string? _root;

    private sealed class Recording
    {
        public required string Name { get; init; }
        public required World World { get; init; }
        public required DemoHeader Header { get; init; }
        public required List<(string Name, byte[] Bytes)> Start { get; init; }
        public required string Path { get; init; }
        public DemoWriter? Writer;     // made on the first tick, which says what step the ticks are
        public bool WarnedDt;
    }

    private sealed class Playback
    {
        public required string Name { get; init; }
        public required World World { get; init; }
        public required DemoFile File { get; init; }
        public int Next;
    }

    private Recording? _recording;
    private Playback? _playback;

    public Demos(Engine engine)
    {
        _engine = engine;
        engine.Signals.WorldDestroying += OnWorldDestroying;
    }

    // Where demos live (`user://demos`). Settable, as SaveSystem.Root is, for a tool or a test.
    public string Root
    {
        get => _root ??= System.IO.Path.Combine(UserPaths.Root, "demos");
        set => _root = value;
    }

    // The world the player is in; the host says (the play world while the editor plays). Without it, the
    // first world that is not an edit world.
    public Func<World?>? PlayerWorld { get; set; }

    public bool IsRecording => _recording != null;
    public bool IsPlaying => _playback != null;

    // What the last playback came to, for `playdemo`'s caller and for tests.
    public DemoResult? LastResult { get; private set; }

    // Why the last Record or Play was refused.
    public string? LastError { get; private set; }

    public string PathOf(string name)
    {
        name = name.Trim();
        if (name.EndsWith(DemoFormat.Extension, StringComparison.OrdinalIgnoreCase)) name = name[..^DemoFormat.Extension.Length];
        foreach (char bad in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        if (name.Length == 0) name = "demo";
        return System.IO.Path.Combine(Root, name + DemoFormat.Extension);
    }

    // ---- recording ----------------------------------------------------------------------------------

    // Starts recording `world` (the player's) into `name`: saves every world, loads the save back, and
    // records from the next tick. Between ticks only.
    public bool Record(string name, World? world = null)
    {
        LastError = null;
        if (_recording != null) return Refuse($"already recording '{_recording.Name}': `stop` first");
        if (_playback != null) return Refuse($"playing '{_playback.Name}': `stop` first");
        if (_engine.Saves.IsMidTick) return Refuse("a demo cannot start during a tick");
        world ??= ThePlayersWorld();
        if (world == null) return Refuse("there is no world to record");
        if (world.Editing) return Refuse($"'{world.Name}' is an edit world: play it first");

        string path = PathOf(name);
        string start = path + ".start";
        try
        {
            Directory.CreateDirectory(Root);
            if (Directory.Exists(start)) Directory.Delete(start, recursive: true);
            if (!_engine.Saves.SaveTo(start, $"demo {name}")) return Refuse($"the start of '{name}' could not be saved");
            // Played from here, so recorded from here: the state the save puts the world in.
            if (!_engine.Saves.LoadFrom(start, $"demo {name}")) return Refuse($"the start of '{name}' could not be loaded back");
            var files = Directory.GetFiles(start).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (System.IO.Path.GetFileName(f), File.ReadAllBytes(f))).ToList();
            _recording = new Recording
            {
                Name = name, World = world, Path = path, Start = files,
                Header = DemoHeader.Current(_engine, world, 0f),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse($"'{name}' cannot be recorded: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(start)) Directory.Delete(start, recursive: true); } catch (IOException) { /* left behind */ }
        }
        Log.Info(LogCat.Input, $"Recording demo '{name}' of world '{world.Name}' to {path}");
        return true;
    }

    // Stops a recording (writing the world's hash after its last tick) or a playback. False when there was
    // neither.
    public bool Stop()
    {
        if (_recording is { } recording)
        {
            _recording = null;
            bool alive = _engine.Worlds.Contains(recording.World);
            try
            {
                var writer = recording.Writer ?? new DemoWriter(recording.Path, recording.Header, recording.Start);
                writer.Finish(alive ? WorldHash.Of(recording.World) : null);
                Log.Info(LogCat.Input, $"Demo '{recording.Name}': {writer.Ticks} ticks recorded to {recording.Path}");
            }
            catch (IOException ex)
            {
                Log.Error(LogCat.Input, $"Demo '{recording.Name}' could not be finished: {ex.Message}");
            }
            return true;
        }
        if (_playback is { } playback)
        {
            Finish(playback, stopped: true);
            return true;
        }
        return false;
    }

    // ---- playback -----------------------------------------------------------------------------------

    // Plays `name` into the world it was recorded in: loads its start, then hands its commands to that
    // world's ticks. Refused, with every reason, when this build, game, plugins, mods or actions are not the
    // ones it was recorded with, and when the file is not a demo or is cut short before its first tick.
    public bool Play(string name)
    {
        LastError = null;
        if (_recording != null) return Refuse($"recording '{_recording.Name}': `stop` first");
        if (_playback != null) Finish(_playback, stopped: true);
        if (_engine.Saves.IsMidTick) return Refuse("a demo cannot start during a tick");

        string path = PathOf(name);
        if (!File.Exists(path)) return Refuse($"no demo '{name}' ({path})");
        DemoFile file;
        try { file = DemoFile.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Refuse($"demo '{name}' cannot be played: {ex.Message}");
        }

        var problems = file.Header.Mismatches(_engine);
        if (problems.Count > 0)
            return Refuse($"demo '{name}' was recorded with something other than what is running, so it would not replay: " +
                          string.Join("; ", problems));
        var world = _engine.Worlds.FirstOrDefault(w => w.Name == file.Header.World && !w.Editing);
        if (world == null) return Refuse($"demo '{name}' plays in world '{file.Header.World}', and there is none");

        string start = path + ".playing";
        try
        {
            if (Directory.Exists(start)) Directory.Delete(start, recursive: true);
            Directory.CreateDirectory(start);
            foreach (var (fileName, bytes) in file.Start)
                File.WriteAllBytes(System.IO.Path.Combine(start, fileName), bytes);
            if (!_engine.Saves.LoadFrom(start, $"demo {name}")) return Refuse($"the start of demo '{name}' could not be loaded");
            // On the tick it was recorded from: a save does not keep the tick count, and a system may read it.
            world.SetClock(file.Header.Tick, file.Header.SimTime);
        }
        catch (IOException ex) { return Refuse($"the start of demo '{name}' could not be unpacked: {ex.Message}"); }
        finally
        {
            try { if (Directory.Exists(start)) Directory.Delete(start, recursive: true); } catch (IOException) { /* left behind */ }
        }

        if (!file.Complete)
            Log.Warn(LogCat.Input, $"Demo '{name}' is cut short: its {file.Ticks.Count} whole ticks play, and there is no hash to check them against");
        _playback = new Playback { Name = name, World = world, File = file };
        LastResult = null;
        Log.Info(LogCat.Input, $"Playing demo '{name}': {file.Ticks.Count} ticks in world '{world.Name}' (recorded {file.Header.RecordedUtc})");
        if (file.Ticks.Count == 0) Finish(_playback, stopped: false);
        return true;
    }

    // ---- the ticks (DemoSystem, World) --------------------------------------------------------------

    // The Commands phase of a step, before anything reads the command: recorded, or replaced.
    internal void OnCommands(World world, in SystemContext ctx)
    {
        if (_recording is { } recording && recording.World == world)
        {
            float dt = ctx.Tick.Dt;
            if (recording.Writer == null)
            {
                recording.Header.Dt = dt;
                try { recording.Writer = new DemoWriter(recording.Path, recording.Header, recording.Start); }
                catch (IOException ex)
                {
                    Log.Error(LogCat.Input, $"Demo '{recording.Name}' cannot be written: {ex.Message}; recording stopped");
                    _recording = null;
                    return;
                }
            }
            else if (dt != recording.Header.Dt && !recording.WarnedDt)
            {
                recording.WarnedDt = true;
                Log.Warn(LogCat.Input, $"Demo '{recording.Name}': the step changed from {recording.Header.Dt} s to {dt} s while recording; it will not replay");
            }
            var tick = world.Resources.TryGet<PlayerInput>(out var input) && input is { HasCommand: true }
                ? new DemoTick(true, input.Command)
                : new DemoTick(false, default);
            try { recording.Writer.Write(tick); }
            catch (IOException ex)
            {
                Log.Error(LogCat.Input, $"Demo '{recording.Name}' could not be written: {ex.Message}; recording stopped");
                recording.Writer.Dispose();
                _recording = null;
            }
        }
        else if (_playback is { } playback && playback.World == world && playback.Next < playback.File.Ticks.Count)
        {
            if (ctx.Tick.Dt != playback.File.Header.Dt)
            {
                Log.Error(LogCat.Input, $"Demo '{playback.Name}' was recorded at a step of {playback.File.Header.Dt} s and this world steps " +
                                        $"{ctx.Tick.Dt} s (sim_tickrate): it cannot replay, and stops");
                Finish(playback, stopped: true);
                return;
            }
            var tick = playback.File.Ticks[playback.Next++];
            var input = world.Resources.GetOrAdd(() => new PlayerInput());
            input.HasCommand = tick.HasCommand;
            input.Command = tick.Command;
            input.Command.Tick = ctx.Tick.Tick;   // the tick it is for, as the host's sample says
        }
    }

    // A world's tick has ended (World.RunFixed): a playback with nothing left to play is finished, here
    // where the world is whole, and its hash taken.
    internal void TickEnded(World world)
    {
        if (_playback is { } playback && playback.World == world && playback.Next >= playback.File.Ticks.Count)
            Finish(playback, stopped: false);
    }

    private void Finish(Playback playback, bool stopped)
    {
        _playback = null;
        ulong? hash = null;
        if (!stopped && _engine.Worlds.Contains(playback.World)) hash = WorldHash.Of(playback.World);
        var result = new DemoResult(playback.Name, playback.Next, playback.File.Ticks.Count, stopped, playback.File.Hash, hash);
        LastResult = result;
        if (stopped)
            Log.Info(LogCat.Input, $"Demo '{playback.Name}' stopped after {result.Played} of {result.Ticks} ticks");
        else if (result.Expected is not { } expected || hash is not { } actual)
            Log.Info(LogCat.Input, $"Demo '{playback.Name}' played: {result.Played} ticks (no hash to check)");
        else if (expected == actual)
            Log.Info(LogCat.Input, $"Demo '{playback.Name}' played: {result.Played} ticks, world hash {actual:x16} as recorded");
        else
            Log.Error(LogCat.Input, $"Demo '{playback.Name}' played {result.Played} ticks to world hash {actual:x16}, and was recorded " +
                                    $"to {expected:x16}: the simulation does not do what it did");
    }

    private void OnWorldDestroying(World world)
    {
        if (_playback?.World == world) Finish(_playback, stopped: true);
        if (_recording?.World == world) Stop();
    }

    private World? ThePlayersWorld() =>
        PlayerWorld?.Invoke() ?? _engine.Worlds.FirstOrDefault(w => !w.Editing);

    private bool Refuse(string why)
    {
        LastError = why;
        Log.Error(LogCat.Input, why);
        return false;
    }

    // ---- console ------------------------------------------------------------------------------------

    public void RegisterCommands(CVarRegistry cvars)
    {
        cvars.RegisterCommand("record", CVarFlags.None,
            "record <name>: record the player's commands to user://demos/<name>.sagedemo, from a save of now, until `stop`.", a =>
            {
                if (a.Count < 1) { Log.Info(LogCat.Console, "usage: record <name>"); return; }
                Record(a[0]);
            });
        cvars.RegisterCommand("stop", CVarFlags.None, "Stop recording or playing a demo.", _ =>
        {
            if (!Stop()) Log.Info(LogCat.Console, "not recording or playing a demo");
        });
        cvars.RegisterCommand("playdemo", CVarFlags.None,
            "playdemo <name>: load a demo's start and play its commands back, checking the world hash at its end.", a =>
            {
                if (a.Count < 1) { Log.Info(LogCat.Console, "usage: playdemo <name>"); return; }
                Play(a[0]);
            });
        cvars.RegisterCommand("demos", CVarFlags.None, "What demos exist in user://demos.", _ =>
        {
            var files = Directory.Exists(Root) ? Directory.GetFiles(Root, "*" + DemoFormat.Extension) : Array.Empty<string>();
            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                Log.Info(LogCat.Console, $"  {System.IO.Path.GetFileNameWithoutExtension(file),-24} {new FileInfo(file).Length / 1024,6} KB");
            Log.Info(LogCat.Console, files.Length == 0 ? $"no demos in {Root}" : $"{files.Length} demo(s) in {Root}");
        });
        cvars.RegisterCommand("world_hash", CVarFlags.None,
            "world_hash [world]: a hash of a world's saved state (the player's world by default), as a demo checks it.", a =>
            {
                var world = a.Count > 0 ? _engine.Worlds.FirstOrDefault(w => w.Name == a[0]) : ThePlayersWorld();
                if (world == null) { Log.Info(LogCat.Console, a.Count > 0 ? $"no world '{a[0]}'" : "no world"); return; }
                Log.Info(LogCat.Console, $"'{world.Name}' at tick {world.Tick}: {WorldHash.Of(world):x16}");
            });
    }
}

// What a playback came to: how many of its ticks played, whether it was stopped before its end, and the
// world hash it was recorded to and the one it reached (null when there is none to compare).
internal sealed record DemoResult(string Name, int Played, int Ticks, bool Stopped, ulong? Expected, ulong? Actual)
{
    public bool Matched => !Stopped && Expected is { } e && Actual is { } a && e == a;
}

// A hash of a world's saved state (issue #333): the world file a save would write, entities and saved
// resources included, hashed (FNV-1a, 64 bits). Two worlds with the same hash are, as far as a save can
// tell, the same — the check a demo makes at its end, and what a regression test compares.
internal static class WorldHash
{
    public static ulong Of(World world)
    {
        var engine = world.Engine ?? throw new InvalidOperationException($"World '{world.Name}' has no engine to save it");
        return Of(engine.Saves.StateOf(world));
    }

    public static ulong Of(string state)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(state))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}

// Commands phase, before everything that reads the player's command: a demo records it or replaces it
// (Demos). Steps only (the default condition): a held pass is not a tick a demo has.
[System(Id, Phase.Commands, Before = new[] { "?sage.character.player_control", QuickSaveKeysSystem.Id })]
internal sealed class DemoSystem : ISystem
{
    public const string Id = "sage.input.demo";

    private readonly Demos _demos;

    public DemoSystem(Engine engine) { _demos = engine.Demos; }

    public void Run(in SystemContext ctx)
    {
        if (_demos.IsRecording || _demos.IsPlaying) _demos.OnCommands(ctx.World, ctx);
    }
}
