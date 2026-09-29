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
// §3.4): read in Commands like every other action, the frame after the key goes down draws the other
// view. A camera is not simulation state, but the command is the only place the press is, and reading
// it here costs nothing and changes nothing else — the pawn, its intent and combat are untouched.
[Experimental("SAGE0123")]
[System(Id, Phase.Commands)]
public sealed class ToggleViewSystem : ISystem
{
    public const string Id = "sage.camera.toggle_view";

    // The button that switches the player's view; the character plugin registers it.
    public const string Action = "ToggleView";

    private readonly ActionId _toggle;
    private readonly Query<FirstPersonRig, ThirdPersonRig> _cameras;

    public ToggleViewSystem(World world, ActionRegistry actions)
    {
        _toggle = actions.Get(Action);
        _cameras = world.Query<FirstPersonRig, ThirdPersonRig>().AllTags(Tags.Get<PlayerCamera>());
    }

    public void Run(in SystemContext ctx)
    {
        if (!_toggle.IsValid || !ctx.World.Resources.TryGet<PlayerInput>(out var input) || input == null) return;
        if (!input.HasCommand || !input.Command.Pressed.Has(_toggle)) return;
        Flip(_cameras);
    }

    // First person to third and back, on every player camera. Also what a game's own code (a menu option,
    // a cutscene that wants the body in view) calls.
    public static void Toggle(World world) =>
        Flip(world.Query<FirstPersonRig, ThirdPersonRig>().AllTags(Tags.Get<PlayerCamera>()));

    private static void Flip(Query<FirstPersonRig, ThirdPersonRig> cameras)
    {
        foreach (var (firsts, thirds, _) in cameras.Chunks)
        {
            var first = firsts.Span;
            var third = thirds.Span;
            for (int i = 0; i < first.Length; i++)
            {
                bool toThird = !third[i].Enabled;
                third[i].Enabled = toThird;
                first[i].Enabled = !toThird;
            }
        }
    }
}
