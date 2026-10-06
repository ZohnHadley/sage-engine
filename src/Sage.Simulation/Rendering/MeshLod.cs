#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// Mesh LOD and draw distance (issue 4n-1, docs/design/06 §3.6). Before it the only culling was the
// frustum and the fog: a pebble a kilometre off was drawn whole, as long as it was on screen.
//
//   { "type": "mesh_lod", "id": "rock", "levels": [
//       { "mesh": "models/rock_lod1.glb", "distance": 30 },
//       { "mesh": "models/rock_lod2.glb", "distance": 80 } ],
//     "cullDistance": 150 }
//
//   "mesh_renderer": { "mesh": "models/rock.glb", "material": "stone", "lod": "rock" }
//
// **A LOD group is a record a renderer names**, as its material is: a thousand rocks share one, and the
// list is checked when content loads (`Check`), not when the first rock is drawn. Level 0 is the
// renderer's own mesh; each listed level is a coarser one, drawn from its switch point on; past
// `cullDistance` (or below `cullScreenSize`) nothing is drawn. The switch is either the distance from the
// camera to the entity, or (`"metric": "ScreenSize"`) how much of the view's height the mesh covers, so a
// wide field of view or a zoomed scope moves it.
//
// **Hysteresis.** A camera standing at a switch point would flip the mesh every frame. Going coarser
// happens at the switch; coming back finer waits until the camera is `hysteresis` (a fraction, 10% by
// default) inside it. The level chosen last is remembered per renderer (`MeshRenderer.LodLevel`, not
// saved), from the screen's view.
//
// **Per-layer draw distance.** `RenderEnvironment.LayerDrawDistance[layer]` (0: none) leaves out any
// mesh on that sort layer farther than it, so small props drop out before the hills behind them.
//
// **The decision is here and the drawing is not**, like `LightRules`: the client's Extract asks `Pick`
// for each renderer and view, and counts what it leaves out (`r_stats`: lod culled, lod lowered).
public enum LodMetric
{
    Distance,     // metres from the camera to the entity
    ScreenSize,   // the share of the view's height the mesh covers (0..1); smaller is farther
}

// One coarser level of a LOD group.
public sealed class MeshLodLevel
{
    [AssetKind("mesh")]
    [Property(Tooltip = "The coarser mesh drawn from this level's switch on")]
    public AssetPath Mesh;
    [Property(Tooltip = "Its material; empty = the renderer's own")]
    public RecordRef<MaterialRecord> Material;
    [Property(Min = 0, Unit = "m", Tooltip = "Metric Distance: drawn from this far from the camera on")]
    public float Distance;
    [Property(Min = 0, Max = 1, Tooltip = "Metric ScreenSize: drawn once the mesh covers less than this share of the view's height")]
    public float ScreenSize;
}

// A LOD group (see above): the coarser meshes a renderer switches to, and where it stops being drawn.
[Record("mesh_lod", Plugin = RegistrationOwners.Core)]
public sealed class MeshLodRecord
{
    [Property(Tooltip = "What the switch points measure: Distance (metres) or ScreenSize (share of the view's height)")]
    public LodMetric Metric = LodMetric.Distance;
    [Property(Tooltip = "The coarser levels, finest first; level 0 is the renderer's own mesh")]
    public List<MeshLodLevel> Levels = new();
    [Property(Min = 0, Unit = "m", Tooltip = "Metric Distance: not drawn at all past this; 0 = always drawn")]
    public float CullDistance;
    [Property(Min = 0, Max = 1, Tooltip = "Metric ScreenSize: not drawn at all below this share of the view's height; 0 = always drawn")]
    public float CullScreenSize;
    [Property(Min = 0, Max = 0.9, Tooltip = "How far inside a switch point the camera must come back before the finer level returns (a fraction)")]
    public float Hysteresis = 0.1f;

    // Load: every level a mesh and a switch point past the one before, measured in the group's metric;
    // a cull point past them all. A mistake here is a load error at its line (issue 4n-1).
    internal static void Check(MeshLodRecord record, RecordCheck check)
    {
        bool distance = record.Metric == LodMetric.Distance;
        string field = distance ? "distance" : "screenSize";
        float previous = 0f;
        for (int i = 0; i < record.Levels.Count; i++)
        {
            var level = record.Levels[i];
            string path = $"levels[{i}]";
            if (level == null) { check.Error(path, "a level is an object: { \"mesh\": ..., \"" + field + "\": ... }"); continue; }
            if (level.Mesh.IsEmpty) check.Error(path + ".mesh", "names no mesh: each level is a coarser model");
            float at = MeshLod.Farness(record.Metric, distance ? level.Distance : level.ScreenSize);
            if (float.IsPositiveInfinity(at))
                check.Error(path + "." + field, $"needs a \"{field}\" above 0 (metric {record.Metric})");
            else if (at <= previous)
                check.Error(path + "." + field, $"switches no later than the level before it: each level's \"{field}\" is " +
                                                (distance ? "farther" : "smaller") + " than the last");
            else previous = at;
        }
        float cull = distance ? record.CullDistance : record.CullScreenSize;
        if (cull > 0f && MeshLod.Farness(record.Metric, cull) <= previous)
            check.Error(distance ? "cullDistance" : "cullScreenSize", "is not past the last level's switch point, so that level would never be drawn");
        if ((distance ? record.CullScreenSize : record.CullDistance) > 0f)
            check.Warn(distance ? "cullScreenSize" : "cullDistance", $"is not read with metric {record.Metric}");
    }
}

