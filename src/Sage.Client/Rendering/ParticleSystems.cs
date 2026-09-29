#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;
using Color = Microsoft.Xna.Framework.Color;
using Vector2 = Microsoft.Xna.Framework.Vector2;
using Vector4 = Microsoft.Xna.Framework.Vector4;

namespace Sage.Client;

// Making the sparks fly (docs/design/06 §3.12, TODO F39).
//
// **The simulation does not throw particles.** It says what happened — a cue fired, something was hit —
// and this reads those events with its own cursors (04 §3.1) and decides what that *looks* like, exactly
// as the audio system decides what it sounds like. A headless server raises the same events into a queue
// nobody reads and draws nothing.
//
// FrameUpdate, not a fixed phase: particles are presentation, they move at display rate, and a spark
// simulated at tick rate would judder next to the sprite that threw it.
[System("sage.client.particles", Phase.FrameUpdate)]
public sealed class ParticleSystem : ISystem
{
    private readonly Particles _particles;
    private readonly FloatingTexts _texts;
    private readonly RecordStore _records;
    private readonly ActiveCamera _camera;
    private readonly CVar<bool> _enabled;
    private readonly CVar<bool> _numbers;

    private readonly EventReader<CueTriggered> _cues;
    private readonly EventReader<Damaged> _damage;
    private readonly ArchetypeQuery<Transform, ParticleEmitter> _emitters;

    public ParticleSystem(World world, RecordStore records, CVar<bool> enabled, CVar<bool> numbers)
    {
        _particles = world.Resources.Get<Particles>();
        _texts = world.Resources.Get<FloatingTexts>();
        _records = records;
        _camera = world.Resources.Get<ActiveCamera>();
        _enabled = enabled;
        _numbers = numbers;

        // **Fixed, not Frame**: gameplay sends these from the fixed tick, and an event belongs to the
        // queue of the schedule that sent it (04 §3.1). This is the mistake F4's audio shipped with.
        _cues = world.Events.Reader<CueTriggered>(this, Schedule.Fixed);
        _damage = world.Events.Reader<Damaged>(this, Schedule.Fixed);
        _emitters = world.Query<Transform, ParticleEmitter>();

        // Everything that holds a position follows the world (R6).
        world.Origin().Rebased += offset =>
        {
            _particles.Rebase(offset);
            _texts.Rebase(offset);
        };
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        float dt = ctx.Frame.Dt;

        _particles.Enabled = _enabled.Value;
        _texts.Enabled = _numbers.Value;
        if (!_enabled.Value && _particles.Live > 0) _particles.Clear();
        if (!_numbers.Value && _texts.Count > 0) _texts.Clear();

        if (_enabled.Value || _numbers.Value)
        {
            foreach (ref readonly var cue in _cues.Read()) Cue(cue);
            foreach (ref readonly var hit in _damage.Read()) Hit(world, hit);
        }
        else
        {
            // Drained anyway: a reader that stops reading holds the queue open for everybody (04 §3.1).
            _cues.Read();
            _damage.Read();
        }

        Emitters(dt);
        _particles.Update(world, dt);
        _texts.Update(dt);
    }

    // What a cue looks like is the cue record's business, so a mod can change what a spell throws off
    // without touching the spell (16 §3.3).
    private void Cue(in CueTriggered cue)
    {
        if (!_enabled.Value) return;
        if (!_records.TryGet(cue.Cue, out CueRecord record) || record.Particles.IsEmpty) return;

        _records.TryGet(record.Particles, out ParticleRecord effect);
        int count = record.ParticleCount > 0 ? record.ParticleCount : effect?.Burst ?? 0;
        _particles.Emit(record.Particles, effect, cue.Point, Vector3.UnitY, count);
    }

