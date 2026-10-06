# 11 · Audio

> Status: built for v1 (phase 4o). WAV and OGG one-shots and loops play through a headless, tested mixer (buses, voice limits, distance rolloff curves, pan, cones, doppler, occlusion, reverb zones) and a MonoGame backend; an OGG streams as it plays (#326) and music crossfades with layers and loop points (#325). Footsteps follow the surface underfoot (#327), effects, attacks and screens raise sounds from data (#330), and the editor has an audio panel and `snd_debug` (#336). Left: see section 10. Owning assemblies: `Sage.Simulation` (records and mixer), `Sage.Client` (system and backend), `Sage.Gameplay` (cue records and events). Design doc: [11 Audio](../../design/11-audio.md).

## 1. Purpose and scope

Audio makes the simulation audible without the simulation knowing about sound. Gameplay raises events (a cue, a hit, an item use) and content says what each one sounds like. The mixer decides which voices play and how loud and where; a backend turns that into output.

It deliberately does not do: sound design or content, speech and lip-sync (dialogue, [16](16-gameplay.md)), or 3D audio middleware (FMOD and the like are not used; occlusion and reverb go through OpenAL's EFX extension under MonoGame, #329).

## 2. Responsibilities

- Define a `sound` record: variations, volume and pitch jitter, bus, instance cap, cooldown, distance range and rolloff curve, cone, doppler scale, priority, and whether it streams.
- Play music from `music` records: one streamed voice per stem, crossfaded, outside the voice cap and never stolen.
- Occlude sounds behind walls and put the listener in a reverb zone.
- Run a mixer that applies buses, distance falloff and pan, caps voices, steals the quietest or oldest, and refuses quietly when full.
- Turn `CueTriggered`, `Damaged`, `Used` and screen `SoundRequested` events and `AudioSource` entities into voices.
- Follow the listener (the main view: player camera, a scripted cut or the editor's free camera) and stay correct when the world origin rebases.
- Give each world its own mixer and share one set of bus volumes.
- Expose bus volumes as archived cvars.

Not responsible for: choosing what a cue sounds like (the `cue` record does), loading the audio device (the platform sheet, [01](01-platform.md)), or deciding when footsteps happen (the `footsteps` part, [10](10-animation.md) and design 10, #327).

## 3. Placement and dependencies

`AudioMixer`, `Voice`, `AudioSettings`, `SoundRecord`, `AudioSource`, `Music` and `MusicPlayer`, `AudioEnvironment` and `ReverbRecord`, and `PcmStreamer` are in `Sage.Simulation/Audio/`, so the mixer runs and is tested headlessly with no MonoGame. `AudioSystem`, the backend and `OpenAlEffects` are in `Sage.Client/Audio/`, because only they touch MonoGame's `SoundEffect`. `CueRecord` and `CueTriggered` are in `Sage.Gameplay/Abilities/Abilities.cs`; the client reads them, never the other way round.

Plugin ids: `sage.client` registers the `sound` record and the audio system and cvars; `sage.gameplay.abilities` owns the `cue` record. The base never references the client.

## 4. Interfaces

| Type | Role | File |
|---|---|---|
| `AudioMixer` | `Play`, `PlayMusic`, `Stop`, `StopAll`, `Move`, `Update`, `Rebase`, `Invalidate(asset)`, listener; counters `Refused` and `Stolen`; `Environment` for occlusion and reverb | `src/Sage.Simulation/Audio/AudioMixer.cs` |
| `Voice`, `VoiceHandle` | One playing sound with computed `Gain` and `Pan`; a handle callers keep | same |
| `AudioSettings` | Per-bus volumes (`Master`, `Music`, `Sfx`, `Voice`, `Ui`, `Ambient`) and `MaxVoices` | same |
| `IAudioBackend` | `Apply(mixer)`; `NullAudioBackend` and `MonoGameAudioBackend`; streamed voices are fed a few buffers at a time, the low-pass and reverb go through OpenAL EFX | `src/Sage.Client/Audio/AudioBackend.cs`, `OpenAlEffects.cs` |
| `Music`, `MusicRules`, `MusicPlayer` | The world's music (saved), what content calls to change it, and the player that turns it into voices | `src/Sage.Simulation/Audio/Music.cs` |
| `PcmStreamer` | Decodes an Ogg as it plays through a bounded set of buffers; loops with and without loop points | `src/Sage.Simulation/Audio/PcmStreamer.cs` |
| `AudioEnvironment` | Budgeted occlusion rays and the reverb zone the listener is in | `src/Sage.Simulation/Audio/AudioEnvironment.cs` |
| `AudioSystem` | Reads events and sources, sets the listener, calls the backend | `src/Sage.Client/Audio/AudioSystem.cs` |
| `CueTriggered(Cue, Source, Point)` | The simulation's "something happened here, show it" event | `src/Sage.Gameplay/Abilities/Abilities.cs` |

Cvars: `snd_enabled`, the bus volumes `snd_volume` (master), `snd_sfx`, `snd_music`, `snd_ui`, `snd_ambient`, `snd_voice`, `snd_maxvoices`, and `snd_occlusion_rays` (rays a frame, 8; 0 turns occlusion off). Commands: `snd_stats`, `snd_play <sound>` and the cheat commands `music [id|stop] [seconds]` and `music_intensity <0..1>`. The trace log on the `Audio` category says why a sound played or was refused, and names the occlusion factor and the reverb zone.

Events consumed: `CueTriggered`, `Damaged` (the attack's own sound, else its damage type's, else the default damage type's), `Used` (the item's sound) and the UI's `SoundRequested`; all are read on the fixed schedule that sent them. None are raised by this subsystem.

## 5. Data model

| Declaration | Kind | Holds |
|---|---|---|
| `sound` | Record | `Variations` (`[AssetKind("sound")]`), `Volume`, `VolumeJitter`, `PitchJitter`, `Bus`, `MaxInstances`, `Cooldown`, `MinDistance`, `MaxDistance` (0 means 2D), `Priority`, `Stream` (Ogg only) |
| `cue` | Record | `Description`, `Sound`, `Particles` (`Rumble` is the gamepad sheet's, [12](12-input.md)) |
| `music` | Record | `Track`, `Layers` (`Asset`, `Volume`, `Intensity`, `Fade`), `Volume`, `Loop`, `LoopStart`, `LoopEnd` (samples per channel), `FadeIn`, `FadeOut`; `.ogg` only |
| `reverb` | Record | `Mix`, `DecayTime`, `DecayHFRatio`, `Density`, `Diffusion`, `Gain`, `GainHF`, `ReflectionsGain`, `ReflectionsDelay`, `LateGain`, `LateDelay`; four engine presets, `sage:room`, `hall`, `cave` and `outdoors` |
| `ui_sounds` | Record | `Move`, `Select`, `Open`, `Close`; `sage:default_ui_sounds` is the game's default and a `screen` may name its own (`Sounds`) |
| `sage:reverb_zone` | Component | `Reverb`, `Size`, `Offset`, `Priority`, `Fade`; a `reverb_zone` prefab part, or a `"trigger" "1"` map brush entity whose prefab has one |
| `music` | Saved resource | The track playing, the one going out, the blend and the intensity; not the position |
| `sage:audio_source` | Component | `Sound`, `Loop`, `Volume`; the `Voice` is transient, so a load restarts the loop |

Other records name sounds by id: damage types, items and cues carry a `Sound` reference. A `sound` also has `Rolloff` (`linear`, `log` or `custom` with a `RolloffCurve`), a cone (`ConeInner`, `ConeOuter`, `ConeOuterGain`) and a `Doppler` scale (#335). Sound files are WAV or OGG. An OGG is decoded whole to PCM on load (mono or stereo, up to 600 s) unless the sound says `"stream"`, when it is read as it plays (Ogg only; a streamed `.wav` is a load error). A `.wav` and an `.ogg` are hot reloaded.

## 6. Lifecycle and data flow

`sage.client.audio` runs in `Phase.FrameUpdate`, per frame, not per tick. It sets the listener, updates the mixer with real time, drains the event readers (even when sound is off, so the event bus does not grow), starts and follows `AudioSource` voices, stops voices whose source vanished, then calls the backend. Voices are positioned in origin space and move on `Origin.Rebased`. A reloaded sound file stops its one-shots and restarts its loops (test: ReplacingAFileStopsItsOneShotsRestartsItsLoopsAndLeavesOthersAlone). A previewed sound (`snd_play`, the editor's Audio panel) is the exception: it restarts, or plays again if it had ended, so a saved file is heard at once (test: ReplacingAPreviewedFileRestartsItsVoiceOrPlaysItAgainWhenItHasEnded). Gameplay one-shots are not replayed.

Loops belonging to a source are kept in step with it; an entity destroyed or a sector unloaded stops its voice. Music is the world's, advanced in the simulation by `sage.world.music` (Late) and followed by the client's `MusicPlayer` each frame. Nothing else about audio is saved except the `AudioSource` component and the `music` resource (which track and the intensity; a load restarts the track from its top).

## 7. Threading, memory and performance

The mixer and system run on the main thread. A world has its own mixer; bus volumes are shared (test: WorldsHaveTheirOwnMixerAndShareOneSetOfVolumes). Voice count is capped (`snd_maxvoices`, default 32), and so is each sound (`MaxInstances`), so a crowd of footsteps cannot flood it. The mixer creates a `Voice` object per play; no allocation-free measurement exists for it. A streamed sound or music track keeps four buffers of 4096 frames and reuses them, so ten minutes of Ogg allocate nothing after it starts (tests: ThreeMinutesOfOgg_StreamThroughTheSameFewBuffers, TenMinutesStreamed_AllocateNothing). Those buffers are filled once a frame, so a frame longer than about 370 ms underruns. Occlusion costs at most `snd_occlusion_rays` ray casts a frame, shared round-robin by the voices (test: TheRayBudgetIsRespectedAndEveryVoiceIsReached).

## 8. Errors and diagnostics

A sound with no variations is refused quietly and counted (test: NothingToPlayIsRefusedQuietly). Refusals say why on the trace log: cooling down, too many of it, every voice busy with something louder. `snd_stats` shows voices, refusals and steals, the reverb zone and the rays cast, and each voice's gain, pan, occlusion and low-pass; `snd_play` plays a record 2D at the listener, as a preview. `snd_debug` (dev tools) overlays the bus levels and the active voices loudest first with their distances, and the editor's Audio panel draws a meter per bus and a play button for any sound (test: BusMetersAddUpTheirVoicesGainsAndMasterMetersEverything, VoiceRowsAreLoudestFirstWithDistancesAndSkipStoppingVoices). A cue, sound or damage-type reference to a missing record is a load error like any reference, and so is a sound whose file is neither `.wav` nor `.ogg`, a streamed one that is a `.wav`, and a `music` record whose track or layer is not an `.ogg` (tests: ASoundOfAnotherKind_OrAStreamedWav_IsALoadError). Without OpenAL's EFX the backend plays occlusion as gain only and warns once.

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
| REQ-AUD-08 | Music shall be a streamed, crossfaded system that is never voice-stolen. | Must | Done | test: TwoTracksCrossfadeAcrossTicks_AndTheOneGoingOutStops, test: MusicVoicesAreOutsideTheCap_AndNeverStolen, test: ALoopWithPoints_PlaysTheIntroOnceAndTheBodyAgain, test: LayersFollowTheIntensity_OverTheirOwnFade, test: TheMusicSurvivesASave, test: TheSandbox_SwitchesMusicOnATriggerAndAScene |
| REQ-AUD-09 | The engine shall decode OGG and stream long audio with bounded memory. | Must | Done | test: AnOggFileDecodesToPcmMonoOrStereo, test: AFileThatIsNotOggVorbisIsRefusedWithInvalidData, test: ALoopWrapsWithoutAGap_TheFileRepeatedSampleForSample, test: ThreeMinutesOfOgg_StreamThroughTheSameFewBuffers, test: AStreamedSound_LoadsAndOpensThroughTheVfs, test: ASoundOfAnotherKind_OrAStreamedWav_IsALoadError, test: TenMinutesStreamed_AllocateNothing |
| REQ-AUD-10 | Footsteps and surface sounds shall come from animation events and ground surfaces. | Must | Done | test: EachSurfaceRaisesItsOwnCue_AndAnythingElseTheDefaults, test: ACharacterStepsByDistance_AndJumpsAndLandsOnItsSurface, test: TheSandboxsWalkerAndPlayerStepOnTheirOwnSurfaces |
| REQ-AUD-11 | Effect, attack and screen actions shall raise sounds from data. | Should | Done | test: AnEffectRaisesItsAppliedTickedAndRemovedCuesAtItsTarget, test: RemovingAnEffectRaisesItsRemovedCuesOnce, test: AnEffectWithoutCuesIsSilentAndEachMomentHasItsOwn, test: AnAttackNamesItsOwnSoundAndOtherwiseTheDamageTypesIsUsed, test: ScreensRaiseTheirSoundsForOpenMoveSelectAndClose, test: AScreenNamesItsOwnSoundsAndFallsBackToTheDefaultForTheRest, test: AScreenSoundPlaysThroughTheUiBus |
| REQ-AUD-12 | Interiors shall have occlusion, obstruction and reverb zones. | Should | Done | test: AWallBrushBetweenListenerAndEmitterLowersTheGain, test: TheRayBudgetIsRespectedAndEveryVoiceIsReached, test: OcclusionIsSmoothed, test: ASoundStartingBehindAWallStartsMuffled, test: EnteringAReverbZoneBlendsItsPresetIn, test: OverlappingZonesPickByPriorityThenSize, test: TheEnginePresetsLoadAndAnUnknownOnePlaysDry, test: TheTraceNamesTheOcclusionFactorAndTheZone |
| REQ-AUD-13 | The mixer shall support doppler, rolloff curves and directional emitters. | Could | Done | test: AnApproachingEmitterRaisesPitchAndARecedingOneLowersIt, test: AMovingListenerShiftsAStillEmitter, test: ASuppliedVelocityIsUsedInsteadOfTheDerivedOne, test: DopplerScaleZeroDisablesItAndAnExtremeSpeedIsClamped, test: ARebaseIsNotAMovement, test: EachRolloffCurveShapesTheGain, test: AnEmptyCustomCurveFallsBackToLinear, test: ACone_QuietensTheListenerOffAxis, test: TheRolloffIsASoundRecordFieldThatLoadsFromJson |
| REQ-AUD-14 | Sounds shall hot reload with editor-visible bus levels and a preview. | Could | Done | test: ReplacingAFileStopsItsOneShotsRestartsItsLoopsAndLeavesOthersAlone, test: ReplacingAPreviewedFileRestartsItsVoiceOrPlaysItAgainWhenItHasEnded, test: APreviewIsTwoDimensionalAndIgnoresCooldown, test: BusMetersAddUpTheirVoicesGainsAndMasterMetersEverything, test: VoiceRowsAreLoudestFirstWithDistancesAndSkipStoppingVoices |
| REQ-AUD-15 | A headless test shall cover the client's audio system and backend seam. | Should | Not started | Not filed yet |

## 10. Open work

Milestone 5, audio and input (epic #324) is built on the audio side: #325, #326, #327, #329, #330, #335 and #336 are done. What is left, by what each one's limits say:

- **Streaming:** a streamed voice is fed four buffers of 4096 frames once a frame, so a frame longer than about 370 ms underruns; there is no separate music asset kind (a track is an `.ogg` named by a `music` record); a source that cannot seek is copied into memory still compressed; the streamed path is not heard in CI (only the buffers' contents are tested).
- **Music:** the position is not saved (a load restarts the track); stems are not sample-locked to each other; there is no ducking; the crossfade is equal-power and a third track replaces the quieter of the two.
- **Footsteps:** no ladder or swim steps; no per-foot surface on sprites; `games/Sandbox/tools/make_placeholder_audio.py` does not reproduce `fire_loop`, `rain_loop` or `thunder`.
- **Occlusion and reverb:** occlusion is binary per ray (no thickness or material), so a grazing hit may read as occluded; the reverb send is not filtered by occlusion; zones are axis-aligned boxes; a sound cannot opt out; 2D sounds are never occluded; an occluded voice keeps 0.45 gain and 0.15 high-frequency gain; the EFX path is not heard on real speakers yet.
- **Doppler and cones:** velocity comes from position, a physics body's is not wired in (`AudioMixer.SetVelocity` and `SetListenerVelocity` take one); one-shots and cues have no direction, so cones work only on `AudioSource` emitters; `snd_stats` shows no pitch.
- **Tools:** `snd_stats` has no distance column (the overlay has); gameplay one-shots are not replayed on a hot reload (an `AudioSource` loop is).
- REQ-AUD-15, a headless test of the client's audio system and its backend seam, is not filed.

## 11. References

- [Design 11](../../design/11-audio.md): the plan, scope and what was built (music, streaming, occlusion and reverb, effect cue moments).
- [Design 16](../../design/16-gameplay-framework.md) §3.3: cues.
- [REDESIGN](../../REDESIGN.md) §5, phase 4o.
- Siblings: [Platform](01-platform.md), [Animation](10-animation.md), [Input](12-input.md), [Gameplay](16-gameplay.md); parent [SRS](../SRS.md).
