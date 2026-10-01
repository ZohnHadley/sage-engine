#nullable enable
using System;
using System.Linq;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.ImGuiNet;

namespace Sage.Editor;

// Everything the host draws for a developer, in one place (docs/design/15 §3, TODO F28).
//
// **This is what a shipped game does not have.** The host used to build the ImGui renderer, the free
// camera, the console window, the overlays and the editor's windows itself, with the editor's types
// spread through its fields — so "a game host without the editor" was a comment rather than a fact. Now
// the host has one field of this type, guarded by `SAGE_DEV`, and a Shipping build references neither
// this assembly nor ImGui at all.
//
// It is not a separate executable, which is what 15 §3 sketched. That is worth doing when the editor
// wants what only a second host can give it — its own worlds for play-in-editor, its own input maps, a
// window laid out for editing rather than for playing — and none of those are here yet. What a separate
// exe would buy *today* is exactly what this buys: nothing of the editor in a shipped game.
public sealed class DevTools : IDisposable
{
    // The editor viewport's render target (issue #81): what a separate editor host's viewport will be
    // (REDESIGN §4.6), drawn by a camera entity like any other view and shown in an ImGui window.
    public const string ViewportTarget = "editor";
    private const int ViewportWidth = 480, ViewportHeight = 270;
    private const string FreeCameraName = "editor free camera", ViewportCameraName = "editor viewport camera";

    private readonly Game _game;
    private readonly Engine _engine;
    private readonly ImGuiRenderer _gui;
    private readonly DevCamera _camera;
    private readonly DevConsoleWindow _console;
    private readonly StatOverlay _stats;
    private readonly CVar<bool> _camFree;
    private readonly CVar<bool> _viewport;
    private readonly CVar<bool> _showEntities;
    private readonly EditorSelection _selection = new();

    // The camera entities the free camera drives (DebugCamera): one on the screen, overriding it while
    // `cam_free` is on and idle (drawing only where nothing else does) otherwise; one into the viewport's
    // target while `ed_viewport` is on. Spawned on first use and again if a load or a scene swept them.
    private Entity _freeCamera, _viewportCamera;
    private bool _wasShown;   // cam_free or the viewport was on last update
    private bool _placed;   // cam_set moved it since the last update
    private Renderer? _renderer;
    private RenderTarget2D? _boundTarget;   // the viewport texture ImGui has an id for
    private IntPtr _viewportTexture;
    private bool _viewportFocused, _viewportHovered;   // last frame's, for the free camera's input

    private World? _world;
    private EditDocument? _document;
    private EditorUI? _menu;
    private EntityOutlinerWindow? _outliner;
    private EntityInspectorWindow? _inspector;

    // The editor mode (`-edit`, issue #219): set once, by BeginEditing, for the life of the host.
    private bool _editing;
    private readonly EditorLayout _layout = new();
    private readonly LogPanel _log = new();

