#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Sage.Simulation;

// Fixed and cinematic cameras driven by entity I/O (issue #80, REDESIGN §5 phase 4a: "a scripted camera
// cut from I/O").
//
// A **scripted camera** is an ordinary camera entity (a `Camera`, placed and moved like anything else)
// that starts disabled with a high priority. A wire turns it on and it wins the screen; a wire, or its
// hold time running out, turns it off and the screen goes back to whatever had it — the player's rig, or
// ActiveCamera when no camera entity draws there. That is the whole of an HL1 scripted scene's camera.
//
//   inputs   CameraOn [hold seconds]   enable the camera; with a number > 0, turn it off again after that
//                                      long (0: stay on until CameraOff); no number: the entity's own
//                                      ScriptedCamera.HoldTime, 0 without one
//            CameraOff                 disable the camera
//   outputs  OnCameraOn / OnCameraOff  fired on the change only: turning on a camera that is on restarts
//                                      its hold and fires nothing
//
// Both inputs work on **any** entity with a `Camera`, not only on a scripted one; the names are
// camera-specific because the input namespace is one global table (EntityInputs).
//
// **A cut, not a blend.** The view switches on the first frame after the tick the input was delivered
// in. Blends wait for 4b's tweens, which will animate between two poses; nothing here assumes a cut
// beyond not having one.
//
// **Ownership: the engine (`sage.core`)**, like the `camera` component, part and director. The inputs
// only toggle components the engine owns, so every game has them whatever plugins it lists, the scene
// or map that wires them validates against the same table in every game, and no plugin has to know
// about cameras (entity I/O, `sage.gameplay.io`, would otherwise depend on them) or has to be added to a
// data-only game's plugin list. Firing them still needs the I/O plugin, as every wire does; a game
// without it registers two dictionary entries and never runs them.

// "components": { "sage:scripted_camera": { "holdTime": 3, "lockInput": true } }, or the
// `scripted_camera` part. The companion of a `Camera` that I/O drives: how long `CameraOn` holds when
// the wire gives no time, and whether the player's input is locked while it is on.
[Experimental("SAGE0123")]
[Component("sage:scripted_camera")]
public struct ScriptedCamera : IComponent
{
    [Property(Min = 0, Unit = "s", Tooltip = "How long CameraOn holds when the wire gives no time; 0 = until CameraOff")]
    public float HoldTime;
    [Property(Tooltip = "While on and drawing to the screen, the local player's movement, look and buttons are held")]
    public bool LockInput;

    // Seconds left before the camera turns itself off; 0 = no hold running. Saved, so a save made
    // mid-cut ends the cut on time.
    public float Remaining;

    // Who started the cut: the activator of the input that turned it on, handed to OnCameraOff when the
    // hold ends it. A handle, meaningless in another session.
    [Transient] public Entity Activator;
}

// "scripted_camera": { "holdTime": 3, "lockInput": true }
//
// Adds a ScriptedCamera. Use it beside a `camera` part that starts disabled with a priority above the
// player's rig — which is what the engine's `sage:scripted_camera` prefab is.
[Experimental("SAGE0123")]
[PrefabPart("scripted_camera", Plugin = RegistrationOwners.Core)]
public sealed class ScriptedCameraPart : IPrefabPart
{
    [Property(Min = 0, Unit = "s", Tooltip = "How long CameraOn holds when the wire gives no time; 0 = until CameraOff")]
    public float HoldTime;
    [Property(Tooltip = "While on and drawing to the screen, the local player's movement, look and buttons are held")]
    public bool LockInput;

    public void Apply(in PrefabPartContext ctx)
    {
        if (!(HoldTime >= 0f) || !float.IsFinite(HoldTime))
            ctx.Warn($"holdTime {HoldTime} is not a time in seconds; 0 (until CameraOff) is used");
        ctx.World.Add(ctx.Entity, new ScriptedCamera
        {
            HoldTime = HoldTime >= 0f && float.IsFinite(HoldTime) ? HoldTime : 0f,
            LockInput = LockInput,
        });
    }
}

// The inputs and outputs, registered by the engine (Engine's constructor, owner sage.core).
internal static class CameraIO
{
    public const string On = "CameraOn";
    public const string Off = "CameraOff";
    public const string OnCameraOn = "OnCameraOn";
    public const string OnCameraOff = "OnCameraOff";

    // Remaining time at or under this is "done": a hold of N seconds counted down in 1/60 s ticks
    // leaves float dust, and it should end on the tick a CameraOff wired with the same delay arrives.
    internal const float Epsilon = 1e-4f;

    public static void Register(Engine engine)
    {
        engine.Inputs.Register(On, TurnOn);
        engine.Inputs.Register(Off, TurnOff);
        engine.Outputs.Declare(OnCameraOn, "This camera was turned on (CameraOn) and was off.");
        engine.Outputs.Declare(OnCameraOff, "This camera was turned off (CameraOff, or its hold time ran out) and was on.");
    }

