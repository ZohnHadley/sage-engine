#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Sage.Editing;

namespace Sage.Editor;

// The editor's animation preview (issue #362): pick an anim_graph (or a model's clips), then scrub a
// clip, drive the graph's params and states, see each layer's clip with its event markers, and look at
// the skeleton and its sockets from any side. What is shown and how it runs is `AnimationPreview`
// (Sage.Editing, tested headlessly; `anim_preview*` press the same buttons); this only draws it, as a
// stick figure on ImGui's draw list, so it needs no renderer, no shaders and no world of the editor's.
internal sealed class AnimationPanel
{
    public const string Title = "Animation";

    private static readonly uint BoneColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.85f, 0.9f, 1f));
    private static readonly uint JointColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.75f, 0.3f, 1f));
    private static readonly uint SocketColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.9f, 1f, 1f));
    private static readonly uint MarkerColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.4f, 0.4f, 1f));
    private static readonly uint GroundColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0.45f, 0.4f, 1f));
    private static readonly uint BarColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.27f, 0.3f, 1f));
    private static readonly uint HeadColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));

    private readonly AnimationPreview _preview;
    private readonly Engine _engine;
    private IReadOnlyList<AnimationSubject>? _subjects;
    private int _subject;
    private float _yaw = 30f, _pitch = 10f;   // the view onto the skeleton, degrees
    private bool _labels = true;
    private bool _focus;                      // opened since the last frame: bring the window forward
    private bool _sync;                       // and show what was opened in the picker

    public AnimationPanel(Engine engine, AnimationPreview preview)
    {
        _engine = engine;
        _preview = preview;
        engine.Records.Reloaded += () => _subjects = null;
        preview.Opened += () => _focus = _sync = true;
    }

    public void Draw(float frameSeconds)
    {
        if (_focus) ImGui.SetNextWindowFocus();
        _focus = false;
        if (!ImGui.Begin(Title))
        {
            ImGui.End();
            return;
        }
        _preview.Advance(frameSeconds);

        DrawPicker();
        if (_preview.Error.Length > 0) ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), _preview.Error);
        if (!_preview.IsOpen)
        {
            ImGui.TextDisabled("pick a graph and Open (or anim_preview <graph>)");
            ImGui.End();
            return;
        }

        ImGui.Separator();
        bool playing = _preview.Playing;
        if (ImGui.Checkbox("play", ref playing)) _preview.Playing = playing;
        ImGui.SameLine();
        float speed = _preview.Speed;
        ImGui.SetNextItemWidth(90);
        if (ImGui.SliderFloat("speed", ref speed, 0f, 2f, "%.2fx")) _preview.Speed = speed;
        ImGui.SameLine();
        ImGui.Checkbox("labels", ref _labels);

        // What is shown: the running graph, or one clip scrubbed by hand (a command may switch it too).
        var mode = _preview.Mode;
        if (!_preview.Graph.IsEmpty)
        {
            if (ImGui.RadioButton("graph", mode == AnimationPreviewMode.Graph)) _preview.ShowGraph();
            ImGui.SameLine();
        }
        if (_preview.Clips.Count > 0 && ImGui.RadioButton("clips", mode == AnimationPreviewMode.Clip) && mode != AnimationPreviewMode.Clip)
            _preview.ShowClip(_preview.Clip ?? _preview.Clips[0], _preview.ClipTime);
        ImGui.NewLine();
        DrawSkeleton(MathF.Min(MathF.Max(ImGui.GetContentRegionAvail().Y * 0.5f, 160f), 320f));
        ImGui.BeginChild("anim_preview_detail");
        if (_preview.Mode == AnimationPreviewMode.Graph) DrawGraph();
        else DrawClips();
        ImGui.EndChild();

        ImGui.End();
    }

    private void DrawPicker()
    {
        _subjects ??= AnimationPreview.Subjects(_engine.Records);
        if (_subjects.Count == 0) { ImGui.TextDisabled("no anim_graph records"); return; }
        if (_sync)
        {
            _sync = false;
            for (int i = 0; i < _subjects.Count; i++)
                if (_subjects[i].Graph == _preview.Graph) _subject = i;
        }
        _subject = Math.Clamp(_subject, 0, _subjects.Count - 1);
        ImGui.SetNextItemWidth(-60);
        if (ImGui.BeginCombo("##graph", Label(_subjects[_subject])))
        {
            for (int i = 0; i < _subjects.Count; i++)
                if (ImGui.Selectable(Label(_subjects[i]), i == _subject)) _subject = i;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Open")) _preview.Open(_subjects[_subject].Graph, _subjects[_subject].Model);
    }

    private static string Label(AnimationSubject s) => s.Model.IsEmpty ? $"{s.Graph}  (no model)" : $"{s.Graph}  on {s.Model}";

    // Params, then each layer: its states as buttons (the one it is in, ticked), its clip on a timeline.
    private void DrawGraph()
    {
        ImGui.Text($"t = {_preview.Time:F2}s");
        ImGui.SameLine();
        if (ImGui.SmallButton("step")) _preview.Step();
        ImGui.SameLine();
        if (ImGui.SmallButton("restart")) _preview.Open(_preview.Graph, _preview.Model);

        foreach (var p in _preview.Params)
        {
            ImGui.PushID(p.Name);
            string from = p.From == AnimParamSource.None ? "" : $"  (from {p.From}; set here)";
            switch (p.Kind)
            {
                case AnimParamKind.Trigger:
                    if (ImGui.Button(p.Name)) _preview.Trigger(p.Name);
                    break;
                case AnimParamKind.Bool:
                    bool on = p.Value != 0f;
                    if (ImGui.Checkbox(p.Name + from, ref on)) _preview.SetParam(p.Name, on ? 1f : 0f);
                    break;
                default:
                    float value = p.Value;
                    ImGui.SetNextItemWidth(160);
                    if (ImGui.DragFloat(p.Name + from, ref value, 0.05f)) _preview.SetParam(p.Name, value);
                    break;
            }
            ImGui.PopID();
        }

        foreach (var layer in _preview.Layers)
        {
            ImGui.PushID(layer.Name);
            ImGui.SeparatorText($"{layer.Name}: {layer.State ?? "(none)"}{(layer.FadingFrom != null ? $"  <- {layer.FadingFrom}" : "")}");
            int n = 0;
            foreach (var state in layer.States)
            {
                if (n++ > 0) ImGui.SameLine();
                bool current = string.Equals(state, layer.State, StringComparison.OrdinalIgnoreCase);
                if (current) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.5f, 0.3f, 1f));
                if (ImGui.SmallButton(state)) _preview.PlayState(state, layer.Name == Animators.BaseLayer ? null : layer.Name);
                if (current) ImGui.PopStyleColor();
            }
            if (layer.Weights.Count > 0)
                ImGui.TextDisabled("blend: " + string.Join("  ", layer.Weights.Select(w => $"{w.Clip} {w.Weight:F2}")));
            if (layer.Clip != null) Timeline(layer.Clip, layer.ClipTime, layer.ClipDuration, layer.Markers, scrub: null);
            ImGui.PopID();
        }

        var events = _preview.Events;
        if (events.Count > 0)
            ImGui.TextDisabled("events: " + string.Join("  ", events.Skip(Math.Max(0, events.Count - 6)).Select(e => $"{e.Name}@{e.Time:F2}s")));
    }

    // The model's clips: one picked, scrubbed on its timeline, its events marked.
    private void DrawClips()
    {
        var clips = _preview.Clips;
        string current = _preview.Clip ?? "(pick a clip)";
        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo("clip", current))
        {
            foreach (var clip in clips)
                if (ImGui.Selectable(clip, clip == _preview.Clip)) _preview.ShowClip(clip, 0f);
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        bool loops = _preview.ClipLoops;
        if (ImGui.Checkbox("loop", ref loops)) _preview.ClipLoops = loops;
        if (_preview.Clip == null) return;
        float time = _preview.ClipTime;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##scrub", ref time, 0f, MathF.Max(_preview.ClipDuration, 0.001f), "%.3f s"))
        {
            _preview.Playing = false;
            _preview.Scrub(time);
        }
        Timeline(_preview.Clip, _preview.ClipTime, _preview.ClipDuration, _preview.ClipMarkers, t => { _preview.Playing = false; _preview.Scrub(t); });
        foreach (var m in _preview.ClipMarkers)
        {
            if (ImGui.SmallButton($"{m.Name} @ {m.Time:F2}s")) { _preview.Playing = false; _preview.Scrub(m.Time); }
            ImGui.SameLine();
        }
        ImGui.NewLine();
    }

    // A clip's timeline: a bar, the event markers on it, and the playhead. Click to scrub, when allowed.
    private static void Timeline(string clip, float time, float duration, IReadOnlyList<PreviewMarker> markers, Action<float>? scrub)
    {
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        float width = MathF.Max(ImGui.GetContentRegionAvail().X, 40f);
        const float height = 18f;
        ImGui.InvisibleButton($"timeline_{clip}", new Vector2(width, height));
        if (scrub != null && ImGui.IsItemActive() && duration > 0f)
            scrub(Math.Clamp((ImGui.GetIO().MousePos.X - origin.X) / width, 0f, 1f) * duration);
        draw.AddRectFilled(origin, origin + new Vector2(width, height), BarColor, 3f);
        foreach (var m in markers)
        {
            float x = origin.X + m.Fraction * width;
            draw.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + height), MarkerColor, 2f);
            draw.AddText(new Vector2(x + 2, origin.Y + 2), MarkerColor, m.Name);
        }
        if (duration > 0f)
        {
            float x = origin.X + Math.Clamp(time / duration, 0f, 1f) * width;
            draw.AddLine(new Vector2(x, origin.Y - 2), new Vector2(x, origin.Y + height + 2), HeadColor, 2f);
        }
        ImGui.TextDisabled($"{clip}  {time:F2} / {duration:F2}s");
    }

    // The pose as a stick figure, turned by dragging: bones, joints, and the sockets (named).
    private void DrawSkeleton(float height)
    {
        var joints = _preview.Joints;
        var sockets = _preview.Sockets;
        var size = new Vector2(MathF.Max(ImGui.GetContentRegionAvail().X, 120f), height);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("skeleton_view", size);
        if (ImGui.IsItemActive())
        {
            var drag = ImGui.GetIO().MouseDelta;
            _yaw += drag.X * 0.5f;
            _pitch = Math.Clamp(_pitch + drag.Y * 0.5f, -80f, 80f);
        }
        var draw = ImGui.GetWindowDrawList();
        draw.AddRect(origin, origin + size, BarColor);
        if (joints.Count == 0)
        {
            draw.AddText(origin + new Vector2(8, 8), GroundColor, "no skeleton to show");
            return;
        }

        var view = Matrix4x4.CreateRotationY(_yaw * MathF.PI / 180f) * Matrix4x4.CreateRotationX(_pitch * MathF.PI / 180f);
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        var projected = new Vector2[joints.Count];
        for (int i = 0; i < joints.Count; i++)
        {
            var p = Vector3.Transform(joints[i].Position, view);
            projected[i] = new Vector2(p.X, -p.Y);
            min = Vector2.Min(min, projected[i]);
            max = Vector2.Max(max, projected[i]);
        }
        var extent = Vector2.Max(max - min, new Vector2(0.1f));
        float scale = MathF.Min((size.X - 40f) / extent.X, (size.Y - 40f) / extent.Y);
        var center = origin + size / 2f;
        var mid = (min + max) / 2f;
        Vector2 Screen(Vector2 p) => center + (p - mid) * scale;
        Vector2 ToScreen(Vector3 world) { var p = Vector3.Transform(world, view); return Screen(new Vector2(p.X, -p.Y)); }

        // The ground under the root, as a line across the view.
        var ground = ToScreen(new Vector3(-0.5f, 0, 0));
        var ground2 = ToScreen(new Vector3(0.5f, 0, 0));
        draw.AddLine(ground, ground2, GroundColor, 1f);

        for (int i = 0; i < joints.Count; i++)
        {
            var at = Screen(projected[i]);
            if (joints[i].Parent >= 0) draw.AddLine(Screen(projected[joints[i].Parent]), at, BoneColor, 2f);
            draw.AddCircleFilled(at, 3f, JointColor);
        }
        foreach (var s in sockets)
        {
            var at = ToScreen(s.Position);
            draw.AddQuadFilled(at + new Vector2(0, -5), at + new Vector2(5, 0), at + new Vector2(0, 5), at + new Vector2(-5, 0), SocketColor);
            if (_labels) draw.AddText(at + new Vector2(7, -7), SocketColor, s.Name);
        }
        draw.AddText(origin + new Vector2(6, size.Y - 18), GroundColor, $"{joints.Count} joints, {sockets.Count} socket(s): drag to turn");
    }
}