    public DevTools(Game game, Engine engine, InputDevices devices, InputActions actions)
    {
        _game = game;
        _engine = engine;
        var cvars = engine.CVars;

        _gui = new ImGuiRenderer(game);
        _camera = new DevCamera(devices, actions, new Vector3(0, 0, 0), new Vector3(0, 0, 0)) { Position = new Vector3(0, 0, 1) };
        _console = new DevConsoleWindow(cvars, engine.Core);
        _stats = new StatOverlay(cvars, engine.Core);

        _camFree = cvars.Register("cam_free", false, CVarFlags.DevOnly,
            "Fly the editor camera even while a player pawn owns the view (16 §3.2): it overrides every camera until turned off.");
        _viewport = cvars.Register("ed_viewport", false, CVarFlags.DevOnly,
            "Show the editor viewport: the free camera's view drawn into the render target 'editor' and shown in a window (issue #81).");
        _showEntities = cvars.Register("ui_entities", true, CVarFlags.DevOnly | CVarFlags.Archive,
            "Show the outliner and inspector windows. They allocate per listed entity per frame (TODO #41).");

        cvars.RegisterCommand("cam_set", CVarFlags.DevOnly,
            "cam_set <x> <y> <z> [yaw] [pitch]: place the editor camera (degrees).", a =>
        {
            if (a.Count < 3 || !float.TryParse(a[0], out float x) || !float.TryParse(a[1], out float y)
                            || !float.TryParse(a[2], out float z))
            {
                Log.Warn(LogCat.Console, "cam_set <x> <y> <z> [yaw] [pitch]");
                return;
            }
            _camera.Position = new Vector3(x, y, z);
            _placed = true;   // a `cam_free 1` in the same frame keeps this rather than starting from the screen's view
            if (a.Count >= 4 && float.TryParse(a[3], out float yaw))
                _camera.SetLook(yaw, a.Count >= 5 && float.TryParse(a[4], out float pitch) ? pitch : 0f);
            Log.Info(LogCat.Console, $"camera at {_camera.Position}");
        });

        cvars.RegisterCommand("ed_layout", CVarFlags.DevOnly, "ed_layout: put the editor's panels back where they started (-edit).", _ =>
        {
            if (!_editing) { Log.Warn(LogCat.Console, "ed_layout: only in the editor (start the host with -edit)"); return; }
            _layout.Reset();
        });

        RegisterDocumentCommands();
    }

    public bool IsEditing => _editing;

    // What ImGui is doing with the mouse and keyboard this frame: with no dev tools, nothing is, which
    // is why the host holds these as constants in a Shipping build.
    public bool WantsMouse => ImGuiNET.ImGui.GetIO().WantCaptureMouse;
    public bool WantsKeyboard => ImGuiNET.ImGui.GetIO().WantCaptureKeyboard;

    public void LoadContent() => _gui.RebuildFontAtlas();
    public void EndFrame(float frameSeconds) => _stats.EndFrame(frameSeconds);

    public bool ConsoleIsOpen => _console.IsOpen;
    public void CloseConsole() => _console.Close();
    public void ToggleConsole() => _console.Toggle();

    // The editor's document and windows belong to a world, because a document is opened *into* one.
    public void OnWorldCreated(World world)
    {
        _world = world;
        _document = new EditDocument(world);
        // A command re-spawns what it changed: the selection follows its placement to the new entity.
        _document.Respawned += (old, now) => { if (_selection.Is(old)) { if (now.IsNull) _selection.Clear(); else _selection.Select(now); } };
        _outliner = new EntityOutlinerWindow(world, _selection);
        _inspector = new EntityInspectorWindow(world, _engine.Components, _selection, _document);
        _menu = new EditorUI(_document, _camFree, _viewport);
        _freeCamera = default;
        _viewportCamera = default;
        // The client's renderer, for the viewport's target: there is none in a host without the client.
        _renderer ??= _engine.Modules.Modules.OfType<ClientModule>().FirstOrDefault()?.Renderer;

        // The free camera keeps its own position, so it has to be told when the world moves under it
        // (R6): without this, `cam_free` after a rebase leaves it a sector behind what it was looking at.
        world.Origin().Rebased += offset => _camera.Position += new Vector3(offset.X, offset.Y, offset.Z);
    }

    // The editor mode (`-edit`, issue #219), once the edit world exists and OnWorldCreated has seen it: the
    // free camera on the screen for good, flying on the Editor context's EditorMove, the docked layout,
    // and `target`'s document open.
    public void BeginEditing(EditTarget target)
    {
        if (_world == null || _document == null) throw new InvalidOperationException("BeginEditing needs the edit world: call OnWorldCreated first");
        _editing = true;
        _outliner = new EntityOutlinerWindow(_world, _selection, EditorLayout.OutlinerTitle);
        if (_menu != null) _menu.Editing = true;
        _camera.EditorMove = _engine.Actions.Get("EditorMove");
        if (!_console.IsOpen) _console.Toggle();   // docked beside the log; `~` still closes it

        // Somewhere to stand: the scene's player start at eye height, else a little back from the origin.
        if (_world.PlayerStart() is { } start)
        {
            _camera.Position = new Vector3(start.X, start.Y + 1.7f, start.Z);
            _camera.SetLook(0f, -10f);
        }
        else
        {
            _camera.Position = new Vector3(0, 3, 8);
            _camera.SetLook(0f, -15f);
        }
        _placed = true;

        // **The one place the editor's document is opened from a launch line**: the EditDocument (#217)
        // bound to this world, whose edits are commands and which `doc_*` / `ed_undo` act on from here on.
        if (!target.Document.IsEmpty) _document.Open(target.Document);
        Log.Info(LogCat.Editor, $"Editing '{_world.Name}': scene {(target.Scene.IsEmpty ? "(none)" : target.Scene.ToString())}, " +
                                $"document {(_document.IsOpen ? _document.Title : "(none: File > New or doc_open)")}");
    }