    private static void TurnOn(World world, in IOContext io)
    {
        var self = io.Self;
        if (!world.Has<Camera>(self))
        {
            Log.Warn(LogCat.Events, $"I/O: {On} at {World.Describe(self)}, which has no camera");
            return;
        }

        // The hold: the wire's number, else the entity's own, else none.
        float hold = world.TryGet<ScriptedCamera>(self, out var scripted) ? scripted.HoldTime : 0f;
        if (io.Parameter.Length > 0)
        {
            if (float.TryParse(io.Parameter, NumberStyles.Float, CultureInfo.InvariantCulture, out float wired)
                && wired >= 0f && float.IsFinite(wired))
                hold = wired;
            else
                Log.Warn(LogCat.Events, $"I/O: {On}({io.Parameter}) at {World.Describe(self)}: the parameter is a hold "
                                      + $"time in seconds; {hold} is used");
        }

        // A hold needs somewhere to count down. Structural, which is allowed here: inputs are delivered
        // outside any query (EntityIO.Run).
        if (hold > 0f && !world.Has<ScriptedCamera>(self)) world.Add(self, new ScriptedCamera());
        if (world.Has<ScriptedCamera>(self))
        {
            ref var state = ref world.Get<ScriptedCamera>(self);
            state.Remaining = hold;
            state.Activator = io.Activator;
        }

        ref var camera = ref world.Get<Camera>(self);
        if (camera.Enabled) return;          // already on: the hold restarted, nothing changed
        camera.Enabled = true;
        world.FireOutput(self, OnCameraOn, io.Activator);
    }

    private static void TurnOff(World world, in IOContext io)
    {
        var self = io.Self;
        if (!world.Has<Camera>(self))
        {
            Log.Warn(LogCat.Events, $"I/O: {Off} at {World.Describe(self)}, which has no camera");
            return;
        }
        if (world.Has<ScriptedCamera>(self)) world.Get<ScriptedCamera>(self).Remaining = 0f;

        ref var camera = ref world.Get<Camera>(self);
        if (!camera.Enabled) return;
        camera.Enabled = false;
        world.FireOutput(self, OnCameraOff, io.Activator);
    }
}

// EntityIO phase, before the dispatch: counts down every scripted camera's hold and turns the camera off
// when it runs out, firing OnCameraOff. Before the dispatch so that a hold of N seconds ends on the same
// tick as a `CameraOff` wired with a delay of N: the input that set the hold is delivered after this
// has run for its tick, so the first second counted is the next tick's.
[Experimental("SAGE0123")]
[System(Id, Phase.EntityIO, Before = new[] { "?sage.io.dispatch" })]
internal sealed class ScriptedCameraSystem : ISystem
{
    public const string Id = "sage.camera.scripted";

    private readonly World _world;
    private readonly Query<Camera, ScriptedCamera> _cameras;

    public ScriptedCameraSystem(World world)
    {
        _world = world;
        _cameras = world.Query<Camera, ScriptedCamera>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.Tick.Dt;
        foreach (var (cameras, scripted, entities) in _cameras.Chunks)
        {
            var c = cameras.Span;
            var s = scripted.Span;
            for (int i = 0; i < c.Length; i++)
            {
                if (!(s[i].Remaining > 0f)) continue;
                if (!c[i].Enabled) { s[i].Remaining = 0f; continue; }   // turned off some other way
                s[i].Remaining -= dt;
                if (s[i].Remaining > CameraIO.Epsilon) continue;

                s[i].Remaining = 0f;
                c[i].Enabled = false;
                // Queued, not run: firing only adds to entity I/O's list, which is safe inside a query.
                _world.FireOutput(entities.EntityAt(i), CameraIO.OnCameraOff, s[i].Activator);
            }
        }
    }
}

// Commands phase, after the player's controller: while a scripted camera with LockInput is on and draws
// to the screen, the local player's pawn stands still — no movement, no buttons, and the view held where
// it was when the lock began (asked of the host each tick with PlayerInput.RequestView, so the mouse
// does not wind the view round behind the cut and snap it on the way out). AI and other pawns are not
// touched: a cut locks the player, not the world.
//
// It edits PawnIntent after the controller wrote it and before anything reads it (PawnIntent is final
// after Commands, CharacterModule's contract), which is the one place a gate on a player's intent can go
// without the client knowing: headless tests see exactly what a played game does.
[Experimental("SAGE0123")]
[System(Id, Phase.Commands, After = new[] { "?sage.character.player_control" })]
internal sealed class CameraInputLockSystem : ISystem
{
    public const string Id = "sage.camera.input_lock";

    private readonly World _world;
    private readonly Query<Camera, ScriptedCamera> _cameras;
    private readonly Query<PawnIntent> _players;
    private PlayerInput? _input;
    private bool _locked;
    private float _yaw, _pitch;

    public CameraInputLockSystem(World world)
    {
        _world = world;
        _cameras = world.Query<Camera, ScriptedCamera>();
        _players = world.Query<PawnIntent>().AllTags(Tags.Get<PlayerControlled>());
    }

    // Whether the player's input is being held this tick.
    public bool Locked => _locked;

    public void Run(in SystemContext ctx)
    {
        if (!AnyLocking())
        {
            _locked = false;
            return;
        }

        if (_input == null) _world.Resources.TryGet(out _input);
        foreach (var (intents, _) in _players.Chunks)
        {
            var intent = intents.Span;
            for (int i = 0; i < intent.Length; i++)
            {
                if (!_locked)
                {
                    // The lock begins: the view it holds is the one the player had.
                    _locked = true;
                    _yaw = intent[i].Yaw;
                    _pitch = intent[i].Pitch;
                }
                intent[i].Move = default;
                intent[i].Held = default;
                intent[i].Pressed = default;
                intent[i].Yaw = _yaw;
                intent[i].Pitch = _pitch;
            }
        }
        if (_locked) _input?.RequestView(_yaw, _pitch);
    }

    private bool AnyLocking()
    {
        foreach (var (cameras, scripted, _) in _cameras.Chunks)
        {
            var c = cameras.Span;
            var s = scripted.Span;
            for (int i = 0; i < c.Length; i++)
                if (s[i].LockInput && c[i].Enabled && c[i].IsScreen) return true;
        }
        return false;
    }
}
