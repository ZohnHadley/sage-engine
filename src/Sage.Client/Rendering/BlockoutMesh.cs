#nullable enable
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Blockout brushes, drawn (issue #61): the client half of `BrushPart`, as MapMeshSystem is of a `.map`.
// The simulation built each brush's polygons when it was placed (BlockoutBrush.Geometry); this turns them
// into one mesh per material, each a child of the brush, so the brush's transform places them.
//
// The polygons are in the brush's own frame with its scale already in them (a scaled brush tiles its
// texture rather than stretching it), so each child undoes the parent's scale: its world matrix is the
// brush's turn and position alone.
//
// A brush is rebuilt by replacing its entity (the editor re-spawns a placement it changes), so a brush is
// built once, and what it built is freed when it is gone: its children destroyed and their buffers given
// back. That is checked each frame for the brushes this has built, which are few.
[System("sage.client.blockout_mesh", Phase.FrameUpdate)]
internal sealed class BlockoutMeshSystem : ISystem
{
    private readonly World _world;
    private readonly Renderer _renderer;
    private readonly Dictionary<Entity, List<(Entity Child, MeshHandle Mesh)>> _built = new();
    private readonly List<Entity> _gone = new();
    private readonly List<VertexPositionNormalTexture> _vertices = new();
    private readonly List<int> _indices = new();
    private readonly Dictionary<string, List<LevelFace>> _byMaterial = new();

    public BlockoutMeshSystem(World world, Renderer renderer)
    {
        _world = world;
        _renderer = renderer;
    }

    public void Run(in SystemContext ctx)
    {
        // What went: a brush destroyed (or re-spawned as another entity), or no longer a brush.
        _gone.Clear();
        foreach (var (brush, _) in _built)
            if (!_world.IsAlive(brush) || !_world.Has<BlockoutBrush>(brush)) _gone.Add(brush);
        foreach (var brush in _gone) Free(brush);

        foreach (var entity in _world.Query<BlockoutBrush>().Entities.ToEntityList())
        {
            if (_built.ContainsKey(entity)) continue;
            Build(entity, entity.GetComponent<BlockoutBrush>());
        }
    }

    private void Build(Entity brush, in BlockoutBrush built)
    {
        var made = new List<(Entity, MeshHandle)>();
        _built[brush] = made;
        if (built.Geometry is not { } geometry) return;

        _byMaterial.Clear();
        foreach (var face in geometry.Faces)
        {
            if (!_byMaterial.TryGetValue(face.Texture, out var faces)) _byMaterial[face.Texture] = faces = new List<LevelFace>();
            faces.Add(face);
        }

        var scale = _world.Has<Transform>(brush) ? _world.Get<Transform>(brush).LocalScale : System.Numerics.Vector3.One;
        var undo = new System.Numerics.Vector3(Inverse(scale.X), Inverse(scale.Y), Inverse(scale.Z));
        string name = brush.Name ?? "brush";

        foreach (var (material, faces) in _byMaterial)
        {
            var handle = Mesh(faces, $"{name} {material}");
            if (handle.IsEmpty) continue;
            var transform = Transform.At(System.Numerics.Vector3.Zero);
            transform.LocalScale = undo;
            var child = _world.Create(transform, $"{name} {(material.Length > 0 ? material : "(plain)")}");
            _world.Add(child, new MeshRenderer { Handle = handle, Material = MaterialFor(material) });
            _world.SetParent(child, brush);
            made.Add((child, handle));
        }
    }

    private void Free(Entity brush)
    {
        if (!_built.Remove(brush, out var made)) return;
        foreach (var (child, mesh) in made)
        {
            if (_world.IsAlive(child)) _world.Destroy(child);
            if (!mesh.IsEmpty) _renderer.DestroyMesh(mesh);
        }
    }

    // A face's material as its id; none, or one that does not exist, draws plain (MapMeshSystem's rule).
    private RecordId MaterialFor(string material)
    {
        if (material.Length == 0) return MaterialRecord.Default;
        try
        {
            var id = RecordId.Parse(material, "sage");
            if (_world.Records().Exists(id)) return id;
        }
        catch (System.FormatException) { }
        Log.Debug(LogCat.Level, $"brush material '{material}' is not a record; drawing it plain");
        return MaterialRecord.Default;
    }

    private static float Inverse(float value) => value != 0f ? 1f / value : 1f;

    private MeshHandle Mesh(List<LevelFace> faces, string name)
    {
        _vertices.Clear();
        _indices.Clear();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var face in faces)
        {
            int first = _vertices.Count;
            var normal = new Vector3(face.Normal.X, face.Normal.Y, face.Normal.Z);
            for (int i = 0; i < face.Positions.Length; i++)
            {
                var p = new Vector3(face.Positions[i].X, face.Positions[i].Y, face.Positions[i].Z);
                _vertices.Add(new VertexPositionNormalTexture(p, normal, new Vector2(face.Uvs[i].X, face.Uvs[i].Y)));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            // Wound with the normal, as a map brush is; this renderer wants the other way (MapMeshSystem).
            int before = _indices.Count;
            BrushGeometry.Triangulate(face.Positions.Length, first, _indices);
            for (int i = before; i + 2 < _indices.Count; i += 3)
                (_indices[i + 1], _indices[i + 2]) = (_indices[i + 2], _indices[i + 1]);
        }

        if (_indices.Count == 0) return default;
        var centre = (min + max) * 0.5f;
        var bounds = new BoundingSphere(centre, Vector3.Distance(centre, max));
        return _renderer.CreateMesh(CollectionsMarshal.AsSpan(_vertices), CollectionsMarshal.AsSpan(_indices), bounds, name);
    }
}