    // The status bar's line: the document, whether it is saved, and the selection.
    private string StatusLine()
    {
        var document = _document;
        string doc = document is { IsOpen: true }
            ? $"{document.Id}  {(document.Dirty ? "modified" : "saved")}"
            : "no document";
        var world = _world;
        string selected = world != null && !_selection.Entity.IsNull && world.IsAlive(_selection.Entity)
            ? World.Describe(_selection.Entity) : "nothing selected";
        return $"{doc}   |   {selected}   |   {world?.Name} ({world?.EntityCount ?? 0} entities)   |   camera {_camera.Position.X:F1} {_camera.Position.Y:F1} {_camera.Position.Z:F1}";
    }

    // Every menu item is a console command as well. That is a rule rather than a convenience: a menu a
    // script cannot press is a feature that cannot be checked the way the rest of this engine is.
    //
    // Registered once, with the tools (issue #19): they act on whichever world was created last. They
    // used to be registered in OnWorldCreated, after the Register stage, which a second world would
    // have turned into a "command already registered" error.
    private void RegisterDocumentCommands()
    {
        var cvars = _engine.CVars;

        // The document's commands (doc_*, ed_undo, ed_redo, ed_history) are Sage.Editing's, so tests press
        // them too (issue #217).
        EditorCommands.Register(cvars, () => _document);

        cvars.RegisterCommand("ent_select", CVarFlags.DevOnly, "ent_select <name>: select an entity for the inspector.", a =>
        {
            if (_world is not { } world) { Log.Warn(LogCat.Console, "no world yet"); return; }
            if (a.Count == 0) { _selection.Clear(); Log.Info(LogCat.Console, "selection cleared"); return; }
            var entity = world.FindByName(a.Rest);
            if (entity.IsNull) { Log.Warn(LogCat.Console, $"ent_select: no entity named '{a.Rest}'"); return; }
            _selection.Select(entity);
            Log.Info(LogCat.Console, $"selected {World.Describe(entity)}");
        });
    }

