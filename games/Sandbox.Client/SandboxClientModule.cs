using System.Numerics;

namespace Sandbox;   // the Sage.* and Friflo.Engine.ECS usings come from games/Directory.Build.props

// The Sandbox's client half (docs/design/01 §3.1, TODO R15): the parts of the game that need a
// screen. Everything else — the scene, the rules, spawning, the combat log — is in `Sandbox`, which
// references only the base engine, so it can be ticked headlessly in a test.
//
// The split is the one the engine review asked games to follow (item 6). It costs a second assembly
// and buys two things: a game's simulation becomes testable without MonoGame, and the boundary is
// enforced by the compiler rather than by remembering. A dedicated server would run `Sandbox` and not
// this.
//
// It is a plain `IModule`, not an `IGameModule` — there is exactly one of those per game, and it is
// the simulation. The host loads this through `game.json`'s `modules.add`.
[Plugin("sandbox.client", "0.1.0")]
public sealed class SandboxClientModule : IModule
{
    private ContentService? _content;
    private RecordStore? _records;

    // The kit's client half registers the screens this binds (issue #27).
    public IReadOnlyList<Type> Dependencies => new[] { typeof(ClientModule), typeof(RpgKitClientModule) };

    // `box_mesh` (BoxMeshPart, below) is declared, so Init has nothing to register (issue #17).
    //
    // The Sandbox's own settings: sun shadows on (phase 4h's exit, engine issue 4h-7), where the engine
    // leaves them off. A value set here, in Init, is the game's default: config.cfg is read after Init and
    // the command line after that, so `+r_shadows 0` still turns them off.
    //
    // `r_post_haze` switches the Sandbox's depth haze (content/data/post.json, engine issue #316), the
    // sample of a post effect that reads the scene's depth; it needs `r_post 1` like the engine's effects.
    public void Init(ModuleContext ctx)
    {
        if (ctx.Engine.CVars.Find("r_shadows") is { } shadows && !shadows.TrySet("1", out var error))
            Log.Warn(LogCat.Render, $"Sandbox: r_shadows 1 was refused: {error}");
        ctx.Engine.CVars.Register("r_post_haze", true, CVarFlags.Archive,
            "The Sandbox's depth haze (sandbox:haze), a post effect that reads the scene's depth; needs r_post 1.");
    }

    public void Start(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _content = ctx.Get<ContentService>();   // textures for the HUD's viewmodel (13 §3)
        _actions = ctx.Engine.Actions;
        _screens = ctx.Get<ScreenRegistry>();
    }

    private ActionRegistry? _actions;
    private ScreenRegistry? _screens;

    // The HUD's screen record (content/data/ui.json): the layout over HudView.
    private static readonly RecordId Hud = new("sandbox", "hud");

    public void OnWorldCreated(World world)
    {
        world.AddSystem(new SandboxHud(world, _records!, _content!));   // the first-person hands (13 §3)

        // Which screens this game has, and what opens them (13 §3, F38). The engine draws and drives
        // them; saying `I` is the bag and `B` is the spellbook is the game's decision, the same way
        // the health bar's shape is.
        var screens = world.Resources.Get<ScreenStack>();
        screens.Bind(_actions!.Get("Inventory"), new InventoryScreen());
        screens.Bind(_actions!.Get("Spellbook"), new SpellbookScreen());
        // The spellmaker is the RPG kit's screen (16 §3.3, issue #27): what a spellmaker is belongs to the
        // feature, not to this game, and the kit registers it by id. Binding it to a key is the game's call.
        screens.Bind(_actions!.Get("Spellmaker"), _screens!.Create(RpgKitClientModule.Spellmaker)!);

        // The widget screens (13 "As built (drawing)", #97, and "As built (the HUD, journal, map and
        // menus)", #99), built from records in content/data/ui.json: `sandbox:status` (C), the kit's
        // journal (J) and map (N), and the main menu (F10) with the
        // saves to load. The HUD is one too, opened once and for good as a layer that never takes a key.
        // Their Close buttons are the one thing a layout cannot say, so it is said here.
#pragma warning disable SAGE0125   // widget screens are Phase 4c's experimental UI (MAKING_A_GAME §10b)
        var widgets = world.Resources.Get<Sage.UI.UiScreenStack>();
        widgets.Bind(_actions!.Get("Status"), new RecordId("sandbox", "status"));
        // The journal and the map are the RPG kit's own screens (issue #349): J and N.
        widgets.Bind(_actions!.Get("Journal"), RpgKitModule.JournalScreen);
        widgets.Bind(_actions!.Get("Map"), RpgKitModule.MapScreen);
        // F10 is the RPG kit's pause menu (issue #342): Resume, Save, Load, Options, Quit, the world stood
        // still while it is open. The Sandbox's own menu of #99 is still `ui_open main_menu`.
        widgets.Bind(_actions!.Get("MainMenu"), RpgKitModule.PauseScreen);
        // The RPG kit's rest screen (4g-7), its own layout from the kit's content: T.
        widgets.Bind(_actions!.Get("Rest"), RpgKitModule.RestScreen);
        widgets.TooltipStyle = "sandbox:ui_tooltip";
        widgets.OpenHud(Hud, new Sage.UI.UiBindContext(world));
        widgets.Activated += (layer, widget) =>
        {
            if (widget.Name == "close") widgets.Close(layer);
        };
#pragma warning restore SAGE0125
    }
}

// "box_mesh": { "size": [1, 1, 1], "material": "crate" } — a plain box mesh built at run time. That
// needs the renderer, which is why this part lives on the client side and the simulation only declares
// it optional (F31): headless, a crate is a collider with no mesh, which is exactly right.
//
// The renderer comes from the context when the part is applied, not from a module field set in Start
// (issue #17): a part is a declaration, with no module instance behind it to capture.
[PrefabPart("box_mesh")]
public sealed class BoxMeshPart : IPrefabPart
{
    [Property(Min = 0, Unit = "m", Tooltip = "Full extents of the box")]
    public Vector3 Size;
    [RecordRef("material"), Property(Tooltip = "What the box is drawn with")]
    public RecordId Material;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Size == Vector3.Zero) { ctx.Error("needs a \"size\""); return; }
        var renderer = ctx.Get<Renderer>();
        ctx.World.Add(ctx.Entity, new MeshRenderer { Handle = renderer.CreateBox(Size, World.Describe(ctx.Entity)), Material = Material });
    }
}
