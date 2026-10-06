#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

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
[Plugin("sage.client", "0.1.0")]
public sealed class ClientModule : IModule
{
    private ContentService? _content;
    private ClientHost? _host;
    private UiResources? _ui;
    private Renderer? _renderer;
    private RendererCVars? _rendererCVars;
    private RecordStore? _records;
    private CVar<bool>? _debugDraw;
    private CVar<bool>? _crosshair;
    private CVar<bool>? _assetHotReload;
    private AssetHotReload? _watcher;
    private ShaderRecompiler? _shaders;
    private InputActions? _actions;
    private CVar<bool>? _particlesOn;
    private CVar<int>? _decalCeiling;
    private CVar<bool>? _weatherOn;
    private CVar<bool>? _lightsOn;
    private CVar<bool>? _damageNumbers;
    private AudioSettings? _audioSettings;
    private bool _audioDevice;
    private CVar<bool>? _soundEnabled;
    private InputDevices? _devices;
    private ActionRegistry? _actionIds;

    // Screens by id, for the client to ask for and a kit or a game to fill (issue #27).
    public ScreenRegistry Screens { get; } = new();

    // The renderer, from Start on (null before): for the host's own tools — the editor's viewport draws a
    // render target it declares here (issue #81). A module asks for it with `ctx.Get<Renderer>()` instead.
    public Renderer? Renderer => _renderer;

    // The render passes (issue 4h-1, REDESIGN §4.7): the engine's own are added here in Init, a game's or
    // a plugin's in its Init (`ctx.Get<RenderPasses>().Add(new MyPass())`); sealed and ordered in Start.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public RenderPasses Passes { get; } = new();
    private UiPass? _uiPass;

