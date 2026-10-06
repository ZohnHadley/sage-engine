#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace Sage.Editor;

// The editor's audio panel (issue 4o-12): a meter per bus and a sound preview. The numbers are
// `AudioMonitor` (Sage.Simulation, tested headlessly); this only draws them. Preview plays a sound
// record 2D through the mixer, and the mixer plays it again when its file is edited, so a WAV saved
// under a folder mount is heard at once.
internal sealed class AudioPanel
{
    public const string Title = "Audio";

    private readonly Engine _engine;
    private readonly Func<World?> _world;
    private readonly AudioMonitor _monitor = new();
    private List<RecordId>? _sounds;
    private int _selected;

    public AudioPanel(Engine engine, Func<World?> world)
    {
        _engine = engine;
        _world = world;
    }

    public void Draw()
    {
        if (!ImGui.Begin(Title)) { ImGui.End(); return; }

        if (_world() is not { } world || !world.Resources.TryGet<AudioMixer>(out var mixer) || mixer == null)
        {
            ImGui.TextUnformatted("no audio mixer in this world");
            ImGui.End();
            return;
        }

        _monitor.Refresh(mixer);
        foreach (var b in _monitor.Buses)
            ImGui.ProgressBar(b.Level, new Vector2(-1, 0), $"{b.Bus}  {b.Voices} voice(s)  peak {b.Peak:F2}");

        ImGui.Separator();
        _sounds ??= _engine.Records.Ids("sound").OrderBy(i => i.ToString(), StringComparer.Ordinal).ToList();
        if (_sounds.Count == 0) ImGui.TextUnformatted("no sound records");
        else
        {
            _selected = Math.Clamp(_selected, 0, _sounds.Count - 1);
            if (ImGui.BeginCombo("sound", _sounds[_selected].ToString()))
            {
                for (int i = 0; i < _sounds.Count; i++)
                    if (ImGui.Selectable(_sounds[i].ToString(), i == _selected)) _selected = i;
                ImGui.EndCombo();
            }
            if (ImGui.Button("Play"))
            {
                _engine.Records.TryGet(_sounds[_selected], out SoundRecord record);
                mixer.Preview(_sounds[_selected], record);
            }
        }
        ImGui.End();
    }
}
