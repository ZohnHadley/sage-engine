#nullable enable
using System.Diagnostics.CodeAnalysis;

namespace Sage.UI;

// The crosshair as a widget HUD (docs/design/13 §3, issue #350): the engine's one piece of HUD, which the
// client used to draw by hand in its UI pass. Combat and the Use action both aim from the centre of the
// screen, so not drawing one is a handicap rather than a style. The screen is `sage:crosshair` in the
// engine's content (engine_content/data/ui.json), which a game restyles or re-lays out with a patch; the
// client opens it as a HUD layer in every world.
//
// Shown only while a player's rig draws the screen in first or third person (issues #78, #79: not from a
// fixed camera, a cutscene or cam_free) — in third person too, since the over-the-shoulder camera looks
// along the pawn's aim — with `ui_crosshair` on, and never with a window up: a cross floating over an
// inventory looks like a bug.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
[ViewModel("ui_crosshair")]
public sealed class CrosshairView : IViewModel
{
    // The screen record the client opens as a HUD layer.
    public static readonly RecordId Screen = new("sage", "crosshair");

    private Engine? _engine;
    private CVar<bool>? _enabled;

    public bool Shown { get; private set; }

    public void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) { Shown = false; return; }
        if (!ReferenceEquals(world.Engine, _engine))
        {
            _engine = world.Engine;
            _enabled = _engine?.CVars.Find(UiModule.CrosshairCVar) as CVar<bool>;
        }
#pragma warning disable SAGE0123   // cameras as entities are experimental; the crosshair follows which rig has the screen
        var rig = world.MainViewRig();
#pragma warning restore SAGE0123
        bool aiming = rig == CameraRigKind.FirstPerson || rig == CameraRigKind.ThirdPerson;
        bool windowUp = world.Resources.TryGet<UiScreenStack>(out var stack) && stack is { IsOpen: true };
        Shown = (_enabled?.Value ?? true) && aiming && !windowUp;
    }
}