    public void Init(ModuleContext ctx)
    {
        _rendererCVars = new RendererCVars(ctx.Engine.CVars);   // here, not in the Renderer: see RendererCVars

        // The `audio` and `particles` prefab parts are declared below (AudioPart, ParticlesPart) and
        // registered for this plugin by generated code (issue #17).

        // The client's own actions (08 §3.2); the gameplay ones (Move, Jump, Crouch...) belong to the
        // gameplay module, so a headless server registers the same ids. Bindings for all of them are
        // in engine_content/data/input.json.
        var actions = ctx.Engine.Actions;
        actions.Register("Look", ActionKind.Axis2D);
        actions.Register("Menu", ActionKind.Button);
        actions.Register("ToggleConsole", ActionKind.Button);
        // The editor's free camera (`-edit`, issue #219): bound in the `Editor` context only, so a pawn's
        // Move and the camera's never share a key in one run.
        actions.Register("EditorMove", ActionKind.Axis2D);

        // Screens (13 §3, F38). Navigation is the client's business because screens are: a headless
        // server has no use for "the highlighted row moved down". The bag opens with Inventory; the
        // actions that open a kit's screens are the kit's (Sage.Kits.Rpg.Client: Spellbook,
        // Spellmaker, Journal), so a game only says which screen a key opens.
        actions.Register("MenuUp", ActionKind.Button);
        actions.Register("MenuDown", ActionKind.Button);
        // Widget screens walk grids and tab order too (issue #97): D-pad left/right and a tab button.
        actions.Register("MenuLeft", ActionKind.Button);
        actions.Register("MenuRight", ActionKind.Button);
        actions.Register("MenuTab", ActionKind.Button);
        actions.Register("MenuConfirm", ActionKind.Button);
        actions.Register("MenuAlternate", ActionKind.Button);
        actions.Register("MenuBack", ActionKind.Button);
        actions.Register("Inventory", ActionKind.Button);
        // Which screen is which, by id (issue #27). In Init, so a module that depends on this one can
        // register its screens in its own Init.
        ctx.Provide(Screens);

        // In Init, not Start: config.cfg is executed between the two (01 §5.1), so an Archive cvar
        // registered in Start does not exist yet when the saved value is read — the line is dropped
        // with an "unknown cvar" warning and the setting silently never applies (review #58).
        _debugDraw = ctx.Engine.CVars.Register("r_debugdraw", false, CVarFlags.DevOnly,
            "Draw debug geometry from the simulation: sweeps, sight cones, colliders (06 §3.2).");
        _crosshair = ctx.Engine.CVars.Register("ui_crosshair", true, CVarFlags.Archive,
            "Draw the crosshair while a camera rig has the view (13 §3).");
        // The engine's render passes, on the registry like anyone's (issue 4h-1): a game orders its own
        // against these ids. Provided here, in Init, so a module that depends on this one adds its
        // passes in its own Init.
        Passes.Add(new ShadowPass(_rendererCVars));   // the sun's shadow map (issue 4h-4)
        Passes.Add(new OpaquePass());
        Passes.Add(new AlphaTestedPass());
        Passes.Add(new SkyPass());                    // the sky behind them (issue 4h-5)
        Passes.Add(new TransparentPass());
        Passes.Add(new DebugLinesPass());
        Passes.Add(new PostProcessPass());
        Passes.Add(_uiPass = new UiPass(_crosshair, _rendererCVars.TestView));
        ctx.Provide(Passes);
        ctx.Engine.CVars.RegisterCommand("r_passes", CVarFlags.None, "The render passes in draw order: stage, id, and what each draws after or before.", _ =>
        {
            if (!Passes.IsSealed) { Log.Info(LogCat.Console, $"{Passes.Count} render pass(es), not ordered until the client starts"); return; }
            foreach (var pass in Passes.Ordered)
            {
                string after = pass.After.Count > 0 ? $" after {string.Join(", ", pass.After)}" : "";
                string before = pass.Before.Count > 0 ? $" before {string.Join(", ", pass.Before)}" : "";
                string replaced = pass.Replaces != null ? $" (replaces {pass.Replaces.Name}, by {pass.By})" : "";
                Log.Info(LogCat.Console, $"  {pass.Stage,-12} {pass.Id,-28} {pass.Type.Name} ({pass.Type.Assembly.GetName().Name}){replaced}{after}{before}");
            }
            foreach (var pass in Passes.Disabled)
                Log.Info(LogCat.Console, $"  {pass.Stage,-12} {pass.Id,-28} {pass.Type.Name} DISABLED by {pass.By}");
            Log.Info(LogCat.Console, $"{Passes.Count} render pass(es)");
        });
        _assetHotReload = ctx.Engine.CVars.Register("asset_hotreload", BuildInfo.IsDevBuild && ctx.Engine.Core.Developer.Value >= 1,
            CVarFlags.DevOnly, "Reload textures and compiled effects when they change on disk (05 §3.6).");

        // Audio (11 §3, F4). The buses are cvars because that is what a volume slider writes to, and
        // they are archived because nobody wants to set them twice.
        // Particles (06 §3.12, F39). Both are Archive because both are the kind of thing a player turns
        // off and expects to stay off.
        _particlesOn = ctx.Engine.CVars.Register("r_particles", true, CVarFlags.Archive,
            "Draw particles: sparks, embers, smoke, blood (06 §3.12).");
        _damageNumbers = ctx.Engine.CVars.Register("ui_damagenumbers", true, CVarFlags.Archive,
            "Show what each hit took, over the thing that took it (13 §3).");
        // Decals (issue #306): how many marks each world keeps before the oldest goes; 0 draws none.
        _decalCeiling = ctx.Engine.CVars.Register("r_decals", Decals.DefaultCeiling, CVarFlags.Archive,
            "How many decals (bullet holes, blood, scorch) a world keeps at once; the oldest goes first. 0 = none.", 0, 4096);

        _lightsOn = ctx.Engine.CVars.Register("r_lights", true, CVarFlags.Archive,
            "Light the world with point lights as well as the sun (06 §3.9). Off is the old look: a room with a roof is a dark box.");

        _weatherOn = ctx.Engine.CVars.Register("r_weather", true, CVarFlags.Archive,
            "Let the sky do things: rain, snow, the light going out of it (06 §3.13).");

        ctx.Engine.CVars.RegisterCommand("weather", CVarFlags.Cheat,
            "weather [id] [seconds]: what the sky is doing, or change it over that many seconds.", a =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<Weather>(out var weather) || weather == null) continue;
                if (a.Count == 0)
                {
                    Log.Info(LogCat.Console, weather.Settled
                        ? $"'{world.Name}': {weather.Target}"
                        : $"'{world.Name}': {weather.Current} → {weather.Target} ({weather.Blend * 100f:F0}%)");
                    continue;
                }

                var id = ctx.Engine.Records.Resolve("weather", a[0]);
                if (id.IsEmpty) continue;
                float seconds = a.Count > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float,
                                                              System.Globalization.CultureInfo.InvariantCulture,
                                                              out float asked) ? asked : 6f;
                weather.Set(id, seconds);
                Log.Info(LogCat.Console, $"'{world.Name}': {id} over {seconds:F1}s");
            }
        });

        ctx.Engine.CVars.RegisterCommand("music", CVarFlags.Cheat,
            "music [id|stop] [seconds]: what the music is doing, or fade to a track (or to silence) over that many seconds.", a =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<Music>(out var music) || music == null) continue;
                if (a.Count == 0)
                {
                    string now = music.Track.IsEmpty ? "silence" : music.Track.ToString();
                    Log.Info(LogCat.Console, (music.Settled || music.Previous.IsEmpty && music.Track.IsEmpty
                        ? $"'{world.Name}': {now}"
                        : $"'{world.Name}': {(music.Previous.IsEmpty ? "silence" : music.Previous.ToString())} → {now} ({music.Blend * 100f:F0}%)")
                        + $", intensity {music.Intensity:0.##}");
                    continue;
                }
                float? seconds = a.Count > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float,
                                                               System.Globalization.CultureInfo.InvariantCulture,
                                                               out float asked) && asked >= 0f ? asked : null;
                if (string.Equals(a[0], "stop", StringComparison.OrdinalIgnoreCase))
                {
                    MusicRules.Stop(world, seconds);
                    Log.Info(LogCat.Console, $"'{world.Name}': music stops");
                    continue;
                }
                var id = ctx.Engine.Records.Resolve("music", a[0]);
                if (id.IsEmpty) continue;
                MusicRules.Play(world, id, seconds);
                Log.Info(LogCat.Console, $"'{world.Name}': music to {id}");
            }
        });

        ctx.Engine.CVars.RegisterCommand("music_intensity", CVarFlags.Cheat,
            "music_intensity <0..1>: how intense things are, which decides the layers of the music that are heard.", a =>
        {
            if (a.Count == 0 || !float.TryParse(a[0], System.Globalization.NumberStyles.Float,
                                                System.Globalization.CultureInfo.InvariantCulture, out float intensity))
            {
                Log.Warn(LogCat.Console, "music_intensity <0..1>");
                return;
            }
            foreach (var world in ctx.Engine.Worlds) MusicRules.SetIntensity(world, intensity);
        });

        ctx.Engine.CVars.RegisterCommand("fx_stats", CVarFlags.None,
            "How many particles are alive, and what has been refused.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<Particles>(out var particles) || particles == null) continue;
                world.Resources.TryGet<FloatingTexts>(out var texts);
                Log.Info(LogCat.Console, $"'{world.Name}': {particles.Live} particle(s) of {particles.Budget}, " +
                                         $"{particles.Refused} refused; {particles.Rays} collision ray(s), " +
                                         $"{particles.RaysSkipped} over the budget of {particles.CollisionBudget} a frame, " +
                                         $"{particles.Hits} hit(s); {texts?.Count ?? 0} number(s)");
                foreach (var group in particles.Groups)
                    if (group.Count > 0)
                        Log.Info(LogCat.Console, $"  {group.Effect,-28} {group.Count,5} live");
                particles.ResetStats();
            }
        });

        ctx.Engine.CVars.RegisterCommand("fx_play", CVarFlags.Cheat,
            "fx_play <effect> [count]: throw a burst where you are standing, to see what it looks like.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "fx_play <effect> [count]"); return; }
            var id = ctx.Engine.Records.Resolve("particle", a[0]);
            if (id.IsEmpty) return;
            ctx.Engine.Records.TryGet(id, out ParticleRecord effect);
            int count = a.Count > 1 && int.TryParse(a[1], out int asked) ? asked : effect?.Burst ?? 16;

            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<Particles>(out var particles) || particles == null) continue;
                // Two metres in front of the eye, not *at* it: an audition you are standing inside
                // tells you nothing about what it looks like.
                // From the screen's view (#81): the camera the player is looking through, whichever it is.
                if (!world.TryGetMainView(out var eye)) continue;
                var ahead = eye.Forward;
                int thrown = particles.Emit(id, effect, eye.Position + ahead * 2f, ahead, count);
                Log.Info(LogCat.Console, $"{thrown} particle(s) of {id}");
            }
        });

        ctx.Engine.CVars.RegisterCommand("fx_decal", CVarFlags.Cheat,
            "fx_decal <decal>: lay a decal on whatever you are looking at (within 50 m), to see what it looks like.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "fx_decal <decal>"); return; }
            var id = ctx.Engine.Records.Resolve("decal", a[0]);
            if (id.IsEmpty || !ctx.Engine.Records.TryGet(id, out DecalRecord decal)) return;

            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<Decals>(out var decals) || decals == null) continue;
                if (!world.TryGetMainView(out var eye) || !world.Resources.TryGet<IPhysicsWorld>(out var space) || space == null) continue;
                var hit = space.Raycast(eye.Position, eye.Forward, 50f);
                bool placed = hit.Hit && decals.Place(id, decal, hit.Position, hit.Normal, surface: hit.Entity);
                Log.Info(LogCat.Console, placed ? $"{id} at {hit.Position}" : $"{id}: nothing within 50 m to put it on");
            }
        });

        _soundEnabled = ctx.Engine.CVars.Register("snd_enabled", true, CVarFlags.Archive,
            "Play sounds at all. The mixer still runs when this is off, so nothing downstream changes.");
        // The settings, not a mixer: the mixers are per world (11 §3) and they all read this one.
        _audioSettings = new AudioSettings();
        RegisterBus(ctx, "snd_volume", AudioBus.Master, "Everything.");
        RegisterBus(ctx, "snd_sfx", AudioBus.Sfx, "Impacts, spells, footsteps.");
        RegisterBus(ctx, "snd_music", AudioBus.Music, "Music.");
        RegisterBus(ctx, "snd_ui", AudioBus.Ui, "Screens and menus.");
        RegisterBus(ctx, "snd_ambient", AudioBus.Ambient, "Waterfalls, wind, rooms.");
        RegisterBus(ctx, "snd_voice", AudioBus.Voice, "Speech.");

        var voices = ctx.Engine.CVars.Register("snd_maxvoices", 32, CVarFlags.Archive,
            "How many sounds may play at once; past it the quietest is stolen (11 §3).");
        voices.Changed += _ => _audioSettings!.MaxVoices = Math.Max(voices.Value, 1);
        _audioSettings.MaxVoices = Math.Max(voices.Value, 1);

        ctx.Engine.CVars.RegisterCommand("snd_stats", CVarFlags.None,
            "What is playing, and what the mixer has refused or stolen.", _ =>
        {
            // Per world, because the mixers are: two worlds running means two sets of voices, and a
            // total that hides which world is making the noise is the number nobody wants.
            int listening = 0;
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<AudioMixer>(out var mixer) || mixer == null) continue;
                listening++;
                world.Resources.TryGet<IAudioBackend>(out var backend);
                Log.Info(LogCat.Console, $"'{world.Name}': {mixer.Playing} voice(s) playing, " +
                                         $"backend {backend?.Playing ?? 0}; {mixer.Refused} refused, " +
                                         $"{mixer.Stolen} stolen since the last reset");
                foreach (var voice in mixer.Voices)
                    Log.Info(LogCat.Console, $"  {voice.Sound,-28} gain {voice.Gain:F2} pan {voice.Pan,5:F2} " +
                                             $"{(voice.Loop ? "loop" : "one-shot")}{(voice.Stopping ? " (stopping)" : "")}");
                mixer.ResetStats();
            }
            if (listening == 0) Log.Info(LogCat.Console, "no world is listening yet");
        });

        ctx.Engine.CVars.RegisterCommand("snd_play", CVarFlags.Cheat,
            "snd_play <sound>: play a sound record at the listener, to hear what it is.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "snd_play <sound>"); return; }
            var id = ctx.Engine.Records.Resolve("sound", a[0]);
            if (id.IsEmpty) return;
            var mixer = FirstMixer(ctx.Engine);
            if (mixer == null) { Log.Warn(LogCat.Console, "snd_play: no world is listening yet"); return; }
            ctx.Engine.Records.TryGet(id, out SoundRecord record);
            var voice = mixer.Play(id, record, mixer.ListenerPosition, positional: false);
            Log.Info(LogCat.Console, voice.IsValid ? $"playing {id}" : $"{id} was refused (see snd_stats)");
        });

        ctx.Engine.CVars.RegisterCommand("asset_reload", CVarFlags.DevOnly,
            "asset_reload [path]: reload one loaded asset, or every loaded asset.", a =>
        {
            if (_content == null) { Log.Warn(LogCat.Assets, "asset_reload: no content service yet"); return; }
            if (a.Count > 0)
            {
                var path = AssetPath.Intern(a[0]);
                if (_content.Reload(path)) { Log.Info(LogCat.Console, $"reloaded {a[0]}"); return; }

                // "Not loaded" and "loaded but cannot be swapped" are different answers, and telling a
                // sound it was never loaded sends you looking in the wrong place (a sound's voices hold
                // instances of it, see `Cached`).
                bool loaded = false;
                foreach (var asset in _content.Cached) if (asset.Path == path) { loaded = true; break; }
                Log.Info(LogCat.Console, loaded
                    ? $"asset_reload: {a[0]} is loaded but cannot be hot reloaded"
                    : $"asset_reload: {a[0]} is not loaded (see asset_list)");
                return;
            }
            int n = 0;
            foreach (var asset in System.Linq.Enumerable.ToList(_content.Cached))
                if (asset.CanReload && _content.Reload(asset.Path)) n++;
            Log.Info(LogCat.Console, $"reloaded {n} asset(s)");
        });

        var listed = ctx.Engine;
        ctx.Engine.CVars.RegisterCommand("asset_list", CVarFlags.None,
            "asset_list [filter]: every asset loaded, with its scope, how many live entities hold it, and its size (issue #308).", a =>
        {
            if (_content == null || _renderer == null) return;
            string? filter = a.Count > 0 ? a[0] : null;
            var worlds = listed.Worlds;
            int n = 0;
            long bytes = 0;
            void Row(AssetPath path, string kind, AssetType? type, AssetScope scope, long size, string note = "")
            {
                if (filter != null && !path.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
                string refs = type is { } t ? AssetReleases.RefCount(worlds, new AssetKey(t, path)).ToString() : "-";
                Log.Info(LogCat.Console, $"  {path,-48} {kind,-8} {AssetScopes.Name(scope),-7} refs {refs,-4} {size / 1024.0,8:F1} KB{note}");
                n++;
                bytes += size;
            }
            foreach (var (_, entry) in _renderer.Meshes.Entries)
                if (!entry.Path.IsEmpty) Row(entry.Path, "mesh", AssetType.Mesh, entry.Scope, entry.Bytes);
            foreach (var (_, entry) in _content.Textures.Entries) Row(entry.Path, "texture", AssetType.Texture, entry.Scope, entry.Bytes);
            foreach (var (path, kind, canReload) in _content.Cached)
                if (kind != "texture") Row(path, kind, null, AssetScope.Game, 0, canReload ? "" : "  (no hot reload)");
            int built = 0;
            foreach (var (_, entry) in _renderer.Meshes.Entries) if (entry.Path.IsEmpty) built++;
            Log.Info(LogCat.Console, $"{n} asset(s) listed, {bytes / (1024.0 * 1024.0):F1} MB of meshes and textures; {built} built mesh(es) " +
                                     $"(terrain, brushes); {_renderer.Releases.Evicted} evicted so far; upload budget {_renderer.Budget.MillisecondsPerFrame:F1} ms, " +
                                     $"{_renderer.Budget.TotalDeferred} load(s) put off to a later frame");
        });
    }

    public void Start(ModuleContext ctx)
    {
        Screens.Seal.Seal("the client started");
        var host = _host = ctx.Get<ClientHost>();
        _records = ctx.Engine.Records;
        _content = new ContentService(host, ctx.Engine.Vfs);
        // Every module's Init has run, so every pass is in: order them now. A cycle, or an After naming
        // a pass nobody added, stops the boot here (RenderPassRegistry).
        Passes.Seal("the client started");
        _renderer = new Renderer(host, _content, ctx.Engine, _rendererCVars!, Passes);
        _content.ModelReloader = _renderer.ReloadMesh;
        // A reloaded sound's old effect is about to be disposed, taking its instances with it: every
        // world's backend lets go of them and its mixer stops or restarts the voices (issue 4h-3).
        var engine = ctx.Engine;
        _content.SoundReplacing += path =>
        {
            foreach (var world in engine.Worlds)
            {
                if (world.Resources.TryGet<IAudioBackend>(out var backend) && backend != null) backend.Release(path);
                if (world.Resources.TryGet<AudioMixer>(out var mixer) && mixer != null) mixer.Invalidate(path);
            }
        };
        _ui = new UiResources(host.GraphicsDevice);
        _uiPass!.Content = _content;
        _uiPass.Shared = _ui;
        ctx.Provide(_content);
        ctx.Provide(_renderer);

        // Watching belongs here, with the thing that owns the cache (05 §3.6). It is polled by a
        // Frame-phase system rather than the host loop, so the client keeps its own hot reload the
        // way records keep theirs.
        if (BuildInfo.IsDevBuild)
        {
            _watcher = new AssetHotReload(_content, ctx.Engine.Vfs);
            _shaders = new ShaderRecompiler(ctx.Engine.Vfs);
        }
        _actions = ctx.Get<InputActions>();   // the host provides it; screens navigate with it (13 §3)
        // Asked once, here, rather than per world: whether this machine has a device does not change
        // between worlds, and the answer is worth exactly one log line. A real backend where there is
        // one and a silent one otherwise, so "no audio" is a configuration and not a branch (11 §3).
        _audioDevice = HasAudioDevice();
        _actionIds = ctx.Engine.Actions;
        _devices = ctx.Get<InputDevices>();   // typed characters for a screen's field (13 §3)
    }

    public void OnWorldCreated(World world)
    {
        world.Resources.GetOrAdd(() => new ActiveCamera());   // what the world is drawn from (issue #13)
        world.Resources.Add(new RenderSnapshot());
        world.Resources.Add(new UiDraw());       // screen-space drawing for the game's HUD (13 §3)
        // Screens (F38): the stack is a world resource because a screen acts on entities in a world.
        // A game says which screen a key opens (`stack.Bind`); the drawing and the navigation are here.
        world.Resources.Add(new ScreenStack());
        // Sprite animation is simulation, not rendering (12 §3), so AnimationModule installs it: a
        // headless server runs it, and combat listens to the "hit" events it raises (16 §3.2).
        // The frame's safe point for assets: what sectors released is freed before anything extracts (#308).
        world.AddSystem(new AssetScopeSystem(world, _renderer!));
        // Terrain chunk meshes are built before extract, on the frame a sector appears (14 §3).
        world.AddSystem(new TerrainMeshSystem(world, _renderer!));
        // The far ring's coarse ground and far looks (#277), when sage.streaming keeps one.
        if (world.Resources.TryGet<SectorLod>(out _))
            world.AddSystem(new FarLodSystem(world, _renderer!));
        // Brush levels (15 §3, F16), when sage.maps is loaded. The client comes after every simulation
        // plugin (SageApp adds host modules after them), so what they furnish is there to look for.
        if (world.Resources.TryGet<MapLevels>(out _))
            world.AddSystem(new MapMeshSystem(world, _renderer!));
        // What the world is drawn from: its views (issue #77; ActiveCamera until camera components).
        world.AddSystem(new CameraExtract(world, _renderer!, new ViewSource(world, _renderer!, _rendererCVars!.TestView)));
        world.AddSystem(new RenderPassExtract(world, _renderer!));   // the passes' own views and items (issue 4h-1)
        world.AddSystem(new MeshExtract(world, _renderer!));
        // Skinned meshes and their joint palettes (issue #117); `r_testskin` stands a bending column up.
        world.AddSystem(new SkinnedMeshExtract(world, _renderer!));
        world.AddSystem(new TestSkinSystem(world, _renderer!, _rendererCVars!.TestSkin));
        world.AddSystem(new SpriteExtract(world, _renderer!, _records!));
        // Debug geometry last in Extract: it is drawn over everything else (06 §3.2, §3.4).
        world.AddSystem(new LightExtract(world, _renderer!, _lightsOn!));
        world.AddSystem(new DebugExtract(world, _debugDraw!));
        world.AddSystem(new ViewmodelExtract(world, _renderer!));   // first-person arms, after the world (issue #121)
        world.AddSystem(new RenderSystem(world, _renderer!));
        // Audio is per world for the same reason the snapshot is: a voice's position is in *this*
        // world's origin space (R6), and two worlds do not share a frame. The backend is a world
        // resource so that the world's own teardown disposes it, and because voice handles are only
        // unique within one mixer — one shared backend would confuse two worlds' voices.
        world.Resources.Add(new AudioMixer(_audioSettings!));
        world.Resources.Add<IAudioBackend>(_audioDevice && _content != null
            ? new MonoGameAudioBackend(_content) : new NullAudioBackend());
        // Particles and the numbers over a fight are per world, like everything else that holds a
        // position in this world's origin space (06 §3.12, R6).
        world.Resources.Add(new Particles());
        world.Resources.Add(new FloatingTexts());
        world.AddSystem(new ParticleSystem(world, _records!, _particlesOn!, _damageNumbers!));
        world.AddSystem(new ParticleExtract(world, _renderer!));
        // Marks that stay (issue #306): the pool is the world's, placed by gameplay's DecalSystem.
        world.Resources.Add(new Decals { Ceiling = _decalCeiling!.Value });
        world.AddSystem(new DecalExtract(world, _renderer!, _decalCeiling!));
        world.AddSystem(new WaterExtract(world, _records!));   // the main view's water (issue #411)
        world.AddSystem(new AudioSystem(world, _records!, _soundEnabled!));
        // The `Weather` state is the world's (installed with it); this only makes it *look* like it.
        world.AddSystem(new WeatherSystem(world, _records!, _weatherOn!, _soundEnabled!));
        // Talking to somebody opens a window, which is the client's business (16 §3.5, F24) — in a game
        // with the dialogue plugin.
        if (world.Resources.TryGet<Conversation>(out _))
            world.AddSystem(new DialogueSystem(world, Screens));
        // Before the HUD is drawn, so a number never sits on top of the health bar.
        world.AddSystem(new FloatingTextSystem(world));
        world.AddSystem(new UiRenderSystem(world, _renderer!));
        // After every FrameUpdate system (so it is drawn over the game's HUD) and before the one that
        // renders the queue.
        world.AddSystem(new ScreenSystem(world, _actions!, _devices!, _actionIds!, _content!));
        if (_watcher != null) world.AddSystem(new AssetReloadSystem(_watcher, _shaders!, _assetHotReload!));
    }

    // `snd_play` is an audition, so it belongs to whichever world is listening — the first one with
    // a mixer, which in a single-world game is the only one there is.
    private static AudioMixer? FirstMixer(Engine engine)
    {
        foreach (var world in engine.Worlds)
            if (world.Resources.TryGet<AudioMixer>(out var mixer) && mixer != null) return mixer;
        return null;
    }

    private static bool HasAudioDevice()
    {
        try
        {
            // Touching the device is what finds out whether there is one: a machine with no sound card,
            // a headless CI runner and a locked-down audio session all throw here rather than earlier.
            _ = Microsoft.Xna.Framework.Audio.SoundEffect.MasterVolume;
            return true;
        }
        catch (Exception ex) when (ex is Microsoft.Xna.Framework.Audio.NoAudioHardwareException or InvalidOperationException)
        {
            Log.Warn(LogCat.Audio, $"No audio device ({ex.GetType().Name}): the game runs silent.");
            return false;
        }
    }

    private void RegisterBus(ModuleContext ctx, string name, AudioBus bus, string what)
    {
        var cvar = ctx.Engine.CVars.Register(name, 1f, CVarFlags.Archive, $"Volume, 0..1. {what}", 0f, 1f);
        cvar.Changed += _ => _audioSettings!.SetVolume(bus, cvar.Value);
        _audioSettings!.SetVolume(bus, cvar.Value);
    }

    public void Shutdown()
    {
        // No backend to dispose here: each world owns one, and the world's teardown disposes it.
        _watcher?.Dispose();
        _shaders?.Dispose();
        _ui?.Dispose();
        _renderer?.Dispose();
        _content?.Dispose();
    }
}