public static class MeshLod
{
    // What `Choose` returns when the mesh is not drawn at all, and what to pass as `previous` the first time.
    public const int Culled = -1;
    public const int Unknown = -2;

    // A switch value in one direction for both metrics: larger is farther. Distance is itself; a screen
    // size is its inverse (0: never, which is +∞).
    internal static float Farness(LodMetric metric, float value) =>
        metric == LodMetric.Distance ? (value > 0f ? value : float.PositiveInfinity)
                                     : (value > 0f ? 1f / value : float.PositiveInfinity);

    // The share of a view's height a sphere of `radius` covers at `distance`: `projectionScaleY` is the
    // projection's M22 (1 / tan(fovY / 2) for a perspective view, 2 / height for an orthographic one).
    public static float ScreenSize(float radius, float distance, float projectionScaleY, bool orthographic) =>
        orthographic ? radius * projectionScaleY : radius * projectionScaleY / MathF.Max(distance, 0.001f);

    // The level of `lod` to draw: 0 the renderer's own mesh, n the group's Levels[n - 1], or `Culled`.
    // `previous` is what it chose last time (`Unknown` the first time), for the hysteresis (see above).
    public static int Choose(MeshLodRecord lod, float distance, float screenSize, int previous = Unknown)
    {
        float f = lod.Metric == LodMetric.Distance ? distance : (screenSize > 0f ? 1f / screenSize : float.PositiveInfinity);
        int count = lod.Levels.Count;
        bool cull = (lod.Metric == LodMetric.Distance ? lod.CullDistance : lod.CullScreenSize) > 0f;
        int last = count + (cull ? 1 : 0);   // states 0..count are levels; count + 1 is culled

        int raw = 0;
        while (raw < last && f >= Threshold(lod, raw)) raw++;

        int current = previous == Culled ? count + 1 : previous;
        if (current < 0 || current > last || raw >= current) return State(raw, count);

        // Coming back finer: each switch point only once the camera is the band inside it.
        float keep = 1f - Math.Clamp(lod.Hysteresis, 0f, 0.9f);
        int state = current;
        while (state > raw && f < Threshold(lod, state - 1) * keep) state--;
        return State(state, count);
    }

    // Where state `i + 1` starts: level i + 1's switch, or (i == count) the cull point.
    private static float Threshold(MeshLodRecord lod, int i)
    {
        bool distance = lod.Metric == LodMetric.Distance;
        if (i < lod.Levels.Count)
        {
            var level = lod.Levels[i];
            return Farness(lod.Metric, distance ? level.Distance : level.ScreenSize);
        }
        return Farness(lod.Metric, distance ? lod.CullDistance : lod.CullScreenSize);
    }

    private static int State(int state, int count) => state > count ? Culled : state;

    // Whether a mesh on `layer` at `distance` is past the layer's draw distance (0: none).
    public static bool PastLayerDistance(RenderEnvironment environment, byte layer, float distance)
    {
        var limits = environment.LayerDrawDistance;
        return layer < limits.Length && limits[layer] > 0f && distance > limits[layer];
    }

    // What one renderer draws in one view (the client's Extract): the level (0 own mesh, n Levels[n - 1])
    // or `Culled`, by the layer's draw distance first and then its group. `remember` (the screen's view)
    // keeps the choice in the renderer for the next frame's hysteresis; other views start from it and
    // leave it be. What it leaves out or lowers is counted in `counts` (r_stats).
    internal static int Pick(ref MeshRenderer renderer, MeshLodRecord? lod, RenderEnvironment environment,
                             float distance, float screenSize, bool remember, ref LodCounts counts)
    {
        int level;
        if (PastLayerDistance(environment, renderer.Layer, distance)) level = Culled;
        else if (lod == null) level = 0;
        else level = Choose(lod, distance, screenSize, renderer.LodLevel - 2);   // 0 = unknown, else level + 2
        if (remember) renderer.LodLevel = (byte)(level + 2);
        if (level == Culled) counts.Culled++;
        else if (level > 0) counts.Lowered++;
        return level;
    }
}

// What LOD left out and lowered this frame, every view: one per renderer per view (r_stats).
internal struct LodCounts
{
    public int Culled;    // not drawn: past its group's cull point or its layer's draw distance
    public int Lowered;   // drawn with a coarser level than its own mesh
}
