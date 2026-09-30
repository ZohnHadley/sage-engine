using System.Numerics;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Sandbox;

// The first-person hands (docs/design/13 §3). Everything else the Sandbox's HUD shows — the health bar,
// what is in your hands, the message log, what Use and V do — is the `sandbox:hud` layout over HudView
// since issue #99 (content/data/ui.json; SandboxClientModule opens it as a HUD layer). What is left here
// is not a widget: a frame of a sprite sheet, chosen by where the swing is, sized to the window and drawn
// behind the widget HUD.
//
// FrameUpdate, so it runs at display rate and is queued before the Overlay phase draws the widgets over
// it; after the camera director, so it knows which rig draws this frame's screen (first person only).
[System("sandbox.hud", Phase.FrameUpdate, After = new[] { "sage.camera.director" })]
public sealed class SandboxHud : ISystem
{
    private readonly Query<Transform> _players;
    private readonly UiDraw _ui;
    private readonly RecordStore _records;
    private readonly ContentService _content;

    public SandboxHud(World world, RecordStore records, ContentService content)
    {
        _content = content;
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
        _ui = world.Resources.Get<UiDraw>();
        _records = records;
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        if (_ui.Size.X < 1f) return;                  // before the first frame sized the viewport

        // Not with a window up: the hands belong to aiming (13 §3, F38).
        bool screenOpen = world.Resources.TryGet<ScreenStack>(out var screens) && screens!.IsOpen
#pragma warning disable SAGE0125   // widget screens (#97) are experimental
                       || world.Resources.TryGet<Sage.UI.UiScreenStack>(out var widgets) && widgets!.IsOpen;
#pragma warning restore SAGE0125
        if (screenOpen) return;

        foreach (var player in _players.Entities)
        {
            DrawViewmodel(world, player);
            return;                                   // one local player
        }
    }

    // Your own hands, bottom right, drawn from the frames the attack record points at: at rest, drawn
    // back during the wind-up, and extended on the strike. Daggerfall drew the same three moments.
    private void DrawViewmodel(World world, Entity player)
    {
        // Only looking out of the player's eyes: hands belong to the body the rig is sitting in, not to
        // the editor's free camera or a camera somewhere else (engine issue #78).
#pragma warning disable SAGE0123   // cameras as entities are experimental; this game follows them
        if (world.MainViewRig() != CameraRigKind.FirstPerson) return;
#pragma warning restore SAGE0123
        if (!world.TryGet<Melee>(player, out var melee)) return;
        var attackId = melee.Attack.IsEmpty ? world.Conventions().Attack.Id : melee.Attack;
        if (attackId.IsEmpty) return;
        if (!_records.TryGet(attackId, out AttackRecord attack) || attack.Viewmodel.IsEmpty) return;
        if (!_records.TryGet(attack.Viewmodel, out SpriteSheetRecord sheet) || sheet.Frames.Count == 0) return;

        var texture = _content.LoadTexture(sheet.Texture);
        if (texture == null) return;

        var frame = sheet.Frames[FrameFor(melee, attack, sheet.Frames.Count)];
        if (frame.Rect.Length < 4) return;

        // Sized against the window height, so it sits the same on any resolution, and anchored to
        // the bottom right corner the way Daggerfall held its weapons.
        float scale = _ui.Size.Y * 0.62f / frame.Rect[3];
        float w = frame.Rect[2] * scale, h = frame.Rect[3] * scale;
        var destination = new Rectangle((int)(_ui.Size.X - w * 0.92f), (int)(_ui.Size.Y - h * 0.94f), (int)w, (int)h);
        _ui.Image(texture, destination, Color.White, new Rectangle(frame.Rect[0], frame.Rect[1], frame.Rect[2], frame.Rect[3]));
    }

    // Frame 0 is at rest and everything after it is the swing, played across the wind-up and the
    // recovery. That way a three-frame placeholder and Daggerfall's six-frame weapon both work, and
    // what you see is the simulation's own phases (16 §3.2) rather than an animation beside them.
    private static int FrameFor(in Melee melee, AttackRecord attack, int frameCount)
    {
        int swing = frameCount - 1;
        if (swing < 1 || melee.Phase == MeleePhase.Ready) return 0;

        float total = MathF.Max(attack.WindupTime + attack.RecoverTime, 0.01f);
        float elapsed = melee.Phase == MeleePhase.Windup ? melee.Timer : attack.WindupTime + melee.Timer;
        int index = (int)(elapsed / total * swing);
        return 1 + System.Math.Clamp(index, 0, swing - 1);
    }
}
