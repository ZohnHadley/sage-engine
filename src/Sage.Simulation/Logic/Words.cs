#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// The base's words for a data-only game (issue #275, REDESIGN §4.3 stage 2): what a trigger, a relay, a
// state machine, a dialogue line or a topic asks and does when there is no C# at all. Each needs nothing
// but the engine, so every game has them whatever plugins it lists; the words that need gameplay
// (`has_tag`, `is_alive`, `set_tag`, `cue`) are Gameplay's (GameplayConditions.cs).
//
//   "requires": { "all": [ { "random": 0.25 }, { "entity_exists": "boss" }, { "distance_to": "altar", "max": 3 },
//                          { "in_scene": "crypt" } ] },
//   "then":     [ { "spawn_prefab": "ghost", "at": "altar", "offset": [0, 1, 0], "name": "ghost" },
//                 { "destroy": "!other" }, { "teleport": "!subject", "to": "crypt_exit" },
//                 { "play_sound": "bell", "at": "!other" }, { "pass_time": 8 }, { "load_scene": "crypt", "entry": "crypt_in" },
//                 { "save_game": "checkpoint" }, { "log": "the bell rang" }, { "message": "Something stirs.", "kind": "bad" },
//                 { "wait": 2 } ]
//
// An entity is named as a wire names one (LogicTargets): a name, or `!subject` / `!other` and their
// aliases. A name that finds nothing makes a condition fail and an action do nothing (logged at Debug).
// What changes the world (spawn, destroy, teleport) does it at once: actions run outside query loops
// (a relay's Trigger, a state machine's step, a conversation), never inside one.

// ---- the world's random numbers ----------------------------------------------------------------------

// One random stream per world, for content (`random`): SplitMix64, the same on every machine, never
// System.Random. It starts from the world's name, so two runs of a game draw the same numbers in the same
// order, and its state is the saved `random` resource, so a loaded game draws what it would have
// (test: RandomIsDeterministic_AcrossRunsAndASave). Every ask draws one number: a `random` asked every
// frame (a widget's `visible`) moves the stream at the frame rate, so use it on wires, relays and
// transitions, which are asked when something happens.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // stage 2 logic (#275)
[SavedResource("random", Plugin = RegistrationOwners.Core)]
internal sealed class WorldRandom
{
    // 0: not drawn from yet; the first draw starts it from the world's name.
    public ulong State { get; set; }

    public static WorldRandom Of(World world) => world.Resources.GetOrAdd(static () => new WorldRandom());

    // 0 <= value < 1.
    public double Next(World world)
    {
        if (State == 0) State = Seed(world.Name);
        State += 0x9E3779B97F4A7C15UL;
        ulong z = State;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    // FNV-1a of the name: stable across runs and machines, unlike string.GetHashCode.
    private static ulong Seed(string name)
    {
        ulong hash = 0xCBF29CE484222325UL;
        foreach (char c in name ?? "")
        {
            hash ^= c;
            hash *= 0x100000001B3UL;
        }
        return hash == 0 ? 0x9E3779B97F4A7C15UL : hash;
    }
}

// ---- conditions ------------------------------------------------------------------------------------

// `{ "random": 0.25 }`: holds one time in four, drawn from the world's stream (WorldRandom).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Condition("random", Plugin = RegistrationOwners.Core)]
internal sealed class RandomCondition : ICondition
{
    [EntryValue, Property(Min = 0, Max = 1, Tooltip = "The chance it holds, from 0 (never) to 1 (always); each ask draws again")]
    public double Chance = 0.5;

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not this time";
        return WorldRandom.Of(context.World).Next(context.World) < Chance;
    }
}

// `{ "entity_exists": "boss" }`: something by that name is in the world (or `!other` is still alive).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Condition("entity_exists", Plugin = RegistrationOwners.Core)]
internal sealed class EntityExistsCondition : ICondition
{
    [EntryValue, Property(Tooltip = "An entity's name, or !subject / !other")]
    public string Entity = "";

    private Entity _found;

