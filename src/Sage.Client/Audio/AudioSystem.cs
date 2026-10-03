#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Client;

// Listening to the simulation and making a noise about it (docs/design/11 §3, TODO F4).
//
// **The simulation does not play sounds.** It says what happened — a cue fired, something was hit,
// something was picked up — and this reads those events with its own cursors (04 §3.1) and decides
// what that sounds like. A headless server raises the same events into a queue nobody reads, which is
// why nothing in the simulation changed to make the game audible.
//
// It runs in **FrameUpdate**: sounds start at display rate, not tick rate, because a sound started a
// tick late is inaudible and a sound started twice is not.
[System("sage.client.audio", Phase.FrameUpdate)]
internal sealed class AudioSystem : ISystem
{
    private readonly AudioMixer _mixer;
    private readonly IAudioBackend _backend;
    private readonly RecordStore _records;
    private readonly CVar<bool> _enabled;

    private readonly EventReader<CueTriggered> _cues;
    private readonly EventReader<SoundRequested> _sounds;
    private readonly EventReader<Damaged> _damage;
    private readonly EventReader<Used> _used;
    private readonly Query<Transform, AudioSource> _sources;

    // The looping voices that belonged to a source last frame. A campfire that is destroyed — or whose
    // sector was unloaded (F14) — stops iterating, and without this its voice would hum for ever from
    // a place nothing is any more.
    private readonly HashSet<int> _sourceVoices = new();
    private readonly HashSet<int> _seen = new();

    // The mixer and the backend are *this world's* (11 §3): voices are positioned in its origin space,
    // so a second world cannot share them any more than it can share a `RenderSnapshot`.
    public AudioSystem(World world, RecordStore records, CVar<bool> enabled)
    {
        _mixer = world.Resources.Get<AudioMixer>();
        _backend = world.Resources.Get<IAudioBackend>();
        _records = records;
        _enabled = enabled;

        // **Fixed, not Frame**, although this system runs per frame: an event belongs to the queue of
        // the schedule that *sent* it (04 §3.1), and gameplay sends these from the fixed tick. Reading
        // the Frame queue finds an empty one for ever, which is exactly what the first version did --
        // the game fought in silence while every test passed. `MessageLog` reads `Said` the same way.
        _cues = world.Events.Reader<CueTriggered>(this, Schedule.Fixed);
        _sounds = world.Events.Reader<SoundRequested>(this, Schedule.Fixed);   // `play_sound` (issue #275)
        _damage = world.Events.Reader<Damaged>(this, Schedule.Fixed);
        _used = world.Events.Reader<Used>(this, Schedule.Fixed);
        _sources = world.Query<Transform, AudioSource>();

        // Sounds are positioned in origin space like everything else, so they move when it does (R6).
        world.Origin().Rebased += offset => _mixer.Rebase(offset);
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;

        // The ears are where the screen's view is (#81): the player's camera, a scripted cut, or the
        // editor's free camera — the same view ActiveCamera mirrors, asked for by name.
        if (world.TryGetMainView(out var listener)) _mixer.SetListener(listener.Position, listener.Rotation);
        _mixer.Update(ctx.Frame.RealTime);

        // Turning the sound off stops what is already playing, rather than leaving a loop running
        // silently until something else ends it.
        if (!_enabled.Value && _mixer.Playing > 0) _mixer.StopAll();

        if (_enabled.Value)
        {
            foreach (ref readonly var cue in _cues.Read()) Cue(cue);
            foreach (ref readonly var sound in _sounds.Read()) Sound(sound);
            foreach (ref readonly var hit in _damage.Read()) Damage(hit);
            foreach (ref readonly var used in _used.Read()) Used(used);
            Sources(world);
        }
        else
        {
            // Drained anyway: a reader that stops reading holds the queue open for everybody (04 §3.1),
            // and turning the sound off must not make the event bus grow.
            _cues.Read();
            _sounds.Read();
            _damage.Read();
            _used.Read();
        }

        _backend.Apply(_mixer);
    }

    // A cue is the simulation saying "something happened here, show it" (16 §3.3). What it sounds like
    // is the cue record's business, so a mod can give a spell a new noise without touching the spell.
    private void Cue(in CueTriggered cue)
    {
        if (!_records.TryGet(cue.Cue, out CueRecord record) || record.Sound.IsEmpty) return;
        Play(record.Sound, cue.Point, positional: true);
    }

    // A sound content asked for by name (`play_sound`): at a place, or everywhere.
    private void Sound(in SoundRequested sound)
    {
        _records.TryGet(sound.Sound, out SoundRecord record);
        _mixer.Play(sound.Sound, record, sound.Point, sound.Positional, volume: sound.Volume <= 0f ? 1f : sound.Volume);
    }

    // A hit makes the *damage type's* noise: one entry for fire covers a fireball, a torch and a trap,
    // and an attack record can override it for a particular weapon.
    private void Damage(in Damaged hit)
    {
        RecordId sound = default;
        if (!hit.Hit.Type.IsEmpty && _records.TryGet(hit.Hit.Type, out DamageTypeRecord type)) sound = type.Sound;
        if (sound.IsEmpty) return;
        Play(sound, hit.Hit.Point, positional: true);
    }

    // What the event carries, not what the world still has: the sword was destroyed the moment it was
    // taken, so asking for its `Pickup` here found nothing and this played silence (Items.cs, `Used`).
    private void Used(in Used used)
    {
        if (used.Item.IsEmpty) return;
        if (!_records.TryGet(used.Item, out ItemRecord item) || item.Sound.IsEmpty) return;
        Play(item.Sound, used.Point, positional: true);
    }

    // Things that hum on their own: a waterfall, a campfire. Started when the entity appears, stopped
    // when it goes, and followed while it lives.
    private void Sources(World world)
    {
        _seen.Clear();
        foreach (var (transforms, sources, entities) in _sources.Chunks)
        {
            var t = transforms.Span;
            var s = sources.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var handle = new VoiceHandle(s[n].Voice);
                if (handle.IsValid && _mixer.Find(handle) != null)
                {
                    _mixer.Move(handle, t[n].LocalPosition);
                    _seen.Add(handle.Id);
                    continue;
                }
                if (!s[n].Loop && handle.IsValid) continue;   // a one-shot source has had its turn

                _records.TryGet(s[n].Sound, out SoundRecord record);
                s[n].Voice = _mixer.Play(s[n].Sound, record, t[n].LocalPosition, positional: true,
                                         volume: s[n].Volume <= 0f ? 1f : s[n].Volume, loop: s[n].Loop).Id;
                if (s[n].Voice != 0) _seen.Add(s[n].Voice);
            }
        }

        // Whatever was humming last frame and is not here now has lost its entity: stop it.
        foreach (int voice in _sourceVoices)
            if (!_seen.Contains(voice)) _mixer.Stop(new VoiceHandle(voice));

        _sourceVoices.Clear();
        foreach (int voice in _seen) _sourceVoices.Add(voice);
    }

    private void Play(RecordId sound, Vector3 position, bool positional)
    {
        _records.TryGet(sound, out SoundRecord record);
        _mixer.Play(sound, record, position, positional);
    }
}
