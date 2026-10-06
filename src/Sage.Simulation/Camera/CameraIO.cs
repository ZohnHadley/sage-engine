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
//   inputs   CameraOn [hold [blend [ease]]]
//                                      enable the camera; with a number > 0, turn it off again after that
//                                      long (0: stay on until CameraOff); no number: the entity's own
//                                      ScriptedCamera.HoldTime, 0 without one. A second number blends
//                                      into it over that many seconds from the view the screen had
//                                      (issue #90; CameraBlend), along the named easing curve (`SineInOut`;
//                                      Easing.TryParse); none: the entity's BlendTime, which is 0 — a cut
//            CameraOff [blend [ease]]  disable the camera; with a number, blend out of it over that many seconds
//                                      to whatever has its target and slot next (issue 4n-19; CameraBlend.Out),
//                                      along the named curve; none: the entity's BlendOutTime, which is 0 — a cut
//   outputs  OnCameraOn / OnCameraOff  fired on the change only: turning on a camera that is on restarts
//                                      its hold and fires nothing
//
// Both inputs work on **any** entity with a `Camera`, not only on a scripted one; the names are
// camera-specific because the input namespace is one global table (EntityInputs).
//
// **A cut unless asked for a blend.** The view switches on the first frame after the tick the input was
// delivered in. With a blend time (the parameter's second number, or ScriptedCamera.BlendTime) the screen
// eases from the view it had — the main view, whatever drew it — to the camera's own over that long
// (issue #90, CameraBlend); only a camera that draws to the screen blends. Turning it off is a cut too,
// unless it says otherwise: `CameraOff`'s blend time or ScriptedCamera.BlendOutTime (also used when the
// hold runs out) eases from where the camera was to what comes back (issue 4n-19).
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

    // How CameraOn brings it in when the wire does not say (issue #90): 0 = a cut.
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds CameraOn blends into it from the screen's view when the wire gives none; 0 = a cut")]
    public float BlendTime;
    [Property(Tooltip = "The easing curve of that blend")]
    public Ease BlendEase;
    // How CameraOff (or the hold running out) gives the view back when the wire does not say (issue 4n-19):
    // 0 = a cut. Along BlendEase.
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds CameraOff (or the hold ending) blends back to what had the screen when the wire gives none; 0 = a cut")]
    public float BlendOutTime;
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
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds CameraOn blends into it from the screen's view when the wire gives none; 0 = a cut")]
    public float BlendTime;
    [Property(Tooltip = "The easing curve of that blend")]
    public Ease BlendEase = Ease.SmoothStep;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds CameraOff (or the hold ending) blends back to what had the screen when the wire gives none; 0 = a cut")]
    public float BlendOutTime;

    public void Apply(in PrefabPartContext ctx)
    {
        if (!(HoldTime >= 0f) || !float.IsFinite(HoldTime))
            ctx.Warn($"holdTime {HoldTime} is not a time in seconds; 0 (until CameraOff) is used");
        if (!(BlendTime >= 0f) || !float.IsFinite(BlendTime))
            ctx.Warn($"blendTime {BlendTime} is not a time in seconds; 0 (a cut) is used");
        if (!(BlendOutTime >= 0f) || !float.IsFinite(BlendOutTime))
            ctx.Warn($"blendOutTime {BlendOutTime} is not a time in seconds; 0 (a cut) is used");
        ctx.World.Add(ctx.Entity, new ScriptedCamera
        {
            HoldTime = HoldTime >= 0f && float.IsFinite(HoldTime) ? HoldTime : 0f,
            LockInput = LockInput,
            BlendTime = BlendTime >= 0f && float.IsFinite(BlendTime) ? BlendTime : 0f,
            BlendEase = BlendEase,
            BlendOutTime = BlendOutTime >= 0f && float.IsFinite(BlendOutTime) ? BlendOutTime : 0f,
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

        // The hold and the blend: the wire's numbers, else the entity's own, else none (a cut that stays).
        bool hasScripted = world.TryGet<ScriptedCamera>(self, out var scripted);
        float hold = hasScripted ? scripted.HoldTime : 0f;
        float blend = hasScripted ? scripted.BlendTime : 0f;
        var ease = hasScripted ? scripted.BlendEase : Ease.SmoothStep;
        ReadParameter(On, io.Parameter, self, first: 0, ref hold, ref blend, ref ease);

        // A hold needs somewhere to count down. Structural, which is allowed here: inputs are delivered
        // outside any query (EntityIO.Run).
        if (hold > 0f && !world.Has<ScriptedCamera>(self)) world.Add(self, new ScriptedCamera());
        if (world.Has<ScriptedCamera>(self))
        {
            ref var state = ref world.Get<ScriptedCamera>(self);
            state.Remaining = hold;
            state.Activator = io.Activator;
        }

        var camera = world.Get<Camera>(self);
        if (camera.Enabled) return;          // already on: the hold restarted, nothing changed

        // From the view the screen has now, to this camera's: a blend, or a cut that ends any blend left
        // over from the last time it came on. Before the camera is switched on, and through a fresh ref
        // after: adding the blend moves the entity's components.
        if (blend > 0f && camera.IsScreen && ScreenView(world, camera.Slot, out var from) && from.Entity != self)
            CameraBlends.Begin(world, self, from, blend, ease);
        else
            CameraBlends.End(world, self);

        world.Get<Camera>(self).Enabled = true;
        world.FireOutput(self, OnCameraOn, io.Activator);
    }

    // What a camera in screen `slot` blends from: that slot's view (a split-screen partner's own), else
    // the main view.
    private static bool ScreenView(World world, int slot, out CameraView view)
    {
        if (world.Resources.TryGet<CameraViews>(out var views) && views != null)
        {
            int at = views.IndexOf("", Math.Max(0, slot));
            if (at >= 0) { view = views[at]; return true; }
        }
        return world.TryGetMainView(out view);
    }

    // `hold [blend [ease]]` (CameraOn, from field 0) or `blend [ease]` (CameraOff, from field 1),
    // space-separated (a `.map` wire is comma-separated). A word that is not what it should be is a
    // warning, and the entity's own value stands.
    private static void ReadParameter(string input, string parameter, Entity self, int first, ref float hold, ref float blend, ref Ease ease)
    {
        if (parameter.Length == 0) return;
        ReadOnlySpan<char> rest = parameter;
        for (int field = first; field < 3; field++)
        {
            rest = rest.TrimStart();
            if (rest.IsEmpty) return;
            int end = 0;
            while (end < rest.Length && !char.IsWhiteSpace(rest[end])) end++;
            var token = rest[..end];
            rest = rest[end..];

            if (field == 2)
            {
                if (!Easing.TryParse(token, out ease))
                    Log.Warn(LogCat.Events, $"I/O: {input}({parameter}) at {World.Describe(self)}: '{token.ToString()}' is not an "
                                          + $"easing curve; {ease} is used");
                continue;
            }
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds)
                && seconds >= 0f && float.IsFinite(seconds))
            {
                if (field == 0) hold = seconds; else blend = seconds;
            }
            else
            {
                Log.Warn(LogCat.Events, $"I/O: {input}({parameter}) at {World.Describe(self)}: the parameter is "
                                      + (field == 0 ? $"a hold time in seconds; {hold} is used"
                                         : first == 0 ? $"a hold time and a blend time in seconds; a blend of {blend} is used"
                                         : $"a blend time in seconds; a blend of {blend} is used"));
                if (field == first) return;
            }
        }
        if (!rest.TrimStart().IsEmpty)
            Log.Warn(LogCat.Events, $"I/O: {input}({parameter}) at {World.Describe(self)}: expected \"{(first == 0 ? "hold [blend [ease]]" : "blend [ease]")}\"; the rest is ignored");
    }

    private static void TurnOff(World world, in IOContext io)
    {
        var self = io.Self;
        if (!world.Has<Camera>(self))
        {
            Log.Warn(LogCat.Events, $"I/O: {Off} at {World.Describe(self)}, which has no camera");
            return;
        }
        bool hasScripted = world.TryGet<ScriptedCamera>(self, out var scripted);
        if (hasScripted) world.Get<ScriptedCamera>(self).Remaining = 0f;

        // The blend out (issue 4n-19): the wire's `blend [ease]`, else the entity's own, else a cut.
        float blend = hasScripted ? scripted.BlendOutTime : 0f;
        var ease = hasScripted ? scripted.BlendEase : Ease.SmoothStep;
        float hold = 0f;
        ReadParameter(Off, io.Parameter, self, first: 1, ref hold, ref blend, ref ease);

        if (!world.Get<Camera>(self).Enabled) return;
        // Structural (the blend may be added), which inputs may be: they are delivered outside any query.
        if (BlendOut(world, self, blend, out var from)) CameraBlends.BeginOut(world, self, from, blend, ease);
        world.Get<Camera>(self).Enabled = false;
        world.FireOutput(self, OnCameraOff, io.Activator);
    }

    // Whether turning `camera` off blends out of it (issue 4n-19), and from where: a blend time, and the
    // view the camera drew last frame (it was winning its target and slot). A camera that was not drawing
    // gives nothing back to blend into, so it is a cut.
    internal static bool BlendOut(World world, Entity camera, float seconds, out CameraView from)
    {
        from = default;
        if (!(seconds > 0f) || !float.IsFinite(seconds)) return false;
        if (!world.Resources.TryGet<CameraViews>(out var views) || views == null) return false;
        int at = views.IndexOf(camera);
        if (at < 0) return false;
        from = views[at];
        return true;
    }
}

// EntityIO phase, before the dispatch: counts down every scripted camera's hold and turns the camera off
// when it runs out (blending out over its BlendOutTime, issue 4n-19), firing OnCameraOff. Before the dispatch so that a hold of N seconds ends on the same
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
                // Its blend out (issue 4n-19), recorded: adding the component is structural, and this is
                // inside a query. Played back at the end of the phase, before the frame the director eases.
                var self = entities.EntityAt(i);
                if (CameraIO.BlendOut(_world, self, s[i].BlendOutTime, out var from))
                    ctx.Commands.Add(self, CameraBlends.Make(from, s[i].BlendOutTime, s[i].BlendEase, blendOut: true));
                // Queued, not run: firing only adds to entity I/O's list, which is safe inside a query.
                _world.FireOutput(self, CameraIO.OnCameraOff, s[i].Activator);
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