    public bool Test(in ConditionContext context, out string why)
    {
        why = "it is not here";
        if (Entity.Length == 0) return true;
        var found = LogicTargets.Find(context.World, Entity, context.Subject, context.Other, ref _found);
        return context.World.IsAlive(found);
    }
}

// `{ "distance_to": "altar", "max": 3 }`: the subject (or `from`) is within [min, max] metres of it.
// Either one missing: it does not hold.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Condition("distance_to", Plugin = RegistrationOwners.Core)]
internal sealed class DistanceToCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The entity measured to: a name, or !subject / !other")]
    public string To = "";
    [Property(Tooltip = "The entity measured from: a name, or !subject / !other")]
    public string From = LogicTargets.Subject;
    [Property(Min = 0, Unit = "m", Tooltip = "The least the distance may be")]
    public float Min;
    [Property(Min = 0, Unit = "m", Tooltip = "The most the distance may be")]
    public float Max = float.PositiveInfinity;

    private Entity _to, _from;

    public bool Test(in ConditionContext context, out string why)
    {
        why = "too far away";
        var world = context.World;
        var to = LogicTargets.Find(world, To, context.Subject, context.Other, ref _to);
        var from = LogicTargets.Find(world, From, context.Subject, context.Other, ref _from);
        if (!world.TryGet<GlobalTransform>(to, out var a) || !world.TryGet<GlobalTransform>(from, out var b)) return false;
        float distance = Vector3.Distance(a.Current.Position, b.Current.Position);
        if (distance < Min) why = "too close";
        return distance >= Min && distance <= Max;
    }
}

// `{ "in_scene": "crypt" }`: the world's scene is that one (Scenes.Current).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Condition("in_scene", Plugin = RegistrationOwners.Core)]
internal sealed class InSceneCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The scene the world must be in")]
    public RecordRef<SceneRecord> Scene;

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not here";
        return Scene.IsEmpty || Scenes.Current(context.World) == Scene.Id;
    }
}

// ---- actions ---------------------------------------------------------------------------------------

// `{ "spawn_prefab": "ghost", "at": "altar", "offset": [0, 1, 0], "yaw": 90, "name": "ghost" }`: a prefab,
// at an entity (turned as it is, and `yaw` more) or, with no `at`, at `position` in world coordinates.
// What it spawns is saved like anything a game spawns (it has a persistent id).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("spawn_prefab", Plugin = RegistrationOwners.Core)]
internal sealed class SpawnPrefabAction : IAction
{
    [EntryValue, Property(Tooltip = "The prefab to spawn")]
    public RecordRef<PrefabRecord> Prefab;
    [Property(Tooltip = "Where: an entity's name, or !subject / !other; empty: at `position`")]
    public string At = "";
    [Property(Unit = "m", Tooltip = "Where in world coordinates, when there is no `at`")]
    public Vector3 Position;
    [Property(Unit = "m", Tooltip = "Added to where it goes, in world axes")]
    public Vector3 Offset;
    [Property(Unit = "deg", Tooltip = "Its heading; with `at`, added to that entity's")]
    public float Yaw;
    [Property(Tooltip = "The name it is given, for wires and other words to find it by")]
    public string Name = "";

    public void Run(in ActionContext context)
    {
        if (Prefab.IsEmpty) return;
        var world = context.World;
        Vector3 position;
        float yaw = Yaw;
        if (At.Length > 0)
        {
            var at = LogicTargets.Find(world, At, context.Subject, context.Other);
            if (!world.TryGet<GlobalTransform>(at, out var where))
            {
                Log.Debug(LogCat.Events, $"spawn_prefab {Prefab.Id}: nothing called '{At}' to spawn at");
                return;
            }
            position = where.Current.Position + Offset;
            yaw += SageMath.YawOf(where.Current.Rotation) * 180f / MathF.PI;
        }
        else position = world.Origin().ToOrigin(Position) + Offset;

        var entity = world.Spawn(Prefab.Id, position, yaw);
        if (!entity.IsNull && Name.Length > 0) entity.Name = Name;
    }
}

