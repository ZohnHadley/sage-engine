#nullable enable
using System;
using System.Collections.Generic;

namespace sage_engine;

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

    // Higher wins when voices are stolen. A death rattle outranks a footstep.
    public int Priority;

    public bool Is2D => MaxDistance <= 0f;
}

// A sound that lives on an entity: a waterfall, a campfire, a torch (11 §3). The system starts it when
// the entity appears and stops it when the entity goes.
[Component("sage:audio_source")]
public struct AudioSource : Friflo.Engine.ECS.IComponent
{
    [RecordRef("sound")] public RecordId Sound;
    public bool Loop;
    public float Volume;

    // The voice it is using, or 0. Transient because a voice is this run's: a save restores the
    // source, and the sound starts again on its own.
    [Transient] public int Voice;
}
