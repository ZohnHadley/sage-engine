#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// What the room does to a sound (issue #329): occlusion and reverb, decided headlessly so they are tested
// without a sound card, and handed to the backend as numbers — the same split as the rest of the mixer.
//
// **Occlusion.** A ray from the listener to each positional voice; a hit means something solid is in the
// way, and the voice's occlusion moves toward 1 (toward 0 when the ray is clear) over `Smoothing` seconds,
// so walking past a door frame does not click. An occluded voice is quieter (`OccludedGain`) and duller
// (`OccludedLowPass`, the high-frequency gain the backend's low-pass filter gets). Rays are **budgeted**:
// at most `AudioSettings.OcclusionRays` per update, round-robin over the voices, so forty footsteps cost
// what eight do. A voice that has not had its first ray is asked first, and takes its answer at once rather
// than fading into it, so a sound that starts behind a wall starts muffled.
//
// The ray sees the world's solid things (the default layer and the game's own) and not characters,
// triggers or hitboxes: the listener stands inside the player's capsule, and a guard in the doorway does
// not muffle the fire behind him. It stops a little short of the emitter, so a torch on a wall is not
// occluded by the wall it hangs on.
//
// **Reverb.** The zone the listener is in (`ReverbZone`) picks a preset (`reverb` record); a change of
// zone blends from what is heard now to the new one over the zone's fade, and leaving every zone fades the
// reverb out over the fade of the zone left.
//
// One per mixer (`AudioMixer.Environment`), so one per world. Nothing here is saved: a load puts the
// listener back and the next update works the room out again.
internal sealed class AudioEnvironment
{
    private readonly World _world;
    private readonly AudioSettings _settings;
    private readonly Query<Transform, ReverbZone> _zones;

    private double _last = double.NaN;
    private int _cursor;
    private int _budget;
    private int _update = 1;   // which update this is, so a voice gets at most one ray per update

    // Reverb: blending from `_from` to `_to` over `_fade` seconds, `_t` of the way there.
    private ReverbMix _from = ReverbMix.Dry;
    private ReverbMix _to = ReverbMix.Dry;
    private float _t = 1f;
    private float _fade = ReverbZone.DefaultFade;
    private float _zoneFade = ReverbZone.DefaultFade;

    public AudioEnvironment(World world, AudioSettings settings)
    {
        _world = world;
        _settings = settings;
        _zones = world.Query<Transform, ReverbZone>();
    }

    // How much an occluded voice keeps of its volume, and of its high frequencies.
    public float OccludedGain { get; set; } = 0.45f;

    public float OccludedLowPass { get; set; } = 0.15f;

    // Seconds for occlusion to go all the way from clear to blocked.
    public float Smoothing { get; set; } = 0.2f;

    // How far short of the emitter the ray stops, in metres.
    public float EndMargin { get; set; } = 0.5f;

    // Rays cast by the last update (and the plays after it), for `snd_stats` and the budget's test.
    public int RaysLastUpdate { get; private set; }

    public int RaysTotal { get; private set; }

    // The reverb heard now, blended.
    public ReverbMix Reverb { get; private set; } = ReverbMix.Dry;

    // The preset of the zone the listener is in, or empty outside every zone.
    public RecordId Zone { get; private set; }

    // How far the blend into `Zone` has gone, 0..1.
    public float Blend => _t;

    // Once a frame, from AudioMixer.Update, before the voices' gains are computed.
    public void Update(AudioMixer mixer, double now)
    {
        float dt = double.IsNaN(_last) ? 0f : (float)Math.Clamp(now - _last, 0.0, 0.25);
        _last = now;
        _update++;
        _budget = Math.Max(_settings.OcclusionRays, 0);
        RaysLastUpdate = 0;

        UpdateReverb(mixer.ListenerPosition, dt);
        UpdateOcclusion(mixer, dt);
    }

    // A voice has just started: its first ray, if this update has any left (Update is called before the
    // frame's plays, so these share its budget).
    public void Started(AudioMixer mixer, Voice voice)
    {
        if (!Wants(mixer, voice)) return;
        if (_settings.OcclusionRays <= 0 || _budget <= 0) return;
        _budget--;
        Probe(mixer, voice);
        voice.Occlusion = voice.OcclusionTarget;
        Apply(voice);
    }

    // ---- occlusion -----------------------------------------------------------------------------------

