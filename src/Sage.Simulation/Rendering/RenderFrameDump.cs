#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace Sage.Simulation;

// `r_snapshot_dump` (docs/design/06 §3.11, issue 4n-18): one frame of the renderer's snapshot as text, to
// answer "why is that not drawn" without a graphics debugger. The client fills it from its RenderSnapshot
// (views, items, sprites) and the pass registry's order; this type has no graphics types, so its format is
// what a headless test pins. One line per fact, fields `key=value`, camera-relative positions:
//
//   frame 1234
//   pass  Opaque       sage:opaque   OpaquePass
//   pass  Transparent  sage:transparent  OitPass (replaces TransparentPass, by my_game)
//   off   Sky          sage:sky      SkyPass (disabled by my_game)
//   view  0 target=screen viewport=0,0,1280,720 camera=1.00,2.00,3.00 items=2 sprites=0
//   item  view=0 mesh=meshes/rock.glb part=0 material=sage:lit key=0x... at=0.00,0.00,-5.00
//   sprite view=0 material=sage:sprite_default key=0x... at=...
//   totals items=2 sprites=0 culled=1 lights=0 debug_lines=0
internal sealed class RenderFrameDump
{
    private readonly StringBuilder _text = new();

    public int Items { get; private set; }
    public int Sprites { get; private set; }

    public RenderFrameDump(long frame) => _text.Append("frame ").Append(frame.ToString(CultureInfo.InvariantCulture)).Append('\n');

    // The passes in draw order, then the ones switched off.
    public void Passes(IEnumerable<RenderPassInfo> ordered, IEnumerable<RenderPassInfo> disabled)
    {
        foreach (var p in ordered)
        {
            _text.Append("pass  ").Append(p.Stage.ToString().PadRight(12)).Append(' ').Append(p.Id.PadRight(20)).Append(' ').Append(p.Type.Name);
            if (p.Replaces != null) _text.Append(" (replaces ").Append(p.Replaces.Name).Append(", by ").Append(p.By).Append(')');
            _text.Append('\n');
        }
        foreach (var p in disabled)
            _text.Append("off   ").Append(p.Stage.ToString().PadRight(12)).Append(' ').Append(p.Id.PadRight(20)).Append(' ').Append(p.Type.Name)
                 .Append(" (disabled by ").Append(p.By).Append(")\n");
    }

    public void View(int index, string? target, int x, int y, int w, int h, Vector3 camera, int items, int sprites) =>
        _text.Append(F($"view  {index} target={target ?? "screen"} viewport={x},{y},{w},{h} camera={V(camera)} items={items} sprites={sprites}\n"));

    public void Item(int view, string mesh, int part, string material, ulong sortKey, Vector3 at)
    {
        Items++;
        _text.Append(F($"item  view={view} mesh={mesh} part={part} material={material} key=0x{sortKey:X16} at={V(at)}\n"));
    }

    public void Sprite(int view, string material, ulong sortKey, Vector3 at)
    {
        Sprites++;
        _text.Append(F($"sprite view={view} material={material} key=0x{sortKey:X16} at={V(at)}\n"));
    }

    public void Totals(int culled, int lights, int debugLines) =>
        _text.Append(F($"totals items={Items} sprites={Sprites} culled={culled} lights={lights} debug_lines={debugLines}\n"));

    public override string ToString() => _text.ToString();

    // `file` relative to `directory` unless rooted; returns the full path written.
    public string Write(string directory, string file)
    {
        string path = Path.IsPathRooted(file) ? file : Path.Combine(directory, file);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToString());
        return path;
    }

    private static string V(Vector3 v) => F($"{v.X:F2},{v.Y:F2},{v.Z:F2}");
    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
