#nullable enable
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Commands phase: the `ToggleView` action switches the player's camera between first and third person
// (issue #79). The character plugin registers the action (in Init, with its others) and installs this.
//
// **Why the rigs' Enabled flags, not a mode.** The player's camera carries both rigs, and the toggle turns
// one off and the other on. Each rig stays a whole thing on its own — a game's chase camera is a
// ThirdPersonRig with nothing else, a security monitor's first-person feed a FirstPersonRig — the third
// person settings (distance, shoulder, probe) survive the switch because nothing is removed, and there is
// no structural change, so the switch lands this tick with nothing deferred. What the player chose is the
// two flags, saved on the camera with everything else about it.
//
// **Why here.** It is a button press, and presses reach the simulation as the tick's PlayerCommand (08
// §3.4), which the controller copies into the pawn's PawnIntent: read from the followed pawn's intent in
// Commands, after the controller and after a scripted camera's input lock (#80) — so a cut that holds the
// player's buttons holds this one too — the frame after the key goes down draws the other view. A camera
// is not simulation state, but the intent is the only place the press is, and reading it changes nothing:
// the pawn, its intent and combat are untouched.
[Experimental("SAGE0123")]
[System(Id, Phase.Commands, After = new[] { "?sage.character.player_control", "?sage.camera.input_lock" })]
public sealed class ToggleViewSystem : ISystem
{
    public const string Id = "sage.camera.toggle_view";

    // The button that switches the player's view; the character plugin registers it.
    public const string Action = "ToggleView";

    private readonly World _world;
    private readonly ActionId _toggle;
    private readonly Query<FirstPersonRig, ThirdPersonRig> _cameras;

    public ToggleViewSystem(World world, ActionRegistry actions)
    {
        _world = world;
        _toggle = actions.Get(Action);
        _cameras = world.Query<FirstPersonRig, ThirdPersonRig>().AllTags(Tags.Get<PlayerCamera>());
    }

    public void Run(in SystemContext ctx)
    {
        if (!_toggle.IsValid) return;
        foreach (var (firsts, thirds, _) in _cameras.Chunks)
        {
            var first = firsts.Span;
            var third = thirds.Span;
            for (int i = 0; i < first.Length; i++)
            {
                var pawn = first[i].Follow.IsNull ? third[i].Follow : first[i].Follow;
                if (_world.TryGet<PawnIntent>(pawn, out var intent) && intent.Pressed.Has(_toggle))
                    Flip(ref first[i], ref third[i]);
            }
        }
    }

    // First person to third and back, on every player camera. Also what a game's own code (a menu option,
    // a cutscene that wants the body in view) calls.
    public static void Toggle(World world)
    {
        foreach (var (firsts, thirds, _) in world.Query<FirstPersonRig, ThirdPersonRig>().AllTags(Tags.Get<PlayerCamera>()).Chunks)
        {
            var first = firsts.Span;
            var third = thirds.Span;
            for (int i = 0; i < first.Length; i++) Flip(ref first[i], ref third[i]);
        }
    }

    private static void Flip(ref FirstPersonRig first, ref ThirdPersonRig third)
    {
        bool toThird = !third.Enabled;
        third.Enabled = toThird;
        first.Enabled = !toThird;
    }
}