// `{ "destroy": "crate_3" }`, `{ "destroy": "!other" }`: gone, with the children its prefab gave it.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("destroy", Plugin = RegistrationOwners.Core)]
internal sealed class DestroyAction : IAction
{
    [EntryValue, Property(Tooltip = "Who goes: an entity's name, or !subject / !other")]
    public string Target = "";

    public void Run(in ActionContext context)
    {
        var target = LogicTargets.Find(context.World, Target, context.Subject, context.Other);
        if (context.World.IsAlive(target)) context.World.Destroy(target);
        else Log.Debug(LogCat.Events, $"destroy: nothing called '{Target}'");
    }
}

// `{ "teleport": "!subject", "to": "crypt_exit" }`: moved at once (no interpolated slide) to an entity,
// turned as it is unless `yaw` says otherwise, or, with no `to`, to `position` in world coordinates.
// A child entity is not moved (it goes where its parent does).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("teleport", Plugin = RegistrationOwners.Core)]
internal sealed class TeleportAction : IAction
{
    [EntryValue, Property(Tooltip = "Who moves: an entity's name, or !subject / !other")]
    public string Target = LogicTargets.Subject;
    [Property(Tooltip = "Where to: an entity's name, or !subject / !other; empty: `position`")]
    public string To = "";
    [Property(Unit = "m", Tooltip = "Where in world coordinates, when there is no `to`")]
    public Vector3 Position;
    [Property(Unit = "m", Tooltip = "Added to where it goes, in world axes")]
    public Vector3 Offset;
    [Property(Unit = "deg", Tooltip = "The heading it ends up with; leave out to face as `to` does (or keep its own)")]
    public float? Yaw;

    public void Run(in ActionContext context)
    {
        var world = context.World;
        var target = LogicTargets.Find(world, Target, context.Subject, context.Other);
        if (!world.TryGet<Transform>(target, out var transform))
        {
            Log.Debug(LogCat.Events, $"teleport: nothing called '{Target}'");
            return;
        }
        if (!target.Parent.IsNull)
        {
            Log.Once(LogCat.Events, LogLevel.Warn, $"teleport-child:{Target}",
                $"teleport: {World.Describe(target)} is a child entity and goes where its parent does; teleport the parent");
            return;
        }

        if (To.Length > 0)
        {
            var to = LogicTargets.Find(world, To, context.Subject, context.Other);
            if (!world.TryGet<GlobalTransform>(to, out var where))
            {
                Log.Debug(LogCat.Events, $"teleport: nothing called '{To}' to go to");
                return;
            }
            transform.LocalPosition = where.Current.Position + Offset;
            if (Yaw == null) transform.LocalRotation = SageMath.RotationFromYaw(SageMath.YawOf(where.Current.Rotation));
        }
        else transform.LocalPosition = world.Origin().ToOrigin(Position) + Offset;
        if (Yaw is { } yaw) transform.LocalRotation = SageMath.RotationFromYaw(yaw * MathF.PI / 180f);
        world.Teleport(target, transform);
    }
}

// What the simulation asks to be heard (issue #275): a sound record, at an entity or everywhere. The
// client's audio system plays it; a headless world sends it into a queue nobody reads, like CueTriggered.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[GameEvent]
public readonly record struct SoundRequested(RecordId Sound, Entity Source, Vector3 Point, bool Positional, float Volume);

// `{ "play_sound": "bell", "at": "!other", "volume": 0.8 }`: the sound at that entity; with no `at`,
// not placed (heard the same everywhere, like music or a voice in the head).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("play_sound", Plugin = RegistrationOwners.Core)]
internal sealed class PlaySoundAction : IAction
{
    [EntryValue, Property(Tooltip = "The sound record to play")]
    public RecordRef<SoundRecord> Sound;
    [Property(Tooltip = "Where it is heard from: an entity's name, or !subject / !other; empty: everywhere")]
    public string At = "";
    [Property(Min = 0, Max = 1, Tooltip = "How loud, before the sound's own volume and the buses")]
    public float Volume = 1f;

