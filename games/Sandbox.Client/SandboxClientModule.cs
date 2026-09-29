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
    public void Init(ModuleContext ctx) { }

    public void Start(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _content = ctx.Get<ContentService>();   // textures for the HUD's viewmodel (13 §3)
        _actions = ctx.Engine.Actions;
        _screens = ctx.Get<ScreenRegistry>();
    }

    private ActionRegistry? _actions;
    private ScreenRegistry? _screens;

    public void OnWorldCreated(World world)
    {
        world.AddSystem(new SandboxHud(world, _records!, _content!));   // 13 §3

        // Which screens this game has, and what opens them (13 §3, F38). The engine draws and drives
        // them; saying `I` is the bag and `B` is the spellbook is the game's decision, the same way
        // the health bar's shape is.
        var screens = world.Resources.Get<ScreenStack>();
        screens.Bind(_actions!.Get("Inventory"), new InventoryScreen());
        screens.Bind(_actions!.Get("Spellbook"), new SpellbookScreen());
        // The spellmaker and the journal are the RPG kit's screens (16 §3.3, issue #27): what a
        // spellmaker is belongs to the feature, not to this game, and the kit registers them by id.
        // Binding them to a key is still the game's call.
        screens.Bind(_actions!.Get("Spellmaker"), _screens!.Create(RpgKitClientModule.Spellmaker)!);
        screens.Bind(_actions!.Get("Journal"), _screens!.Create(RpgKitClientModule.Journal)!);
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