    // The free camera runs at display rate, before the frame's FrameUpdate, so the director sees this
    // frame's pose. It no longer writes ActiveCamera (issue #81): it drives a camera entity, which the
    // director resolves like any other — over every camera while `cam_free` is on, under every camera
    // otherwise — and mirrors into ActiveCamera when it has the screen.
    public void Update(GameTime time)
    {
        var world = _world;
        if (world == null) { _camera.Update(time); return; }

        bool free = _camFree.Value || _editing;   // the editor's screen is the free camera's
        // It flies while something shows it: the screen (cam_free, or no other camera there last frame)
        // or the viewport with the mouse or focus on it.
        bool onScreen = world.TryGetMainView(out var main) && !main.Entity.IsNull && main.Entity == _freeCamera;
        _camera.Active = free || onScreen || (_viewport.Value && _viewportFocused);
        _camera.MouseOverViewport = _viewport.Value && _viewportHovered;
        _camera.Update(time);
        // Shown again (cam_free or the viewport turned on): fly from the view that has the screen, not from
        // wherever the free camera was left — unless cam_set just put it somewhere.
        bool shown = free || _viewport.Value;
        if (shown && !_wasShown && !_placed && world.TryGetMainView(out var screen) && screen.Entity != _freeCamera)
        {
            _camera.Position = screen.Position;
            _camera.LookAlong(screen.Rotation);
        }
        _wasShown = shown;
        _placed = false;

        var position = _camera.Position.ToNumerics();
        var rotation = _camera.Rotation.ToNumerics();
        if (!world.IsAlive(_freeCamera)) _freeCamera = DebugCamera.Spawn(world, FreeCameraName);
        DebugCamera.Drive(world, _freeCamera, position, rotation, overriding: free);

        if (_viewport.Value && _renderer != null)
        {
            if (_renderer.FindTarget(ViewportTarget) is not { } target || target.Width != ViewportWidth || target.Height != ViewportHeight)
                _renderer.DeclareTarget(ViewportTarget, ViewportWidth, ViewportHeight);
            if (!world.IsAlive(_viewportCamera)) _viewportCamera = DebugCamera.Spawn(world, ViewportCameraName, ViewportTarget);
            DebugCamera.Drive(world, _viewportCamera, position, rotation, overriding: true);   // the target is the editor's own
        }
        else if (world.IsAlive(_viewportCamera) && world.Get<Camera>(_viewportCamera).Enabled)
        {
            world.Get<Camera>(_viewportCamera).Enabled = false;   // nothing draws into the target while it is closed
        }
    }

    public void Draw(GameTime time)
    {
        _gui.BeginLayout(time);
        if (_editing)
        {
            DrawEditor();
            _gui.EndLayout();
            return;
        }
        _menu?.Draw(_game);
        if (_showEntities.Value)
        {
            _outliner?.Draw();
            _inspector?.Draw();
        }
        DrawViewport();
        _console.Draw();
        _stats.Draw();
        _gui.EndLayout();
    }

    // The editor mode's frame (issue #219): the menu, the dock space, its panels, the status bar.
    private void DrawEditor()
    {
        _menu?.Draw(_game);
        _layout.BeginFrame();
        _outliner?.Draw();
        _inspector?.Draw();
        _log.Draw();
        _console.Draw();
        DrawViewport();
        _stats.Draw();
        _layout.DrawStatusBar(StatusLine());
    }

    // The editor viewport (issue #81): the render target the viewport camera drew this frame (views into
    // targets draw before the screen, and this is after both), shown through ImGui's texture binding.
    // The prerequisite REDESIGN §4.6's editor host builds on: an editor's view of a world is a camera
    // entity and a named target, not a special renderer path.
    private void DrawViewport()
    {
        _viewportHovered = _viewportFocused = false;
        if (!_viewport.Value || _renderer == null) return;
        var target = _renderer.FindTarget(ViewportTarget);
        if (target == null) return;
        if (!ReferenceEquals(target, _boundTarget))
        {
            // Re-declared at another size, or released and made again: a new texture, so a new id.
            if (_boundTarget != null) _gui.UnbindTexture(_viewportTexture);
            _viewportTexture = _gui.BindTexture(target);
            _boundTarget = target;
        }

        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(display.X - ViewportWidth - 24, 28), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(ViewportWidth + 16, ViewportHeight + 58), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("Viewport", ref open))
        {
            // As wide as the window allows, at the target's shape.
            float width = MathF.Max(ImGui.GetContentRegionAvail().X, 16f);
            ImGui.Image(_viewportTexture, new System.Numerics.Vector2(width, width * target.Height / target.Width));
            _viewportHovered = ImGui.IsItemHovered();
            _viewportFocused = ImGui.IsWindowFocused();
            ImGui.TextDisabled(_camFree.Value ? "the free camera, on the screen too (cam_free 1)"
                                              : "the free camera: click, WASD, right-drag; cam_free 1: on the screen");
        }
        ImGui.End();
        if (!open) _viewport.Value = false;
    }

    public void Dispose()
    {
        if (_boundTarget != null) _gui.UnbindTexture(_viewportTexture);
        _boundTarget = null;
    }
}