    public void Run(in ActionContext context)
    {
        if (Sound.IsEmpty) return;
        var world = context.World;
        if (At.Length == 0)
        {
            world.Events.Send(new SoundRequested(Sound.Id, default, Vector3.Zero, false, Volume));
            return;
        }
        var at = LogicTargets.Find(world, At, context.Subject, context.Other);
        if (!world.TryGet<GlobalTransform>(at, out var where))
        {
            Log.Debug(LogCat.Events, $"play_sound {Sound.Id}: nothing called '{At}' to play it at");
            return;
        }
        world.Events.Send(new SoundRequested(Sound.Id, at, where.Current.Position, true, Volume));
    }
}

// `{ "pass_time": 8 }`: eight game hours go by at the tick's end (Time.Pass: the clock skips and the world
// hears TimePassed), as resting or waiting would.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("pass_time", Plugin = RegistrationOwners.Core)]
internal sealed class PassTimeAction : IAction
{
    [EntryValue, Property(Min = 0, Unit = "h", Tooltip = "Game hours that pass")]
    public double Hours;
    [Property(Tooltip = "Why, for whoever hears TimePassed: rest, wait, travel")]
    public string Reason = "wait";

    public void Run(in ActionContext context)
    {
        if (Hours > 0) Time.Pass(context.World, Hours, Reason);
    }
}

// `{ "load_scene": "crypt" }`: the world goes to that scene at the tick's end, and the player to its
// start; `"entry": "crypt_in"` puts the player at that entry instead, as a load door does (Travel.To),
// and `"hours"` pass on the way.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("load_scene", Plugin = RegistrationOwners.Core)]
internal sealed class LoadSceneAction : IAction
{
    [EntryValue, Property(Tooltip = "The scene to go to")]
    public RecordRef<SceneRecord> Scene;
    [Property(Tooltip = "Where the player arrives: a placement's name or a map entity's targetname; empty: the scene's start")]
    public string Entry = "";
    [Property(Min = 0, Unit = "h", Tooltip = "Game hours the journey takes")]
    public double Hours;

    public void Run(in ActionContext context)
    {
        if (Scene.IsEmpty) return;
        if (Entry.Length > 0) Travel.To(context.World, Scene.Id, Entry, Hours, "travel");
        else Travel.ToStart(context.World, Scene.Id, Hours);
    }
}

// `{ "save_game": "checkpoint" }`: a save into that slot at the tick's end; with no slot, an autosave
// (the next of the rotation).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("save_game", Plugin = RegistrationOwners.Core)]
internal sealed class SaveGameAction : IAction
{
    [EntryValue, Property(Tooltip = "The slot to save into; empty: an autosave")]
    public string Slot = "";

    public void Run(in ActionContext context)
    {
        if (context.World.Engine is not { } engine || context.World.Editing) return;
        if (Slot.Length > 0) engine.Saves.RequestSave(Slot);
        else engine.Saves.Autosave();
    }
}

// `{ "log": "the bell rang" }`: a line in the log (category Events), for whoever is building the level.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("log", Plugin = RegistrationOwners.Core)]
internal sealed class LogAction : IAction
{
    [EntryValue, Property(Tooltip = "What the log says")]
    public string Text = "";
    [Property(Tooltip = "How loud: Debug, Info, Warn or Error")]
    public LogLevel Level = LogLevel.Info;

    public void Run(in ActionContext context)
    {
        if (Text.Length > 0) Log.Write(LogCat.Events, Level, Text);
    }
}

// `{ "message": "Something stirs.", "kind": "bad", "seconds": 4 }`: a line for the player, the one that
// scrolls past the bottom of the screen (world.Say; MessageLog).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Action("message", Plugin = RegistrationOwners.Core)]
internal sealed class MessageAction : IAction
{
    [EntryValue, Property(Tooltip = "What the player reads")]
    public string Text = "";
    [Property(Tooltip = "What it is about, for its colour: Info, Good or Bad")]
    public MessageKind Kind = MessageKind.Info;
    [Property(Min = 0, Unit = "s", Tooltip = "How long it stays on screen")]
    public float Seconds = 5f;

    public void Run(in ActionContext context) => context.World.Say(Text, Kind, Seconds);
}
