# 11 · Audio

> Status: partly built. WAV one-shots and loops play through a headless, tested mixer (buses, voice limits, distance, pan) and a MonoGame backend. Music, OGG decoding and streaming, occlusion, reverb, surface footsteps and doppler are not built. Owning assemblies: `Sage.Simulation` (records and mixer), `Sage.Client` (system and backend), `Sage.Gameplay` (cue records and events). Design doc: [11 Audio](../../design/11-audio.md).

## 1. Purpose and scope

Audio makes the simulation audible without the simulation knowing about sound. Gameplay raises events (a cue, a hit, an item use) and content says what each one sounds like. The mixer decides which voices play and how loud and where; a backend turns that into output.

It deliberately does not do: sound design or content, speech and lip-sync (dialogue, [16](16-gameplay.md)), or 3D audio middleware (OpenAL or FMOD are not used; an occlusion and reverb backend decision is part of #329).

## 2. Responsibilities

- Define a `sound` record: variations, volume and pitch jitter, bus, instance cap, cooldown, distance range, priority.
- Run a mixer that applies buses, distance falloff and pan, caps voices, steals the quietest or oldest, and refuses quietly when full.
- Turn `CueTriggered`, `Damaged` and `Used` events and `AudioSource` entities into voices.
- Follow the listener (the main view: player camera, a scripted cut or the editor's free camera) and stay correct when the world origin rebases.
- Give each world its own mixer and share one set of bus volumes.
- Expose bus volumes as archived cvars.

Not responsible for: choosing what a cue sounds like (the `cue` record does), loading the audio device (the platform sheet, [01](01-platform.md)), or deciding when footsteps happen (animation events, [10](10-animation.md), planned in #327).

## 3. Placement and dependencies

`AudioMixer`, `Voice`, `AudioSettings`, `SoundRecord` and `AudioSource` are in `Sage.Simulation/Audio/`, so the mixer runs and is tested headlessly with no MonoGame. `AudioSystem` and the backend are in `Sage.Client/Audio/`, because only they touch MonoGame's `SoundEffect`. `CueRecord` and `CueTriggered` are in `Sage.Gameplay/Abilities/Abilities.cs`; the client reads them, never the other way round.

Plugin ids: `sage.client` registers the `sound` record and the audio system and cvars; `sage.gameplay.abilities` owns the `cue` record. The base never references the client.

## 4. Interfaces

| Type | Role | File |
|---|---|---|
| `AudioMixer` | `Play`, `Stop`, `StopAll`, `Move`, `Update`, `Rebase`, `Invalidate(asset)`, listener; counters `Refused` and `Stolen` | `src/Sage.Simulation/Audio/AudioMixer.cs` |
| `Voice`, `VoiceHandle` | One playing sound with computed `Gain` and `Pan`; a handle callers keep | same |
| `AudioSettings` | Per-bus volumes (`Master`, `Music`, `Sfx`, `Voice`, `Ui`, `Ambient`) and `MaxVoices` | same |
| `IAudioBackend` | `Apply(mixer)`; `NullAudioBackend` and `MonoGameAudioBackend` | `src/Sage.Client/Audio/AudioBackend.cs` |
| `AudioSystem` | Reads events and sources, sets the listener, calls the backend | `src/Sage.Client/Audio/AudioSystem.cs` |
| `CueTriggered(Cue, Source, Point)` | The simulation's "something happened here, show it" event | `src/Sage.Gameplay/Abilities/Abilities.cs` |

Cvars: `snd_enabled`, the bus volumes `snd_volume` (master), `snd_sfx`, `snd_music`, `snd_ui`, `snd_ambient`, `snd_voice`, and `snd_maxvoices`. Commands: `snd_stats` and `snd_play <sound>`. The trace log on the `Audio` category says why a sound played or was refused.

Events consumed: `CueTriggered`, `Damaged` (the damage type's sound), `Used` (the item's sound); all are read on the fixed schedule that sent them. None are raised by this subsystem.

## 5. Data model

| Declaration | Kind | Holds |
|---|---|---|
| `sound` | Record | `Variations` (`[AssetKind("sound")]`), `Volume`, `VolumeJitter`, `PitchJitter`, `Bus`, `MaxInstances`, `Cooldown`, `MinDistance`, `MaxDistance` (0 means 2D), `Priority` |
| `cue` | Record | `Description`, `Sound`, `Particles` |
| `sage:audio_source` | Component | `Sound`, `Loop`, `Volume`; the `Voice` is transient, so a load restarts the loop |

Other records name sounds by id: damage types, items and cues carry a `Sound` reference. Sound files are WAV.

## 6. Lifecycle and data flow

`sage.client.audio` runs in `Phase.FrameUpdate`, per frame, not per tick. It sets the listener, updates the mixer with real time, drains the event readers (even when sound is off, so the event bus does not grow), starts and follows `AudioSource` voices, stops voices whose source vanished, then calls the backend. Voices are positioned in origin space and move on `Origin.Rebased`. A reloaded sound file stops its one-shots and restarts its loops (test: ReplacingAFileStopsItsOneShotsRestartsItsLoopsAndLeavesOthersAlone), though the editor does not yet preview it (#336).

Loops belonging to a source are kept in step with it; an entity destroyed or a sector unloaded stops its voice. Nothing about audio is saved except the `AudioSource` component.

## 7. Threading, memory and performance

The mixer and system run on the main thread. A world has its own mixer; bus volumes are shared (test: WorldsHaveTheirOwnMixerAndShareOneSetOfVolumes). Voice count is capped (`snd_maxvoices`, default 32), and so is each sound (`MaxInstances`), so a crowd of footsteps cannot flood it. The mixer creates a `Voice` object per play; no allocation-free measurement exists for it. Whole files are loaded into memory, so long audio is not yet affordable (#326).

## 8. Errors and diagnostics

A sound with no variations is refused quietly and counted (test: NothingToPlayIsRefusedQuietly). Refusals say why on the trace log: cooling down, too many of it, every voice busy with something louder. `snd_stats` shows voices, refusals and steals; `snd_play` lets a designer hear a record. A cue, sound or damage-type reference to a missing record is a load error like any reference. There is no overlay for active voices or bus meters yet (#336).

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-AUD-01 | Sounds shall be data records with variations, jitter, bus, cooldown and distance range. | Must | Done | test: VariationsArePickedBetween |
| REQ-AUD-02 | The mixer shall attenuate by distance and pan by the listener's facing, and treat Max 0 as 2D. | Must | Done | test: DistanceSetsTheGain, test: PanFollowsWhereTheListenerIsFacing |
| REQ-AUD-03 | Buses shall multiply with a master bus and have player-adjustable volumes. | Must | Done | test: BusesMultiplyAndMasterCoversThemAll |
| REQ-AUD-04 | Voices shall be limited per sound and overall, stealing the oldest or quietest. | Must | Done | test: TooManyOfOneSoundStealsTheOldest, test: AtTheCapTheQuietestGoesUnlessItMattersMore |
| REQ-AUD-05 | Looping ambience shall be an entity component that follows its entity. | Must | Done | `AudioSource` in `src/Sage.Simulation/Audio/SoundRecord.cs` |
| REQ-AUD-06 | Sounds shall move with an origin rebase. | Must | Done | test: SoundsMoveWithAnOriginRebase |
| REQ-AUD-07 | A cue, a hit and an item use shall be audible from data alone. | Must | Done | `AudioSystem` in `src/Sage.Client/Audio/AudioSystem.cs` |
| REQ-AUD-08 | Music shall be a streamed, crossfaded system that is never voice-stolen. | Must | Not started | #325 |
| REQ-AUD-09 | The engine shall decode OGG and stream long audio with bounded memory. | Must | Not started | #326 |
| REQ-AUD-10 | Footsteps and surface sounds shall come from animation events and ground surfaces. | Must | Not started | #327 |
| REQ-AUD-11 | Effect, attack and screen actions shall raise sounds from data. | Should | Not started | #330 |
| REQ-AUD-12 | Interiors shall have occlusion, obstruction and reverb zones. | Should | Not started | #329 |
| REQ-AUD-13 | The mixer shall support doppler, rolloff curves and directional emitters. | Could | Not started | #335 |
| REQ-AUD-14 | Sounds shall hot reload with editor-visible bus levels and a preview. | Could | Partial: the mixer swaps on reload; no tooling | #336 |
| REQ-AUD-15 | A headless test shall cover the client's audio system and backend seam. | Should | Not started | Not filed yet |

## 10. Open work

Milestone 5, audio and input (epic #324):

- #325 4o-1 Build the music system (streamed, crossfaded, not voice-stolen) (P1)
- #326 4o-2 Decode OGG and stream long audio (P1)
- #327 4o-3 Raise footstep and surface sounds from animation events (P1)
- #329 4o-5 Add occlusion, obstruction and reverb zones (P2)
- #330 4o-6 Finish the cue and sound wiring that is declared but not raised (P2)
- #335 4o-11 Add doppler, spatial 3D improvements and emitter parity (P3)
- #336 4o-12 Audio tooling: bus meters, sound preview and a snd_debug overlay (P3)

## 11. References

- [Design 11](../../design/11-audio.md): the plan, scope and open questions (music second path, effect cue moments).
- [Design 16](../../design/16-gameplay-framework.md) §3.3: cues.
- [REDESIGN](../../REDESIGN.md) §5, phase 4o.
- Siblings: [Platform](01-platform.md), [Animation](10-animation.md), [Input](12-input.md), [Gameplay](16-gameplay.md); parent [SRS](../SRS.md).
