# 11 — Audio (short)

> **From the slice retrospective (2026-09-23), finding 4.** `CueTriggered` is raised by every cast and
> every impact and **nothing listens**, because audio is a later phase. That is the design working — the
> simulation says *what happened*, never what it sounds like — but it means the finished slice is
> silent, which is the largest single gap between "it runs" and "it is a game". The events are already
> on the bus, so nothing that raises them changes when this doc is built. See [`../history/vertical-slice-2026-09-23.md`](../history/vertical-slice-2026-09-23.md).

## 1. Purpose and scope
Positional sound effects, ambient loops, music and volume buses. A client module (`Sage.Client`). Expanded when roadmap Phase 1's audio item (F4) starts.

## 3. Key decisions
- **Backend:** MonoGame `SoundEffect`/`SoundEffectInstance` with `Apply3D(AudioListener, AudioEmitter)` for basic 3D panning and attenuation, behind an `IAudioBackend` interface. That allows a later move to OpenAL Soft or FMOD for occlusion and reverb (ARCHITECTURE §6).
- **The simulation doesn't play sounds.** Gameplay sends game events (`Damaged`, `SpellCast`, footsteps from animation events, 12). Presentation systems in the Frame schedule read them with their own cursors (04 §3.1) and start sounds. Persistent sources (a waterfall, a campfire) are an `AudioSource` component.
- **Sound definitions are records** (`sound`): variations (a list of `AssetPath`s), volume/pitch random ranges, bus, max concurrent instances, cooldown, attenuation (min/max distance). Gameplay refers to `RecordId`s, never to files.
- **Buses:** `Master`, `Music`, `Sfx`, `Voice`, `Ui`, `Ambient`, with volumes from cvars (`snd_volume_master` etc., `Archive`).
- **Voice limiting:** a cap on simultaneous instances (`snd_maxvoices`). Priority is by bus, distance and definition priority; the quietest voice is stolen.
- **The listener** is the active camera (06), in camera-relative space (the same as rendering).

## 4. API sketch
```csharp
[Record("sound")] public sealed class SoundDef { public List<AssetPath> Variations; public float Volume = 1, VolumeJitter, PitchJitter; public AudioBus Bus; public int MaxInstances = 4; public float MinDistance = 1, MaxDistance = 30; }

public struct AudioSource { public RecordId Sound; public bool Loop, PlayOnSpawn; public float Volume; [Transient] public int VoiceHandle; }

public sealed class AudioSystem                      // client module service
{
    public VoiceHandle Play(RecordId sound, Vector3 originSpacePosition, float volume = 1f);
    public VoiceHandle Play2D(RecordId sound, float volume = 1f);
    public void Stop(VoiceHandle v);
    public void SetBusVolume(AudioBus bus, float volume);
}
```

## 11. v1 scope vs later
- **v1:**
  - WAV one-shots and loops;
  - `sound` records;
  - the event-driven playback pattern;
  - `AudioSource`;
  - buses + cvars;
  - voice limiting;
  - `snd_list`/`snd_stats`;
  - log category `Audio`.
- **Later:**
  - streamed OGG music with crossfades (decoder choice in 05 Open questions);
  - reverb zones and occlusion;
  - a backend swap;
  - voice/dialogue playback with subtitles (with the Narrative module).

## 14. Build steps
1. `IAudioBackend` over MonoGame + `AudioSystem` + buses (TODO F4).
2. `sound` records + event-driven presentation systems.
3. `AudioSource` component + listener from the active camera.