    private void UpdateOcclusion(AudioMixer mixer, float dt)
    {
        var voices = mixer.Voices;
        bool on = _settings.OcclusionRays > 0;

        if (on && voices.Count > 0)
        {
            // Unasked voices first, at once; then round-robin from where the last update stopped.
            for (int i = 0; i < voices.Count && _budget > 0; i++)
            {
                var voice = voices[i];
                if (voice.OcclusionProbed || !Wants(mixer, voice)) continue;
                _budget--;
                Probe(mixer, voice);
                voice.Occlusion = voice.OcclusionTarget;
            }

            if (_cursor >= voices.Count) _cursor = 0;
            for (int seen = 0; seen < voices.Count && _budget > 0; seen++)
            {
                var voice = voices[_cursor];
                _cursor = (_cursor + 1) % voices.Count;
                if (!voice.OcclusionProbed || !Wants(mixer, voice) || voice.OcclusionUpdate == _update) continue;
                _budget--;
                Probe(mixer, voice);
            }
        }

        float step = Smoothing <= 0f ? 1f : dt / Smoothing;
        foreach (var voice in voices)
        {
            if (!on) voice.OcclusionTarget = 0f;
            voice.Occlusion = MoveTowards(voice.Occlusion, voice.OcclusionTarget, step);
            Apply(voice);
        }
    }

    // Worth a ray: positional, playing, and within earshot (past MaxDistance it is silent anyway).
    private static bool Wants(AudioMixer mixer, Voice voice)
    {
        if (!voice.Positional || voice.Stopping || voice.Record == null) return false;
        return Vector3.DistanceSquared(voice.Position, mixer.ListenerPosition) < voice.Record.MaxDistance * voice.Record.MaxDistance;
    }

    private void Probe(AudioMixer mixer, Voice voice)
    {
        RaysLastUpdate++;
        RaysTotal++;
        voice.OcclusionProbed = true;
        voice.OcclusionUpdate = _update;

        float target = 0f;
        var to = voice.Position - mixer.ListenerPosition;
        float distance = to.Length();
        float reach = distance - EndMargin;
        if (reach > 0.01f && _world.Resources.TryGet<IPhysicsWorld>(out var space) && space != null)
        {
            var layers = space.Layers;
            var mask = LayerMask.All.Except(layers.Player).Except(layers.Enemy).Except(layers.Trigger);
            var hit = space.Raycast(mixer.ListenerPosition, to / distance, reach, mask);
            if (hit.Hit) target = 1f;
        }

        if (target != voice.OcclusionTarget)
            Log.Trace(LogCat.Audio, $"occlusion {voice.Sound} {(target > 0f ? "blocked" : "clear")} " +
                                    $"at {distance:F1} m (now {voice.Occlusion:F2})");
        voice.OcclusionTarget = target;
    }

    private void Apply(Voice voice)
    {
        voice.OcclusionGain = 1f - voice.Occlusion * (1f - Math.Clamp(OccludedGain, 0f, 1f));
        voice.LowPass = 1f - voice.Occlusion * (1f - Math.Clamp(OccludedLowPass, 0f, 1f));
    }

    private static float MoveTowards(float value, float target, float step) =>
        value < target ? MathF.Min(value + step, target) : MathF.Max(value - step, target);

    // ---- reverb --------------------------------------------------------------------------------------

    private void UpdateReverb(Vector3 listener, float dt)
    {
        RecordId want = default;
        ReverbMix target = ReverbMix.Dry;
        float fade = _zoneFade;

        if (ReverbZones.Find(_zones, listener, out var zone))
        {
            want = zone.Reverb;
            fade = zone.FadeOrDefault;
        }

        if (want != Zone)
        {
            if (!want.IsEmpty)
            {
                if (_world.Engine != null && _world.Engine.Records.TryGet(want, out ReverbRecord record))
                    target = ReverbMix.From(record);
                else
                    Log.Once(LogCat.Audio, LogLevel.Warn, $"reverb-missing:{want}", $"reverb zone names '{want}', which is not a reverb record; it plays dry");
            }
            else
            {
                // Out of every zone: the tail keeps its character and fades.
                target = Reverb with { Mix = 0f };
            }

            Log.Trace(LogCat.Audio, want.IsEmpty
                ? $"reverb off (fading over {fade:F1} s)"
                : $"reverb {want} mix {target.Mix:F2} decay {target.DecayTime:F2} s (fading over {fade:F1} s)");

            _from = Reverb;
            _to = target;
            _t = 0f;
            _fade = fade;
            if (!want.IsEmpty) _zoneFade = fade;
            Zone = want;
        }

        if (_t < 1f)
        {
            _t = _fade <= 0f ? 1f : MathF.Min(1f, _t + dt / _fade);
            Reverb = ReverbMix.Lerp(_from, _to, _t);
        }
    }
}
