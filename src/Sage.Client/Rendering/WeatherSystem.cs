#nullable enable
using System;
using System.Numerics;

namespace Sage.Client;

// Making it rain (docs/design/06 §3.13, TODO F40).
//
// **Weather is not a thing in the world, it is a thing around the camera.** Rain is not placed and does
// not belong anywhere: it falls in a box that follows the player, because a box you can see the edge of
// is the only kind anybody can afford. Everything else — what falls, how hard, what colour the sky goes,
// what it sounds like — is the record, blended by `WeatherRules`, which is engine-side and tested.
//
// Client-side, like the particles it throws and the sound it plays: a headless server has weather in its
// records and never spends a cycle on it.
[System("sage.client.weather", Phase.FrameUpdate, After = new[] { "sage.client.audio" })]
internal sealed class WeatherSystem : ISystem
{
    private readonly Weather _weather;
    private readonly Particles _particles;
    private readonly RenderEnvironment _environment;
    private readonly RenderEnvironment _clearSky;      // the game's own light, before weather touched it
    private readonly World _world;           // its main view is where the weather falls around (#81)
    private readonly AudioMixer _audio;
    private readonly RecordStore _records;
    private readonly CVar<bool> _enabled;
    private readonly CVar<bool> _sound;

    private float _pending;                 // fractional particles carried between frames
    private RecordId _playing;              // the weather whose sound is running
    private VoiceHandle _voice;

    public WeatherSystem(World world, RecordStore records, CVar<bool> enabled, CVar<bool> sound)
    {
        _weather = world.Resources.Get<Weather>();
        _particles = world.Resources.Get<Particles>();
        _environment = world.Resources.Get<RenderEnvironment>();
        _world = world;
        _audio = world.Resources.Get<AudioMixer>();
        _records = records;
        _enabled = enabled;
        _sound = sound;

        // A copy of the sky as the game set it up. Weather *scales* the sun and the ambient rather than
        // replacing them, so this is what it scales: without it, two storms in a row would darken the
        // world twice and never let the light back.
        _clearSky = new RenderEnvironment
        {
            SunColor = _environment.SunColor,
            AmbientSky = _environment.AmbientSky,
            AmbientGround = _environment.AmbientGround,
        };
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.Frame.Dt;
        _weather.Advance(dt);

        if (!_enabled.Value)
        {
            _particles.Wind = Vector3.Zero;
            Silence();
            return;
        }

        // The baseline is what the *game* set the light to, and a game may change it (a day/night cycle
        // will). Re-read it whenever the sky is clear and settled: at that moment the environment is the
        // baseline, so anything the game has changed is picked up, and nothing weather did is baked in.
        if (_weather.Settled && WeatherRules.Falling(_records, _weather).SunScale >= 1f)
        {
            _clearSky.SunColor = _environment.SunColor;
            _clearSky.AmbientSky = _environment.AmbientSky;
            _clearSky.AmbientGround = _environment.AmbientGround;
        }

        // With a sky, the sky system composes weather into the environment (it is the baseline, and
        // weather adjusts it); overwriting it here would freeze the dusk (issue 4h-2).
        bool sky = _world.Resources.TryGet<WorldClock>(out var clock) && SkyRules.Current(_records, clock) != null;
        if (!sky) WeatherRules.Apply(_records, _weather, _environment, _clearSky);
        _particles.Wind = WeatherRules.WindNow(_records, _weather);
        Sound();
        Fall(dt);
    }

    // The falling half: a slab of sky above the camera, refilled every frame at the blended rate.
    private void Fall(float dt)
    {
        var record = WeatherRules.Falling(_records, _weather);
        if (record.Particles.IsEmpty) { _pending = 0f; return; }

        float rate = WeatherRules.RateNow(_records, _weather);
        if (rate <= 0f) { _pending = 0f; return; }

        _pending += rate * dt;
        int count = (int)_pending;
        if (count <= 0) return;
        _pending -= count;

        if (!_records.TryGet(record.Particles, out ParticleRecord effect)) return;

        // Above the camera and around it, in the weather's volume rather than the effect's: how wide the
        // rain is belongs to the weather, how a drop behaves belongs to the effect.
        var at = Eye() + new Vector3(0, record.Ceiling, 0);
        _particles.Emit(record.Particles, effect, at, -Vector3.UnitY, count, volume: record.Volume);
    }

    // Rain you cannot hear is a screen saver. One 2D loop, started when the weather that owns it takes
    // hold and stopped when it lets go (11 §3).
    private void Sound()
    {
        var record = WeatherRules.Falling(_records, _weather);
        // With the sound off the mixer stops everything every frame (11 §3), so asking it to start a rain
        // loop again each frame would be a voice made and killed sixty times a second.
        var wanted = _sound.Value ? record.Sound : default;

        if (wanted == _playing && (_voice.IsValid ? _audio.Find(_voice) != null : wanted.IsEmpty)) return;

        Silence();
        if (wanted.IsEmpty) return;

        _records.TryGet(wanted, out SoundRecord sound);
        _voice = _audio.Play(wanted, sound, Eye(), positional: false,
                             volume: record.SoundVolume, loop: true);
        _playing = wanted;
    }

    // Where the screen's view is this frame (the camera it shows, or ActiveCamera without one).
    private Vector3 Eye() => _world.TryGetMainView(out var view) ? view.Position : Vector3.Zero;

    private void Silence()
    {
        if (_voice.IsValid) _audio.Stop(_voice);
        _voice = VoiceHandle.None;
        _playing = default;
    }
}