    // A hit throws off what its *damage type* says — one entry for fire covers a fireball, a torch and a
    // trap — and puts up a number, which is the same information for the part of the player that reads.
    private void Hit(World world, in Damaged hit)
    {
        if (hit.Applied <= 0f) return;
        bool onThePlayer = !hit.Hit.Target.IsNull && world.IsAlive(hit.Hit.Target)
                           && hit.Hit.Target.Tags.Has<PlayerControlled>();

        if (_enabled.Value && !hit.Hit.Type.IsEmpty
            && _records.TryGet(hit.Hit.Type, out DamageTypeRecord type) && !type.Particles.IsEmpty)
        {
            _records.TryGet(type.Particles, out ParticleRecord effect);
            // Away from the blow: sparks come off the way the hit was going, turned back a little.
            var direction = hit.Hit.Direction.LengthSquared() > 0.001f ? -hit.Hit.Direction : Vector3.UnitY;
            _particles.Emit(type.Particles, effect, hit.Hit.Point, direction + Vector3.UnitY * 0.5f,
                            effect?.Burst ?? 0);
        }

        if (!_numbers.Value) return;
        uint colour = DamageNumbers.ColourFor(_records, hit.Hit.Type, onThePlayer);
        _texts.Add(FloatingTexts.Number((int)MathF.Round(hit.Applied)),
                   DamageNumbers.Above(hit.Hit.Point), colour, onThePlayer ? 1.2f : 1f);
    }

    // Things that smoke on their own: a torch, a brazier, rain over the player. The fractional part is
    // carried between frames, so a rate of 2.5 a second really is two and a half.
    private void Emitters(float dt)
    {
        if (!_enabled.Value || dt <= 0f) return;

        foreach (var (transforms, emitters, entities) in _emitters.Chunks)
        {
            var t = transforms.Span;
            var e = emitters.Span;
            for (int n = 0; n < t.Length; n++)
            {
                if (!e[n].Enabled || e[n].Effect.IsEmpty) continue;
                if (!_records.TryGet(e[n].Effect, out ParticleRecord effect) || effect.Rate <= 0f) continue;

                e[n].Pending += effect.Rate * dt;
                int count = (int)e[n].Pending;
                if (count <= 0) continue;
                e[n].Pending -= count;

                _particles.Emit(e[n].Effect, effect, t[n].LocalPosition, Vector3.UnitY, count,
                                follows: effect.Local ? entities.EntityAt(n) : default);
            }
        }
    }
}

// Particles into the snapshot (06 §3.1, §3.12).
//
// **No renderer changes.** A particle is a camera-facing quad with a tint, and the sprite path already
// draws exactly that — so this writes `SpriteInstance`s like `SpriteExtract` does, with a transparent
// material and a full-texture UV. Sharing one material and one texture per effect keeps a puff of smoke
// to one draw call.
[System("sage.client.extract.particles", Phase.Extract, After = new[] { "sage.client.extract.camera" })]
public sealed class ParticleExtract : ISystem
{
    private readonly Particles _particles;
    private readonly RenderSnapshot _snapshot;
    private readonly Renderer _renderer;

    public ParticleExtract(World world, Renderer renderer)
    {
        _particles = world.Resources.Get<Particles>();
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _renderer = renderer;
    }

