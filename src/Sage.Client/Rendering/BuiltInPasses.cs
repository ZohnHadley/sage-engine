#nullable enable
using Microsoft.Xna.Framework;

namespace Sage.Client;

// The engine's own render passes (issue 4h-1): what the renderer's fixed pass array and the UI system
// drew before, now on the registry like anyone's, so a game orders its passes against them by id. Added
// by ClientModule.Init. There is no Sky pass yet: the sky is the targets' clear colour (4h-5 adds one).

[RenderPass("sage:opaque", RenderStage.Opaque)]
internal sealed class OpaquePass : IRenderPass
{
    public void Draw(RenderContext context) => context.Renderer.DrawSceneRun(context);
}

[RenderPass("sage:alpha_tested", RenderStage.AlphaTested)]
internal sealed class AlphaTestedPass : IRenderPass
{
    public void Draw(RenderContext context) => context.Renderer.DrawSceneRun(context);
}

[RenderPass("sage:transparent", RenderStage.Transparent)]
internal sealed class TransparentPass : IRenderPass
{
    public void Draw(RenderContext context) => context.Renderer.DrawSceneRun(context);
}

[RenderPass("sage:debug", RenderStage.Debug)]
internal sealed class DebugLinesPass : IRenderPass
{
    public void Draw(RenderContext context) => context.Renderer.DrawDebugLines(context);
}

// The game's UI (docs/design/13 §3): whatever was queued into the world's UiDraw this frame, plus the
// crosshair, in one SpriteBatch; drawn only for the screen world (UiRenderSystem empties the others'
// queues, and the queue after this). What UiRenderSystem drew itself before issue 4h-1.
[RenderPass("sage:ui", RenderStage.Overlay)]
internal sealed class UiPass : IRenderPass
{
    private readonly CVar<bool> _crosshair;
    private readonly CVar<int> _testView;
    private bool _fontWarned;

    // Interned once; the font itself is looked up every frame (see `Draw`).
    private static readonly AssetPath FontPath = AssetPath.Intern("textures/font.png");

    public UiPass(CVar<bool> crosshair, CVar<int> testView)
    {
        _crosshair = crosshair;
        _testView = testView;
    }

    // Made in the client's Start, after this pass is added in its Init.
    internal ContentService? Content;
    internal UiResources? Shared;

    public void Draw(RenderContext context)
    {
        if (Content == null || Shared == null) return;
        var world = context.World;
        var ui = world.Resources.Get<UiDraw>();

        // The font is content like anything else, so it is loaded on the first frame that wants it
        // rather than at boot, and a missing one costs the text, not the HUD (13 §3).
        //
        // Asked for **every frame**, not held: a hot reload drops the wrapper and disposes the texture
        // under it, and a pass that kept the old one would draw from a disposed texture — which GL
        // renders as black boxes where the text was, without an error anywhere (F32, R12). The ask is a
        // dictionary lookup on an interned path; the warning is the part that happens once.
        var font = Content.LoadFont(FontPath);
        ui.SetFont(font);
        if (font == null && !_fontWarned)
        {
            _fontWarned = true;
            Log.Warn(LogCat.Render, $"{FontPath} is missing: the HUD draws its bars but no text");
        }

        var viewport = context.Device.Viewport;
        ui.Size = new Vector2(viewport.Width, viewport.Height);

        // The crosshair is the engine's one piece of HUD: combat and the Use action both aim from the
        // centre of the screen, so not drawing it is a handicap rather than a style. Only while a
        // player's rig draws the screen (issues #78, #79: not from a fixed camera, a cutscene or
        // cam_free). In third person too: the over-the-shoulder camera looks along the pawn's aim, so
        // the centre is where the swing goes, a shoulder-width to the side. With a screen open there is
        // nothing to aim at, and a cross floating over an inventory looks like a bug.
        bool screenOpen = world.Resources.TryGet<ScreenStack>(out var screens) && screens!.IsOpen
                       || world.Resources.TryGet<Sage.UI.UiScreenStack>(out var widgets) && widgets!.IsOpen;
        var rig = world.MainViewRig();
        bool aiming = rig == CameraRigKind.FirstPerson || rig == CameraRigKind.ThirdPerson;
        if (_crosshair.Value && aiming && !screenOpen)
        {
            const float Arm = 6f, Thickness = 2f;
            float x = viewport.Width * 0.5f, y = viewport.Height * 0.5f;
            var colour = new Color(255, 255, 255, 150);
            ui.Rect(x - Arm, y - Thickness * 0.5f, Arm * 2f, Thickness, colour);
            ui.Rect(x - Thickness * 0.5f, y - Arm, Thickness, Arm * 2f, colour);
        }

        // `r_testview 2`: the render target the top-down test view drew into, in the top-right corner.
        if (_testView.Value == 2 && context.Renderer.FindTarget(ViewSource.TestTarget) is { } map)
        {
            const int Size = 192, Margin = 12;
            var at = new Rectangle(viewport.Width - Size - Margin, Margin, Size, Size);
            ui.Rect(at.X - 2, at.Y - 2, at.Width + 4, at.Height + 4, new Color(0, 0, 0, 180));
            ui.Image(map, at, Color.White);
        }

        ui.Draw(Shared.Batch, Shared.White, Shared.Scissor);
    }
}
