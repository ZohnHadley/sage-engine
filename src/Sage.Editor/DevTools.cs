#nullable enable
using System;
using Microsoft.Xna.Framework;
using MonoGame.ImGuiNet;

namespace sage_engine;

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
    private readonly Game _game;
    private readonly Engine _engine;
    private readonly ImGuiRenderer _gui;
    private readonly DevCamera _camera;
    private readonly DevConsoleWindow _console;
    private readonly StatOverlay _stats;
    private readonly CVar<bool> _camFree;
    private readonly CVar<bool> _showEntities;
    private readonly EditorSelection _selection = new();

    private EditorDocument? _document;
    private EditorUI? _menu;
    private EntityOutlinerWindow? _outliner;
    private EntityInspectorWindow? _inspector;

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
            "Fly the editor camera even while a player pawn owns the view (16 §3.2).");
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
            if (a.Count >= 4 && float.TryParse(a[3], out float yaw))
                _camera.SetLook(yaw, a.Count >= 5 && float.TryParse(a[4], out float pitch) ? pitch : 0f);
            Log.Info(LogCat.Console, $"camera at {_camera.Position}");
        });
    }

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
        _document = new EditorDocument(_engine);
        _outliner = new EntityOutlinerWindow(world, _selection);
        _inspector = new EntityInspectorWindow(world, _engine.Components, _selection, _document);
        _menu = new EditorUI(world, _document);

        // The free camera keeps its own position, so it has to be told when the world moves under it
        // (R6): without this, `cam_free` after a rebase leaves it a sector behind what it was looking at.
        world.Origin().Rebased += offset => _camera.Position += new Vector3(offset.X, offset.Y, offset.Z);

        RegisterDocumentCommands(world);
    }

    // Every menu item is a console command as well. That is a rule rather than a convenience: a menu a
    // script cannot press is a feature that cannot be checked the way the rest of this engine is.
    private void RegisterDocumentCommands(World world)
    {
        var cvars = _engine.CVars;
        var document = _document!;

        cvars.RegisterCommand("doc_new", CVarFlags.DevOnly, "doc_new: start an empty placements document.",
            _ => document.New(world, NamespaceOfGame()));

        cvars.RegisterCommand("doc_open", CVarFlags.DevOnly, "doc_open <id>: open a placements document.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "doc_open <id>   (see rec_list placements)"); return; }
            var id = _engine.Records.Resolve("placements", a[0]);
            if (!id.IsEmpty) document.Open(world, id);
        });

        cvars.RegisterCommand("doc_save", CVarFlags.DevOnly, "doc_save: write the open document back to its file.",
            _ => document.Save(world));

        cvars.RegisterCommand("doc_close", CVarFlags.DevOnly, "doc_close: close the document, removing what it placed.",
            _ => document.Close(world));

        cvars.RegisterCommand("doc_status", CVarFlags.None, "doc_status: what is open, and whether it is saved.",
            _ => Log.Info(LogCat.Console, document.IsOpen
                ? $"{document.Title} — {(document.Path.Length > 0 ? document.Path : "never saved")}"
                : "no document open"));

        cvars.RegisterCommand("ent_select", CVarFlags.DevOnly, "ent_select <name>: select an entity for the inspector.", a =>
        {
            if (a.Count == 0) { _selection.Clear(); Log.Info(LogCat.Console, "selection cleared"); return; }
            var entity = world.FindByName(a.Rest);
            if (entity.IsNull) { Log.Warn(LogCat.Console, $"ent_select: no entity named '{a.Rest}'"); return; }
            _selection.Select(entity);
            Log.Info(LogCat.Console, $"selected {World.Describe(entity)}");
        });
    }

    // The free camera runs at display rate, and gives the camera back to a rig that claimed it.
    public void Update(GameTime time, ActiveCamera camera)
    {
        _camera.Update(time);
        camera.RigEnabled = !_camFree.Value;
        if (camera.DrivenByRig) return;

        camera.Position = _camera.Position.ToNumerics();
        camera.Rotation = _camera.Rotation.ToNumerics();
    }

    public void Draw(GameTime time)
    {
        _gui.BeginLayout(time);
        _menu?.Draw(_game);
        if (_showEntities.Value)
        {
            _outliner?.Draw();
            _inspector?.Draw();
        }
        _console.Draw();
        _stats.Draw();
        _gui.EndLayout();
    }

    private string NamespaceOfGame()
    {
        foreach (var mount in _engine.Vfs.Mounts)
            if (mount.RecordNamespace != "sage") return mount.RecordNamespace;
        return "sage";
    }

    public void Dispose() { }
}
