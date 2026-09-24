#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// What the host offers client modules (ModuleManager.ProvideHostService): the MonoGame Game and its
// graphics device. Provided before Start, so client modules can create GPU resources there.
public sealed class ClientHost
{
    public ClientHost(Game game) { Game = game; }
    public Game Game { get; }
    public GraphicsDevice GraphicsDevice => Game.GraphicsDevice;
}

// The client engine module (a default module; docs/design/01 §3.1): registers the client record types
// and the engine's input actions, provides ContentService and Renderer, and per world installs the
// RenderSnapshot and the Extract/Render systems (06 §3.1).
public sealed class ClientModule : IModule
{
    private ContentService? _content;
    private ClientHost? _host;
    private UiResources? _ui;
    private Renderer? _renderer;
    private RecordStore? _records;
    private CVar<bool>? _debugDraw;
    private CVar<bool>? _crosshair;
    private CVar<bool>? _assetHotReload;
    private AssetHotReload? _watcher;
    private InputActions? _actions;
    private AudioMixer? _mixer;
    private IAudioBackend? _audio;
    private CVar<bool>? _soundEnabled;
    private InputDevices? _devices;
    private ActionRegistry? _actionIds;

    public void Init(ModuleContext ctx)
    {
        ctx.Engine.Records.Register<MaterialRecord>();
        ctx.Engine.Records.Register<InputMapRecord>();
        ctx.Engine.Records.Register<SpriteSheetRecord>();
        ctx.Engine.Records.Register<SoundRecord>();

        // A thing that hums: `"audio": { "sound": "fire_loop", "loop": true }` on any prefab. The
        // component is the engine's, so a headless run carries it and simply never plays it.
        ctx.Engine.Prefabs.Register("audio", (world, entity, options, where) =>
        {
            var o = PrefabParts.Read<AudioOptions>(world, options, "audio", where);
            if (o.Sound.IsEmpty) { Log.Error(LogCat.Records, $"{where}: audio needs a \"sound\""); return; }
            world.Add(entity, new AudioSource { Sound = o.Sound, Loop = o.Loop, Volume = o.Volume <= 0f ? 1f : o.Volume });
        });

        // The client's own actions (08 §3.2); the gameplay ones (Move, Jump, Crouch...) belong to the
        // gameplay module, so a headless server registers the same ids. Bindings for all of them are
        // in engine_content/data/input.json.
        var actions = ctx.Engine.Actions;
        actions.Register("Look", ActionKind.Axis2D);
        actions.Register("Menu", ActionKind.Button);
        actions.Register("ToggleConsole", ActionKind.Button);

        // Screens (13 §3, F38). Navigation is the client's business because screens are: a headless
        // server has no use for "the highlighted row moved down". What *opens* them is here too, so a
        // game only says which screen a key opens.
        actions.Register("MenuUp", ActionKind.Button);
        actions.Register("MenuDown", ActionKind.Button);
        actions.Register("MenuConfirm", ActionKind.Button);
        actions.Register("MenuAlternate", ActionKind.Button);
        actions.Register("MenuBack", ActionKind.Button);
        actions.Register("Inventory", ActionKind.Button);
        actions.Register("Spellbook", ActionKind.Button);
        actions.Register("Spellmaker", ActionKind.Button);

        // In Init, not Start: config.cfg is executed between the two (01 §5.1), so an Archive cvar
        // registered in Start does not exist yet when the saved value is read — the line is dropped
        // with an "unknown cvar" warning and the setting silently never applies (review #58).
        _debugDraw = ctx.Engine.CVars.Register("r_debugdraw", false, CVarFlags.DevOnly,
            "Draw debug geometry from the simulation: sweeps, sight cones, colliders (06 §3.2).");
        _crosshair = ctx.Engine.CVars.Register("ui_crosshair", true, CVarFlags.Archive,
            "Draw the crosshair while a camera rig has the view (13 §3).");
        _assetHotReload = ctx.Engine.CVars.Register("asset_hotreload", BuildInfo.IsDevBuild && ctx.Engine.Core.Developer.Value >= 1,
            CVarFlags.DevOnly, "Reload textures and compiled effects when they change on disk (05 §3.6).");

        // Audio (11 §3, F4). The buses are cvars because that is what a volume slider writes to, and
        // they are archived because nobody wants to set them twice.
        _soundEnabled = ctx.Engine.CVars.Register("snd_enabled", true, CVarFlags.Archive,
            "Play sounds at all. The mixer still runs when this is off, so nothing downstream changes.");
        _mixer = new AudioMixer();
        RegisterBus(ctx, "snd_volume", AudioBus.Master, "Everything.");
        RegisterBus(ctx, "snd_sfx", AudioBus.Sfx, "Impacts, spells, footsteps.");
        RegisterBus(ctx, "snd_music", AudioBus.Music, "Music.");
        RegisterBus(ctx, "snd_ui", AudioBus.Ui, "Screens and menus.");
        RegisterBus(ctx, "snd_ambient", AudioBus.Ambient, "Waterfalls, wind, rooms.");
        RegisterBus(ctx, "snd_voice", AudioBus.Voice, "Speech.");

        var voices = ctx.Engine.CVars.Register("snd_maxvoices", 32, CVarFlags.Archive,
            "How many sounds may play at once; past it the quietest is stolen (11 §3).");
        voices.Changed += _ => _mixer!.MaxVoices = Math.Max(voices.Value, 1);
        _mixer.MaxVoices = Math.Max(voices.Value, 1);

        ctx.Engine.CVars.RegisterCommand("snd_stats", CVarFlags.None,
            "What is playing, and what the mixer has refused or stolen.", _ =>
        {
            Log.Info(LogCat.Console, $"{_mixer!.Playing} voice(s) playing, backend {_audio?.Playing ?? 0}; " +
                                     $"{_mixer.Refused} refused, {_mixer.Stolen} stolen since the last reset");
            foreach (var voice in _mixer.Voices)
                Log.Info(LogCat.Console, $"  {voice.Sound,-28} gain {voice.Gain:F2} pan {voice.Pan,5:F2} " +
                                         $"{(voice.Loop ? "loop" : "one-shot")}{(voice.Stopping ? " (stopping)" : "")}");
            _mixer.ResetStats();
        });

        ctx.Engine.CVars.RegisterCommand("snd_play", CVarFlags.Cheat,
            "snd_play <sound>: play a sound record at the listener, to hear what it is.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "snd_play <sound>"); return; }
            var id = ctx.Engine.Records.Resolve("sound", a[0]);
            if (id.IsEmpty) return;
            ctx.Engine.Records.TryGet(id, out SoundRecord? record);
            var voice = _mixer!.Play(id, record, _mixer.ListenerPosition, positional: false);
            Log.Info(LogCat.Console, voice.IsValid ? $"playing {id}" : $"{id} was refused (see snd_stats)");
        });

