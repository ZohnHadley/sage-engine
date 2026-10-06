#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// What a sound *is*, as data (docs/design/11 §3, TODO F4).
//
// In the engine rather than the client, for the same reason `sprite_sheet` is: gameplay and content
// refer to sounds by `RecordId` and never to files, a headless server loads and validates the records
// like any other, and only the thing that actually makes a noise is MonoGame's.
//
// The parts that matter are the ones that stop a game sounding like a machine: **variations** (four
// footsteps, picked at random, instead of one played four times), **jitter** on volume and pitch, and
// a **cooldown** so two hits in the same tick do not phase against each other.

public enum AudioBus { Master, Music, Sfx, Voice, Ui, Ambient }

// How a positional sound fades between its MinDistance and MaxDistance (issue #335).
public enum Rolloff
{
    Linear,   // a straight line from full to silent: the default, predictable from two numbers
    Log,      // inverse distance (Min / d), rescaled to reach silence at Max: loud close up, long tail
    Custom,   // the record's own `rolloffCurve`, gains evenly spaced from Min to Max
}

[Record("sound", Plugin = "sage.client")]
public sealed class SoundRecord
{
    // One of these is picked each time it plays. A single-entry list is the normal case.
    [AssetKind("sound")] public List<AssetPath> Variations = new();

    [Property(Min = 0, Max = 1, Tooltip = "Loudness before the bus and distance apply")]
    public float Volume = 1f;            // 0..1 before the bus and distance are applied
    public float VolumeJitter;           // ± this, so repeats are not identical
    public float PitchJitter;            // ± semitone-ish fraction, -1..1 in MonoGame's terms
    public AudioBus Bus = AudioBus.Sfx;

    // How many of *this* sound may play at once. A crowd of forty footsteps is noise, not footsteps.
    public int MaxInstances = 4;

    // Seconds before the same sound may start again. Two swings landing on the same tick sound like
    // one swing with a flanger; this is what stops that.
    public float Cooldown;

    // Full volume within Min, silent past Max, and 2D (no attenuation, no pan) when Max is 0.
    public float MinDistance = 3f;
    public float MaxDistance = 40f;

    [Property(Tooltip = "How the sound fades from MinDistance to MaxDistance: linear, log (inverse distance) or custom (rolloffCurve)")]
    public Rolloff Rolloff = Rolloff.Linear;

    // For `custom`: gains (0..1) at evenly spaced distances from Min to Max, linearly interpolated.
    // Empty falls back to linear.
    [Property(Tooltip = "Custom rolloff: gains from 1 at MinDistance to 0 at MaxDistance, evenly spaced, e.g. [1, 0.6, 0.2, 0]")]
    public List<float> RolloffCurve = new();

    // A directional emitter (a megaphone, a speaker): full volume inside the inner cone, OuterGain
    // outside the outer one, a blend between. Full angles in degrees about the emitter's forward; 360
    // is no cone. Needs a direction: an AudioSource's entity forward.
    [Property(Min = 0, Max = 360, Unit = "deg", Tooltip = "Inner cone, full angle about the emitter's forward: full volume inside. 360 = omnidirectional")]
    public float ConeInner = 360f;

    [Property(Min = 0, Max = 360, Unit = "deg", Tooltip = "Outer cone, full angle: OuterGain beyond it")]
    public float ConeOuter = 360f;

    [Property(Min = 0, Max = 1, Tooltip = "Gain outside the outer cone")]
    public float ConeOuterGain = 0.25f;

    // Scales the doppler shift for this sound: 0 makes it immune (music-like), 1 is the world's.
    [Property(Min = 0, Max = 4, Tooltip = "Doppler scale for this sound; 0 = never shifts")]
    public float Doppler = 1f;

    // Higher wins when voices are stolen. A death rattle outranks a footstep.
    public int Priority;

    // Decoded as it plays, a few buffers at a time, instead of whole when it loads (issue #326): music,
    // ambience beds and dialogue, minutes long, which would be tens of megabytes each as PCM. Ogg Vorbis
    // only; a loop wraps without a gap.
    [Property(Tooltip = "Decode as it plays instead of whole: for music, ambience and dialogue. Ogg Vorbis (.ogg) only")]
    public bool Stream;

    public bool Is2D => MaxDistance <= 0f;

    // What a sound's files are (issue #326): a `.wav` or an `.ogg`, which are what the client can play, and
    // only `.ogg` when it streams. Run wherever the record type is registered (the client, `sage validate`).
    internal static void Check(SoundRecord sound, RecordCheck check)
    {
        for (int i = 0; i < sound.Variations.Count; i++)
        {
            var asset = sound.Variations[i];
            if (asset.IsEmpty) continue;
            string path = asset.Path.Value;
            bool ogg = path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);
            if (!ogg && !path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                check.Error($"variations[{i}]", $"'{path}' is neither a .wav nor an .ogg, which are the sounds the engine plays");
            else if (sound.Stream && !ogg)
                check.Error($"variations[{i}]", $"'{path}' is a .wav, and only Ogg Vorbis streams; encode it as .ogg or drop \"stream\"");
        }
    }
}

// A sound that lives on an entity: a waterfall, a campfire, a torch (11 §3). The system starts it when
// the entity appears and stops it when the entity goes.
[Component("sage:audio_source")]
public struct AudioSource : IComponent
{
    [RecordRef("sound")] public RecordId Sound;
    public bool Loop;
    public float Volume;

    // The voice it is using, or 0. Transient because a voice is this run's: a save restores the
    // source, and the sound starts again on its own.
    [Transient] public int Voice;
}
