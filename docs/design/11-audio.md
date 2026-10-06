# 11 — Audio (short)

> **From the slice retrospective (2026-09-23), finding 4.** `CueTriggered` is raised by every cast and
> every impact and **nothing listens**, because audio is a later phase. That is the design working — the
> simulation says *what happened*, never what it sounds like — but it means the finished slice is
> silent — which the readiness review (2026-09-24 §5) puts second only to streaming in what to build
> next, because the events already exist at every interesting moment and this is wiring rather than
> design. Nothing that raises them changes when this doc is built. See [`../history/vertical-slice-2026-09-23.md`](../history/vertical-slice-2026-09-23.md).

## 1. Purpose and scope
Positional sound effects, ambient loops, music and volume buses. A client module (`Sage.Client`). Expanded when roadmap Phase 1's audio item (F4) starts.

## 3. Key decisions
- **Backend:** MonoGame `SoundEffect`/`SoundEffectInstance`, behind an `IAudioBackend` interface. The mixer does the 3D sums (distance, pan, cone, doppler, occlusion) and the backend applies the result to the instance; it never calls `Apply3D`. The low-pass filter for occlusion and the reverb go to OpenAL's EFX extension, which MonoGame's DesktopGL already loads (`OpenAlEffects`, #329); the engine did not swap its backend for OpenAL Soft or FMOD.
- **The simulation doesn't play sounds.** Gameplay sends game events (`Damaged`, `SpellCast`, footsteps from animation events, 12). Presentation systems in the Frame schedule read them with their own cursors (04 §3.1) and start sounds. Persistent sources (a waterfall, a campfire) are an `AudioSource` component.
- **Sound files are WAV or OGG** (issue #302). A `.wav` goes to `SoundEffect.FromStream`; an `.ogg` is decoded **whole** to 16-bit PCM when it loads (`OggVorbis`, NVorbis, the decoder MonoGame itself depends on) and handed to a `SoundEffect`, so it is smaller on disk and in a package and the same in memory once loaded. Mono or stereo only, and at most 600 seconds per file (ten minutes of 48 kHz stereo is 110 MB of PCM); a file that is not Ogg Vorbis, has more channels or is longer is refused with `InvalidData` and costs the sound, not the frame (tests: AnOggFileDecodesToPcmMonoOrStereo, AFileThatIsNotOggVorbisIsRefusedWithInvalidData). A sound with `"stream": true` is read as it plays instead (#326, "As built (streaming)" below); both `.wav` and `.ogg` are hot reloaded.
- **Sound definitions are records** (`sound`): variations (a list of `AssetPath`s), volume/pitch random ranges, bus, max concurrent instances, cooldown, attenuation (min/max distance). Gameplay refers to `RecordId`s, never to files.
- **Buses:** `Master`, `Music`, `Sfx`, `Voice`, `Ui`, `Ambient`, with volumes from archived cvars (`snd_volume` for the master, then `snd_sfx`, `snd_music`, `snd_ui`, `snd_ambient`, `snd_voice`).
- **Voice limiting:** a cap on simultaneous instances (`snd_maxvoices`). Priority is by bus, distance and definition priority; the quietest voice is stolen.
- **Attenuation** is linear from `minDistance` to `maxDistance` by default, or `log` (inverse distance, rescaled to reach silence at the max) or `custom` (a `rolloffCurve` of gains), set per sound (#335).
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
  - `snd_play`/`snd_stats`;
  - log category `Audio`.
  - since phase 4o (below): streamed OGG, music with crossfades, footsteps by surface, effect, attack and screen sounds, occlusion and reverb zones, doppler, rolloff curves and cones, the audio panel and `snd_debug`.
- **Later:**
  - voice/dialogue playback with subtitles (with the Narrative module);
  - a second backend (a real 3D middleware), if EFX under MonoGame is not enough.

### As built (F4, 2026-09-24)
The slice is no longer silent. Nothing in `Sage.Simulation`'s gameplay changed to make it audible, which
was the claim this doc made a day earlier: the events were already there.

- **Code:** `src/Sage.Simulation/Audio/` (`SoundRecord`, `AudioBus`, `AudioSource`, **`AudioMixer`**) and
  `src/Sage.Client/Audio/` (`IAudioBackend`, `MonoGameAudioBackend`, `NullAudioBackend`,
  `AudioSystem`). Sounds in `games/Sandbox/content/audio/`, generated by
  `tools/make_placeholder_audio.py`. Tests in `tests/Sage.Tests/Presentation/AudioTests.cs`.
- **Every decision is in the engine, none of the noise is.** `AudioMixer` owns attenuation, panning,
  bus volumes, per-sound limits, cooldowns and voice stealing, and has no MonoGame in it — so 16
  headless tests cover them. The client owns `SoundEffectInstance`, which decides nothing. Same split
  as `Panel`/`PanelView` and `RenderSnapshot`/`Renderer`.
- **The simulation still does not play sounds.** `AudioSystem` reads `CueTriggered`, `Damaged` and
  `Used` with its own cursors and looks up what they sound like: a **cue record** names a sound, a
  **damage type** names a sound, an **item** names a sound. A mod can give a spell a new noise without
  touching the spell.
- **A cue has a moment**, which is a rule this doc learnt the hard way. An ability now has two lists:
  `castCues` where the spell leaves the caster and `cues` where it does its work. They were one list
  raised at both ends, so a fireball's burst was heard in the caster's hand a second before it
  happened — and every test passed, because a test that counts cues cannot hear one in the wrong place.
  An `attack` record has `swingCue` for the same reason: the *swing* is the weapon's own noise and
  plays whether the blow lands or not, while the *hit* is the damage type's.
- **An event carries what its reader needs** (04 §3.2). `Used` says what was taken and where, because
  the sword it describes is destroyed in the same tick it is picked up: the audio system, reading a
  frame later, asked the world for a `Pickup` on a dead entity and played silence.
- **Pan and volume are applied directly, not through `Apply3D`:** the mixer has already done the sums
  in origin space (R6), and handing MonoGame a second listener would mean two sets of numbers that can
  disagree. That still holds after doppler (the mixer works out a voice's pitch and the backend sets
  `Pitch`) and after occlusion and reverb (the mixer says how much, EFX applies it): `Apply3D` was
  never needed and the backend was never swapped.
- **Sounds live in origin space** and move with a rebase, so a waterfall does not stay a kilometre
  behind the world it belongs to.
- **A mixer belongs to a world, and so does its backend.** Both are world resources, like
  `RenderSnapshot`: a voice's position is in *that* world's origin space (R6), and a second world would
  otherwise shift the first one's sounds every time it rebased. What is *not* per world is
  `AudioSettings` — the bus volumes and the voice cap, which every mixer reads, because somebody who
  moves the music slider means all of it, including a world that does not exist yet.
- **`AudioSource`** is the component for things that hum — the Sandbox has a campfire, placed by the
  `audio` prefab part. A source whose entity is destroyed (or whose sector is unloaded) has its voice
  stopped by a per-frame sweep; without it, a campfire that no longer exists burns for ever.
- **No device is a configuration, not a branch.** `NullAudioBackend` runs the whole mixer and plays
  nothing, so a machine with no sound card, a locked audio session and `snd_enabled 0` all behave the
  same way as far as the game is concerned.
- **Console:** `snd_stats` (per world: what is playing, with gain and pan, and what was refused or
  stolen), `snd_play <sound>`, `snd_enabled`, `snd_volume`/`snd_sfx`/`snd_music`/`snd_ui`/
  `snd_ambient`/`snd_voice`, `snd_maxvoices`. **`log_level audio trace`** names every sound as it
  starts, with its gain, its pan and the voice count — and names every *refusal* with the reason, which
  is the whole of "why can I not hear it". Trace is compiled out of Shipping builds (02 §3.2).
- **The placeholders are generated**, like the sprites, by `games/Sandbox/tools/make_placeholder_audio.py`:
  twenty WAVs of arithmetic (a whoosh, thuds, steps, a fire loop with a cross-faded seam, rain and thunder,
  two menu ticks) and four OGG tracks, so the repository ships no audio it does not own. `fire_loop`,
  `rain_loop` and `thunder` are not reproduced byte for byte by a re-run.
- **Not built** (as of F4, since answered): music and crossfades, OGG streaming, reverb and occlusion,
  footsteps from animation events, an attack record overriding its damage type's sound, screens making
  a noise, and effect records raising their cues. Phase 4o built all of them (the sections below); what
  is still not built is voice playback with subtitles.

### As built (OGG streaming, 2026-10-06 — issue #326)
- **Code:** `src/Sage.Simulation/Audio/PcmStreamer.cs` (`IPcmSource`, `VorbisPcmSource`, `PcmStreamer`), the
  `Stream` field of `SoundRecord`, and the streamed path of `MonoGameAudioBackend`. Tests in
  `tests/Sage.Tests/Presentation/AudioStreamingTests.cs`.
- **`"stream": true` on a sound** decodes it as it plays: music, an ambience bed or dialogue, minutes long.
  Ogg Vorbis only. A streamed sound keeps four buffers of 4096 frames, made once; each frame the backend
  refills the ones the device has finished with and queues them, so ten minutes of Ogg cost what ten seconds
  do (tests: ThreeMinutesOfOgg_StreamThroughTheSameFewBuffers, TenMinutesStreamed_AllocateNothing).
- **A loop wraps without a gap:** the source seeks back to the start and the next read is what a read from the
  start would have given, so the file is repeated sample for sample (test:
  ALoopWrapsWithoutAGap_TheFileRepeatedSampleForSample).
- **It goes through the VFS** like any asset, so a folder mount, a mod and a package all work (test:
  AStreamedSound_LoadsAndOpensThroughTheVfs). A source that cannot seek is copied into memory still compressed,
  because a loop has to wind back.
- **Load errors:** a variation must be a `.wav` or an `.ogg`, and a streamed one an `.ogg` (test:
  ASoundOfAnotherKind_OrAStreamedWav_IsALoadError). `asset_list` shows a streamed path as `sound (streamed)`.
- **Limits:** buffers are pumped once a frame, so a frame longer than about 370 ms underruns; there is no
  separate music asset kind; the streamed path is not heard in CI.

### As built (music, 2026-10-06 — issue #325)
- **Code:** `src/Sage.Simulation/Audio/Music.cs` (`MusicRecord`, `MusicLayer`, `Music`, `MusicRules`,
  `MusicSystem`, `MusicPlayer`, the `play_music`, `stop_music` and `music_intensity` actions). Tests in
  `tests/Sage.Tests/Presentation/MusicTests.cs`.
- **A `music` record is a track** (`track`, an `.ogg`), its `layers` (stems: `asset`, `volume`, `intensity`,
  `fade`), `volume`, `loop`, `loopStart` and `loopEnd` (samples per channel; an intro before `loopStart` plays
  once), and `fadeIn` and `fadeOut` in seconds. The loop points are exact: the player seeks to `loopStart`, and
  corrects the decoder's seek offset (test: ALoopWithPoints_PlaysTheIntroOnceAndTheBodyAgain).
- **The world's music is a saved resource** (`music`: the track, the one going out, the blend, the intensity),
  advanced at tick rate by `sage.world.music` (Late), so a server and a save have the same idea of it as the
  client. A load is back in the same track, which starts again from the top (test: TheMusicSurvivesASave).
- **Content changes it with data alone:** a scene's `environment.music` when the player arrives; the inputs
  `PlayMusic "<music> [seconds]"`, `StopMusic "[seconds]"` and `SetMusicIntensity` on any wire; and the actions
  `play_music` (`fade`), `stop_music` and `music_intensity` in a `then`. `MusicRules.Play`, `Stop` and
  `SetIntensity` do the same from code. The cheat commands `music [id|stop] [seconds]` and
  `music_intensity <0..1>` do it from the console.
- **Crossfade:** equal-power, so it is as loud half-way as at either end; a change mid-fade starts from where it
  is (back to the track going out turns the fade round; a third track lets the quieter go) (test:
  TwoTracksCrossfadeAcrossTicks_AndTheOneGoingOutStops). **Layers** are heard while the world's intensity is at
  or above their own, each on its own fade (test: LayersFollowTheIntensity_OverTheirOwnFade).
- **§13's "second mixer path" is answered with both:** the `MusicPlayer` owns a deck of streamed voices for each
  of at most two tracks and asks the mixer for them with a flag (`AudioMixer.PlayMusic`): outside the voice cap,
  never stolen, on the Music bus (test: MusicVoicesAreOutsideTheCap_AndNeverStolen).
- **The Sandbox:** `wander` (with drums at intensity 0.5), `hearth` (the `hut_yard` trigger) and `crypt` (the
  open world's crypt scene) (test: TheSandbox_SwitchesMusicOnATriggerAndAScene).
- **Limits:** the position is not saved; stems are not sample-locked to each other; there is no ducking.

### As built (footsteps, 2026-10-06 — issue #327)
The code and the content are in 10 "As built (surfaces)" and 12; the audio side is that a step is a cue, so
it is a sound like any other: a surface's `footstep`, `land` or `jump` cue picks a `sound` with its variations
and jitter. The Sandbox's walker, the player and its two surfaces are the proof (test:
TheSandboxsWalkerAndPlayerStepOnTheirOwnSurfaces). No ladder or swim steps, and no per-foot surface on sprites.

### As built (effect cues, attack sounds and screen sounds, 2026-10-06 — issue #330)
- **Code:** `EffectRecord` (`Sage.Gameplay/Attributes/Effects.cs`), `AttackRecord.Sound` and `Combat.HitSound`
  (`Combat/Combat.cs`), `UiSoundsRecord` and `UiSounds` (`Sage.UI/UiRecords.cs`), and the client's
  `AudioSystem`. Tests in `tests/Sage.Tests/Gameplay/CueMomentTests.cs` and `tests/Sage.Tests/UI/UiSoundTests.cs`.
- **An effect has three moments, so three lists** (§13's first question): `appliedCues`, `tickCues` and
  `removedCues`, each raised at the target with the effect's source as the cue's source. The old single `cues`
  list is gone (tests: AnEffectRaisesItsAppliedTickedAndRemovedCuesAtItsTarget,
  RemovingAnEffectRaisesItsRemovedCuesOnce, AnEffectWithoutCuesIsSilentAndEachMomentHasItsOwn).
- **An attack can name its own sound** (`attack.sound`): a war hammer and a dagger both do physical damage and
  need not sound alike. Without one the damage type's is used, and without a damage type the conventions' default
  one (test: AnAttackNamesItsOwnSoundAndOtherwiseTheDamageTypesIsUsed).
- **A screen's sounds are a record** (§13's second question): `ui_sounds` has `move`, `select`, `open` and
  `close`. `sage:default_ui_sounds` (every field empty, so silent) is patched by the game, and a `screen` may name
  a set of its own with `sounds`, field by field over the default. They are raised as `SoundRequested`, which
  the client plays on whatever bus the `sound` says (the Sandbox's are `Ui`) (tests:
  ScreensRaiseTheirSoundsForOpenMoveSelectAndClose, AScreenNamesItsOwnSoundsAndFallsBackToTheDefaultForTheRest,
  AScreenSoundPlaysThroughTheUiBus). `move` is raised on navigation input only, not on hover or the initial
  focus; HUD layers and `CloseAll` are silent.

### As built (occlusion and reverb zones, 2026-10-06 — issue #329)
- **Code:** `src/Sage.Simulation/Audio/AudioEnvironment.cs` (occlusion and the zone), `Reverb.cs` (`ReverbRecord`,
  `ReverbMix`, the `sage:reverb_zone` component and the `reverb_zone` part) and, in the client,
  `OpenAlEffects.cs`. Presets in `engine_content/data/audio.json`; the Sandbox's `hut_room` and `crypt_echo` in
  `games/Sandbox/content/data/reverb.json`. Tests in `tests/Sage.Tests/Presentation/AudioEnvironmentTests.cs`.
- **Occlusion is a ray from the listener to each positional voice.** Anything solid in the way (not
  characters, triggers or hitboxes, and the ray stops a little short of the emitter) lowers the
  voice's gain (to 0.45 of itself) and its high frequencies (to 0.15), smoothed over 0.2 s so a door opening is not a
  click (tests: AWallBrushBetweenListenerAndEmitterLowersTheGain, OcclusionIsSmoothed). A sound that starts
  behind a wall starts muffled (test: ASoundStartingBehindAWallStartsMuffled). The rays are **budgeted**:
  `snd_occlusion_rays` a frame (8; 0 turns it off), round-robin over the voices, so every voice is reached
  and a crowd costs no more (test: TheRayBudgetIsRespectedAndEveryVoiceIsReached).
- **Reverb is a zone.** A `reverb` record is a preset (`mix`, `decayTime`, `decayHFRatio`, `density`,
  `diffusion`, `gain`, `gainHF`, `reflectionsGain`, `reflectionsDelay`, `lateGain`, `lateDelay`); the engine ships
  `sage:room`, `sage:hall`, `sage:cave` and `sage:outdoors`. A `reverb_zone` part (or the `sage:reverb_zone`
  component: `reverb`, `size`, `offset`, `priority`, `fade`) is an axis-aligned box; a map brush entity with
  `"trigger" "1"` whose prefab has one is sized from its brushes (a warning if it lacks the `trigger` key).
  The listener blends into the zone it is in over its fade (1 s by default); where zones overlap, higher
  `priority`, then the smaller box, wins (tests: EnteringAReverbZoneBlendsItsPresetIn,
  OverlappingZonesPickByPriorityThenSize, TheEnginePresetsLoadAndAnUnknownOnePlaysDry).
- **EFX does the DSP.** MonoGame's DesktopGL plays through OpenAL Soft, which has the EFX extension; MonoGame
  keeps it internal, so `OpenAlEffects` takes the entry points from `alGetProcAddress` and a playing instance's
  `SourceId` through an `UnsafeAccessor`. One filter serves every voice and one effect slot holds the reverb. No
  EFX: one warning, and occlusion is gain only with no reverb, which is what the mixer's numbers already give.
- **Seeing it:** `snd_stats` names the zone and the rays cast, and each voice's occlusion and low-pass; `log_level
  audio trace` logs `occl X.XX` on a play, `occlusion <sound> blocked|clear at N m` on a change, and
  `reverb <id> mix ... decay ...` or `reverb off` on a zone change (test:
  TheTraceNamesTheOcclusionFactorAndTheZone).
- **Limits:** occlusion is binary per ray (no thickness or material), so a grazing hit may read as occluded; the
  reverb send is not filtered by occlusion; zones are boxes; there is no per-sound opt-out; 2D sounds are never
  occluded; the EFX path is not heard on real speakers yet.

### As built (doppler, rolloff and cones, 2026-10-06 — issue #335)
- **Code:** `SoundRecord` (`rolloff`, `rolloffCurve`, `coneInner`, `coneOuter`, `coneOuterGain`, `doppler`) and
  `AudioMixer` (`Attenuation`, the cone, the doppler sum, `SetVelocity`, `SetListenerVelocity`, `Aim`). Tests in
  `tests/Sage.Tests/Presentation/AudioSpatialTests.cs`.
- **Doppler is in the mixer, not the backend.** A voice's pitch is its jitter plus the shift from the emitter and
  the listener moving along the line between them, against a speed of sound of 343 m/s
  (`AudioSettings.SpeedOfSound`); the backend sets `Pitch`, clamped to MonoGame's octave. Velocity is derived from
  how far things moved since the last update, so a game says nothing; `SetVelocity` and `SetListenerVelocity`
  give one instead; an origin rebase is not a movement. Each speed is capped under the speed of sound, so a
  teleport cannot divide by nothing. `snd_doppler` scales it (1; 0 turns it off) and a sound's own `doppler`
  scales it again (0 makes it immune) (tests: AnApproachingEmitterRaisesPitchAndARecedingOneLowersIt,
  AMovingListenerShiftsAStillEmitter, ASuppliedVelocityIsUsedInsteadOfTheDerivedOne,
  DopplerScaleZeroDisablesItAndAnExtremeSpeedIsClamped, ARebaseIsNotAMovement).
- **Rolloff** is `linear` (the default), `log` (`min / d`, rescaled to 1 at the min and 0 at the max) or `custom`
  (evenly spaced gains from the min to the max; an empty curve is linear) (tests: EachRolloffCurveShapesTheGain,
  AnEmptyCustomCurveFallsBackToLinear, TheRolloffIsASoundRecordFieldThatLoadsFromJson).
- **A cone** makes an emitter directional: full volume inside `coneInner` (a full angle, degrees), `coneOuterGain`
  (0.25) beyond `coneOuter`, a blend between; 360 is no cone. The direction is an `AudioSource` entity's forward
  (test: ACone_QuietensTheListenerOffAxis).
- **Limits:** a physics body's velocity is not wired in; one-shots and cues have no direction, so cones work only
  on `AudioSource` emitters; `snd_stats` shows no pitch.

### As built (audio tools, 2026-10-06 — issue #336)
- **Code:** `src/Sage.Simulation/Audio/AudioMonitor.cs` (`BusLevel`, `VoiceRow`), `AudioMixer.Preview` and
  `BusVolume`, `src/Sage.Editor/Tools/AudioPanel.cs` and the audio part of `StatOverlay`.
- **`AudioMonitor`** reads a mixer headlessly: a level per bus (the sum of its voices' effective gains, clamped,
  with the peak, the voice count and the slider) and the active voices loudest first with their distances. Master
  meters everything. No sound data is measured: a voice's gain is already volume x bus x master x distance (tests:
  BusMetersAddUpTheirVoicesGainsAndMasterMetersEverything, VoiceRowsAreLoudestFirstWithDistancesAndSkipStoppingVoices).
- **`snd_debug`** (a dev-only cvar) overlays the voices and the bus levels in the dev tools' stat overlay.
  The editor's **Audio panel** is a floating window (it has no dock slot) with a meter per bus and a combo of the
  `sound` records with a Play button.
- **A preview** (`AudioMixer.Preview`, used by the button and `snd_play`) plays a record 2D at the listener and
  ignores the cooldown and the instance limit, so the button always answers (test:
  APreviewIsTwoDimensionalAndIgnoresCooldown). The mixer remembers the last one, and when its file is replaced it
  restarts the voice or, if it has ended, plays it again, so a saved WAV is heard at once (test:
  ReplacingAPreviewedFileRestartsItsVoiceOrPlaysItAgainWhenItHasEnded). Other sounds: a reload stops a
  one-shot and restarts a loop (test: ReplacingAFileStopsItsOneShotsRestartsItsLoopsAndLeavesOthersAlone).
- **Limits:** `snd_stats` no longer prints a distance column (the overlay has it); gameplay one-shots are not
  replayed on a hot reload.

## 13. Open questions
All three questions this section held in F4 are answered:
- ~~**When does an effect's cue fire?**~~ Three fields: `appliedCues`, `tickCues`, `removedCues` (#330, above).
- ~~**Does a screen name its own sounds, or does the game bind them?**~~ A `ui_sounds` record: the game patches the
  default, a screen may name its own (#330).
- ~~**Music wants a second mixer path.**~~ Both: a `MusicPlayer` that asks the mixer for voices flagged as music,
  which are outside the cap and never stolen (#325).

## 14. Build steps
1. ~~`IAudioBackend` over MonoGame + `AudioSystem` + buses~~ **Done 2026-09-24** (TODO F4).
2. ~~`sound` records + event-driven presentation systems~~ **Done 2026-09-24.**
3. ~~`AudioSource` component + listener from the active camera~~ **Done 2026-09-24.**
4. ~~OGG streaming, music, surface footsteps, effect, attack and screen cues, occlusion and reverb, doppler and cones, audio tools~~ **Done 2026-10-06** (phase 4o, "As built" above).