        ctx.Engine.CVars.RegisterCommand("asset_reload", CVarFlags.DevOnly,
            "asset_reload [path]: reload one loaded asset, or every loaded asset.", a =>
        {
            if (_content == null) { Log.Warn(LogCat.Assets, "asset_reload: no content service yet"); return; }
            if (a.Count > 0)
            {
                Log.Info(LogCat.Console, _content.Reload(AssetPath.Intern(a[0]))
                    ? $"reloaded {a[0]}" : $"asset_reload: {a[0]} is not loaded (see asset_list)");
                return;
            }
            int n = 0;
            foreach (var asset in System.Linq.Enumerable.ToList(_content.Cached))
                if (asset.CanReload && _content.Reload(asset.Path)) n++;
            Log.Info(LogCat.Console, $"reloaded {n} asset(s)");
        });

        ctx.Engine.CVars.RegisterCommand("asset_list", CVarFlags.None, "Every asset currently loaded.", _ =>
        {
            if (_content == null) return;
            int n = 0;
            foreach (var (path, kind, canReload) in _content.Cached)
            {
                Log.Info(LogCat.Console, $"  {path,-48} {kind}{(canReload ? "" : "  (rebuild to change)")}");
                n++;
            }
            Log.Info(LogCat.Console, $"{n} asset(s) loaded");
        });
    }

    public void Start(ModuleContext ctx)
    {
        var host = _host = ctx.Get<ClientHost>();
        _records = ctx.Engine.Records;
        _content = new ContentService(host, ctx.Engine.Vfs);
        _renderer = new Renderer(host, _content, ctx.Engine);
        _ui = new UiResources(host.GraphicsDevice);
        ctx.Provide(_content);
        ctx.Provide(_renderer);

        // Watching belongs here, with the thing that owns the cache (05 §3.6). It is polled by a
        // Frame-phase system rather than the host loop, so the client keeps its own hot reload the
        // way records keep theirs.
        if (BuildInfo.IsDevBuild) _watcher = new AssetHotReload(_content, ctx.Engine.Vfs);
        _actions = ctx.Get<InputActions>();   // the host provides it; screens navigate with it (13 §3)
        // A real backend where there is a device, and a silent one otherwise: "no audio" is a
        // configuration rather than a branch through the game's code (11 §3).
        _audio = CreateBackend();
        _actionIds = ctx.Engine.Actions;
        _devices = ctx.Get<InputDevices>();   // typed characters for a screen's field (13 §3)
    }

    public void OnWorldCreated(World world)
    {
        world.Resources.Set(new RenderSnapshot());
        world.Resources.Set(new UiDraw());       // screen-space drawing for the game's HUD (13 §3)
        // Screens (F38): the stack is a world resource because a screen acts on entities in a world.
        // A game says which screen a key opens (`stack.Bind`); the drawing and the navigation are here.
        world.Resources.Set(new ScreenStack());
        // Sprite animation is simulation, not rendering (12 §3), so AnimationModule installs it: a
        // headless server runs it, and combat listens to the "hit" events it raises (16 §3.2).
        // Terrain chunk meshes are built before extract, on the frame a sector appears (14 §3).
        world.AddSystem(new TerrainMeshSystem(world, _renderer!), Phase.FrameUpdate);
        world.AddSystem(new CameraExtract(world, _renderer!), Phase.Extract);
        world.AddSystem(new MeshExtract(world, _renderer!), Phase.Extract, after: new[] { typeof(CameraExtract) });
        world.AddSystem(new SpriteExtract(world, _renderer!, _records!), Phase.Extract, after: new[] { typeof(CameraExtract) });
        // Debug geometry last in Extract: it is drawn over everything else (06 §3.2, §3.4).
        world.AddSystem(new DebugExtract(world, _debugDraw!), Phase.Extract, after: new[] { typeof(CameraExtract) });
        world.AddSystem(new RenderSystem(world, _renderer!), Phase.Render);
        world.AddSystem(new AudioSystem(world, _mixer!, _audio!, _records!, _soundEnabled!), Phase.FrameUpdate);
        world.AddSystem(new UiRenderSystem(world, _host!, _content!, _ui!, _crosshair!), Phase.Overlay);
        // After every FrameUpdate system (so it is drawn over the game's HUD) and before the one that
        // renders the queue.
        world.AddSystem(new ScreenSystem(world, _actions!, _devices!, _actionIds!), Phase.Overlay,
                        before: new[] { typeof(UiRenderSystem) });
        if (_watcher != null) world.AddSystem(new AssetReloadSystem(_watcher, _assetHotReload!), Phase.FrameUpdate, RunCondition.DevOnly);
    }

    private sealed class AudioOptions { public RecordId Sound; public bool Loop = true; public float Volume; }

    private IAudioBackend CreateBackend()
    {
        try
        {
            // Touching the device is what finds out whether there is one: a machine with no sound card,
            // a headless CI runner and a locked-down audio session all throw here rather than earlier.
            _ = Microsoft.Xna.Framework.Audio.SoundEffect.MasterVolume;
            return new MonoGameAudioBackend(_content!);
        }
        catch (Exception ex) when (ex is Microsoft.Xna.Framework.Audio.NoAudioHardwareException or InvalidOperationException)
        {
            Log.Warn(LogCat.Audio, $"No audio device ({ex.GetType().Name}): the game runs silent.");
            return new NullAudioBackend();
        }
    }

    private void RegisterBus(ModuleContext ctx, string name, AudioBus bus, string what)
    {
        var cvar = ctx.Engine.CVars.Register(name, 1f, CVarFlags.Archive, $"Volume, 0..1. {what}", 0f, 1f);
        cvar.Changed += _ => _mixer!.SetBusVolume(bus, cvar.Value);
        _mixer!.SetBusVolume(bus, cvar.Value);
    }

    public void Shutdown()
    {
        _audio?.Dispose();
        _watcher?.Dispose();
        _ui?.Dispose();
        _renderer?.Dispose();
        _content?.Dispose();
    }
}

// FrameUpdate: gives the watcher its once-a-frame look on the main thread. A system rather than a
// host-loop call so that a world without a client (a headless test) simply never has one.
public sealed class AssetReloadSystem : ISystem
{
    private readonly AssetHotReload _watcher;
    private readonly CVar<bool> _enabled;

    public AssetReloadSystem(AssetHotReload watcher, CVar<bool> enabled)
    {
        _watcher = watcher;
        _enabled = enabled;
    }

    public void Run(in SystemContext ctx)
    {
        if (_enabled.Value) _watcher.Poll();
    }
}