// FrameUpdate: gives the watcher its once-a-frame look on the main thread. A system rather than a
// host-loop call so that a world without a client (a headless test) simply never has one.
[System("sage.client.asset_reload", Phase.FrameUpdate, Condition = RunCondition.DevOnly)]
internal sealed class AssetReloadSystem : ISystem
{
    private readonly AssetHotReload _watcher;
    private readonly ShaderRecompiler _shaders;
    private readonly CVar<bool> _enabled;

    public AssetReloadSystem(AssetHotReload watcher, ShaderRecompiler shaders, CVar<bool> enabled)
    {
        _watcher = watcher;
        _shaders = shaders;
        _enabled = enabled;
    }

    public void Run(in SystemContext ctx)
    {
        if (!_enabled.Value) return;
        _watcher.Poll();
        _shaders.Poll();
    }
}

// A thing that hums: `"audio": { "sound": "fire_loop", "loop": true }` on any prefab. The component is
// the engine's, so a headless run carries it and simply never plays it — which is why a simulation that
// uses it declares it `Prefabs.Optional("audio")`: without the client there is no part to apply.
[PrefabPart("audio", Plugin = "sage.client")]
public sealed class AudioPart : IPrefabPart
{
    public RecordId Sound;
    public bool Loop = true;
    public float Volume;               // 0 = full

    public void Apply(in PrefabPartContext ctx)
    {
        if (Sound.IsEmpty) { ctx.Error("needs a \"sound\""); return; }
        ctx.World.Add(ctx.Entity, new AudioSource { Sound = Sound, Loop = Loop, Volume = Volume <= 0f ? 1f : Volume });
    }
}

// Something that smokes on its own: `"particles": { "effect": "sandbox:embers" }`. The twin of `audio`,
// and for the same reason — the component is the engine's, so a headless run carries it and never
// draws a thing.
[PrefabPart("particles", Plugin = "sage.client")]
public sealed class ParticlesPart : IPrefabPart
{
    public RecordId Effect;
    public bool Enabled = true;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Effect.IsEmpty) { ctx.Error("needs an \"effect\""); return; }
        ctx.World.Add(ctx.Entity, new ParticleEmitter { Effect = Effect, Enabled = Enabled });
    }
}