    public void Run(in SystemContext ctx)
    {
        if (!_snapshot.HasView || _particles.Live == 0) return;

        var view = _snapshot.View;
        foreach (var group in _particles.Groups)
        {
            if (group.Count == 0) continue;
            int texture = _renderer.ResolveTexture(group.Record.Texture);
            int material = _renderer.Materials.Resolve(group.Record.Material.IsEmpty
                ? ParticleRecord.DefaultMaterial : group.Record.Material);
            var runtime = _renderer.Materials.Get(material);
            var pass = runtime?.Pass ?? RenderPass.Transparent;

            for (int i = 0; i < group.Count; i++)
            {
                float size = group.SizeOf(i);
                if (size <= 0.0001f) continue;

                var centre = group.Position[i] - view.CameraPosition.ToNumerics();
                uint colour = group.ColourOf(i);

                // Every field, every time: `Add()` hands back last frame's slot as it was (06 §3.1).
                ref var instance = ref _snapshot.Sprites.Add();
                instance.Center = centre;
                instance.Size = new Vector2(size, size);
                instance.Pivot = new Vector2(0.5f, 0.5f);     // particles turn about their middle
                instance.Uv = new Vector4(0f, 0f, 1f, 1f);    // one whole texture, no atlas in v1
                instance.Tint = new Vector4(((colour >> 0) & 0xFF) / 255f,
                                            ((colour >> 8) & 0xFF) / 255f,
                                            ((colour >> 16) & 0xFF) / 255f,
                                            ((colour >> 24) & 0xFF) / 255f);
                instance.Material = material;
                instance.Texture = texture;
                instance.Mode = BillboardMode.Spherical;      // a spark has no up
                instance.Roll = group.Rotation[i];            // and it may be turning (`spinDegrees`)
                instance.SortKey = RenderSortKey.Make(pass, 0, material, texture,
                                                      Vector3.Dot(centre, view.Forward.ToNumerics()), view.Far);
            }
        }
    }
}

// Numbers over the fight (docs/design/13 §3, F39).
//
// Overlay, with the HUD, because a number is text: it is drawn in pixels through `UiDraw` rather than as
// a quad in the world. What makes it feel three-dimensional is the projection — the only world-to-screen
// in the engine, written here because this is the first thing that needed one.
[System("sage.client.floating_text", Phase.Overlay, Before = new[] { "sage.client.ui" })]
public sealed class FloatingTextSystem : ISystem
{
    private static readonly Color Shadow = new(0, 0, 0, 160);

    private readonly FloatingTexts _texts;
    private readonly RenderSnapshot _snapshot;
    private readonly UiDraw _ui;

    public FloatingTextSystem(World world)
    {
        _texts = world.Resources.Get<FloatingTexts>();
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _ui = world.Resources.Get<UiDraw>();
    }

    public void Run(in SystemContext ctx)
    {
        if (_texts.Count == 0 || !_snapshot.HasView || !_ui.HasFont) return;

        var view = _snapshot.View;
        for (int i = 0; i < _texts.Count; i++)
        {
            var entry = _texts[i];
            // Camera-relative, like everything the renderer is handed (06 §3.1).
            var relative = entry.Position - view.CameraPosition.ToNumerics();
            if (!Project(relative, view, out var screen)) continue;      // behind the camera

            // It shrinks with distance like anything else, but only so far: a number you cannot read is
            // not information, and one that fills the screen is not either.
            float distance = MathF.Max(relative.Length(), 0.5f);
            float scale = Math.Clamp(14f / distance, 0.55f, 1.6f) * entry.Scale;
            uint colour = _texts.ColourOf(i);
            var tint = new Color((byte)(colour & 0xFF), (byte)((colour >> 8) & 0xFF),
                                 (byte)((colour >> 16) & 0xFF), (byte)((colour >> 24) & 0xFF));

            var size = _ui.Measure(entry.Text, scale);
            float x = screen.X - size.X * 0.5f;
            float y = screen.Y - size.Y * 0.5f;
            // A shadow, because a white number over a bright sky is nothing at all.
            _ui.Text(x + 1f, y + 1f, entry.Text, new Color(Shadow, tint.A / 255f), scale);
            _ui.Text(x, y, entry.Text, tint, scale);
        }
    }

    // World to screen: clip space, the perspective divide, then pixels. False when it is behind the
    // camera, where the divide would put it back on screen mirrored.
    private bool Project(System.Numerics.Vector3 relative, in RenderView view, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(relative.X, relative.Y, relative.Z, 1f), view.ViewProj);
        if (clip.W <= 0.001f) { screen = default; return false; }

        float x = clip.X / clip.W, y = clip.Y / clip.W;
        screen = new Vector2((x * 0.5f + 0.5f) * _ui.Size.X, (1f - (y * 0.5f + 0.5f)) * _ui.Size.Y);
        return true;
    }
}
