#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Sage.Simulation;

// Animation events (docs/design/12 "As built (animation events)", issue #119): named moments in a clip
// that gameplay reacts to — the blow lands here, a foot hits the ground here, the magazine comes out
// here — so the timing of an action lives with the art instead of being guessed at in code.
//
// **Where they come from.** A skinned model's clips get theirs from an `anim_events` record keyed by
// the model, like #120's sockets (a fact about the rig's clips, shared by every prefab and graph that
// plays them; a mod adds events with a second record for the same model):
//
//   { "type": "anim_events", "id": "knight", "model": "models/knight.glb",
//     "clips": { "attack": [ { "time": 0.35, "name": "hit" } ],
//                "walk":   [ { "time": 0.1, "name": "footstep" }, { "time": 0.6, "name": "footstep" } ] } }
//
// A sprite sheet's clips carry theirs already (`events: [{ "frame": 1, "name": "hit" }]`, at frame/fps
// seconds). Code may add more with AnimationClip.AddEvent.
//
// **Where they go.** The animator (AnimatorSystem) raises each one exactly once every time its layer's
// time crosses it, a loop's wrap included, whatever the animation LOD. Each goes three ways, all without
// allocating:
//   - to the world's IAnimationEventSink, which Sage.Gameplay installs: it sends the `AnimationEvent`
//     game event, which combat (the `hit` that lands a blow), sounds and viewmodels read;
//   - to entity I/O as the animator's `OnAnimEvent` output, with the event's name as its value;
//   - to the animator's own graph: an event sets the trigger param of the same name, if the graph
//     declares one, so `{ "to": "reload_end", "on": "mag_in" }` is taken when the clip says so.

// One event of a clip, as a record writes it.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class AnimEventEntry
{
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds from the clip's start")]
    public float Time;
    [Property(Tooltip = "Its name: what AnimationEvent carries, OnAnimEvent hands on, and a same-named trigger param is set by")]
    public string Name = "";
}

// The events of one model's clips (see above). Several records may name the same model: a clip gets
// the events of every one, in id order.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[Record("anim_events", Plugin = RegistrationOwners.Core)]
public sealed class AnimEventsRecord
{
    [Property(Tooltip = "The skinned model (.glb) whose clips these are")]
    [AssetKind("mesh")] public AssetPath Model;
    [Property(Tooltip = "Events by clip name (as the model names its clips)")]
    public Dictionary<string, List<AnimEventEntry>> Clips = new(StringComparer.Ordinal);
}

// Where an animator's clip events go (see above). One per world, a resource; Sage.Gameplay installs one
// that sends the AnimationEvent game event. Called in Phase.Animation while the animator is being
// stepped: it may send events and set values, never add or remove components.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public interface IAnimationEventSink
{
    void Raise(World world, Entity entity, string name);
}

internal static class AnimEvents
{
    // Gives a loaded set's clips the events the anim_events records name for its model, replacing what
    // they gave before. Content time: when the model is read, and again after every records reload.
    public static void Apply(RecordStore records, AssetPath model, AnimationSet set)
    {
        var byClip = new Dictionary<string, List<ClipEvent>>(StringComparer.Ordinal);
        if (records.TypeNameOf(typeof(AnimEventsRecord)) != null)
        {
            foreach (var record in records.All<AnimEventsRecord>())
            {
                if (record.Model != model || record.Clips == null) continue;
                foreach (var (clip, events) in record.Clips)
                {
                    if (events == null) continue;
                    if (set.FindClip(clip) is not { } found)
                    {
                        Log.Once(LogCat.Animation, LogLevel.Warn, $"anim-events:{model}:{clip}",
                            $"anim_events for {model}: {set.Source} has no clip '{clip}'; its events are never raised");
                        continue;
                    }
                    if (!byClip.TryGetValue(clip, out var list)) byClip.Add(clip, list = new List<ClipEvent>());
                    foreach (var e in events)
                    {
                        if (e == null || string.IsNullOrEmpty(e.Name) || !float.IsFinite(e.Time)) continue;
                        if (e.Time > found.Duration)
                            Log.Once(LogCat.Animation, LogLevel.Warn, $"anim-event-late:{model}:{clip}:{e.Name}",
                                $"anim_events for {model}: '{e.Name}' at {e.Time}s is past the end of '{clip}' ({found.Duration}s); it is never raised");
                        list.Add(new ClipEvent(e.Time, e.Name));
                    }
                }
            }
        }
        var none = new List<ClipEvent>();
        foreach (var clip in set.Clips)
            clip.SetRecordEvents(byClip.TryGetValue(clip.Name, out var list) ? list : none);
    }

    internal static void Check(AnimEventsRecord record, RecordCheck check)
    {
        if (record.Model.IsEmpty) check.Error("model", "names no model: clip events are looked up by the model whose clips they are");
        if (record.Clips == null) return;
        foreach (var (clip, events) in record.Clips)
        {
            if (string.IsNullOrWhiteSpace(clip)) check.Error("clips", "a clip needs a name");
            if (events == null) continue;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is not { } e) { check.Error($"clips.{clip}[{i}]", "is empty"); continue; }
                if (string.IsNullOrWhiteSpace(e.Name)) check.Error($"clips.{clip}[{i}].name", "an event needs a name");
                if (!(e.Time >= 0f) || !float.IsFinite(e.Time)) check.Error($"clips.{clip}[{i}].time", $"{e.Time} is not a time in seconds from the clip's start");
            }
        }
    }
}

// A sprite sheet's clips as the animation graph plays them (issue #119: sprites are graph leaves too):
// a clip with no joints, lasting its frames over its fps, whose events sit at frame / fps. Made once per
// sheet clip and kept while the sheet's clip is (a records reload makes new ones).
internal static class SpriteClips
{
    private static readonly ConditionalWeakTable<SpriteAnimation, AnimationClip> Made = new();

    public static AnimationClip? Find(SpriteSheetRecord sheet, string name)
    {
        if (sheet.Animations == null || !sheet.Animations.TryGetValue(name, out var animation) || animation == null) return null;
        if (Made.TryGetValue(animation, out var clip)) return clip;
        clip = Make(name, animation);
        Made.AddOrUpdate(animation, clip);
        return clip;
    }

    // The seconds a sheet clip lasts: its frames (per direction) over its fps.
    public static float Duration(SpriteAnimation animation, out int frames, out float fps)
    {
        frames = animation.Dirs is { Count: > 0 } dirs && dirs[0] != null ? dirs[0].Count : 0;
        fps = MathF.Max(animation.Fps, 0.0001f);
        return frames / fps;
    }

    private static AnimationClip Make(string name, SpriteAnimation animation)
    {
        float duration = Duration(animation, out int frames, out float fps);
        var clip = new AnimationClip(name, 0, duration);
        if (animation.Events != null)
            foreach (var e in animation.Events)
                if (e != null && !string.IsNullOrEmpty(e.Name) && e.Frame >= 0 && e.Frame < frames)
                    clip.AddEvent(e.Frame / fps, e.Name);
        return clip;
    }
}
