#nullable enable
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Brush levels, drawn (docs/design/15 §3, TODO F16).
//
// The client half of a `.map`, and it is deliberately the smaller one: the simulation has already
// decided where every polygon is (`BrushGeometry`), so this turns polygons into buffers and nothing
// else. The same division as terrain — `TerrainMeshSystem` next door does the same job for heightfields.
//
// **One mesh per texture, not per brush.** A `MeshRenderer` holds one material and `CreateMesh` makes
// one part, so a room of two hundred brushes sharing four textures becomes four draws rather than two
// hundred. Brushes are static, so there is nothing to keep separate.
internal sealed class MapMeshSystem : ISystem
{
    private readonly World _world;
    private readonly Renderer _renderer;
    private readonly MapLevels _levels;

    // Reused across builds, like the terrain builder's: a level loads rarely, but it loads while the
    // game is running and a hitch is a hitch (02 §4.6).
    private readonly List<VertexPositionNormalTexture> _vertices = new();
    private readonly List<int> _indices = new();
    private readonly Dictionary<string, List<LevelFace>> _byTexture = new();
    private readonly List<Entity> _built = new();

    public MapMeshSystem(World world, Renderer renderer)
    {
        _world = world;
        _renderer = renderer;
        _levels = world.Resources.Get<MapLevels>();

        // A level that unloads takes its GPU buffers with it. Terrain does not do this yet and leaks a
        // sector's chunks on unload (F14); a level that a mapper reloads every few seconds would leak
        // far faster, so it is done here from the start.
        _levels.Unloaded += level =>
        {
            foreach (var entity in _world.Query<MapGeometry, MeshRenderer>().Entities.ToEntityList())
            {
                if (entity.GetComponent<MapGeometry>().Level != level.Record) continue;
                var handle = entity.GetComponent<MeshRenderer>().Handle;
                if (!handle.IsEmpty) _renderer.DestroyMesh(handle);
                _world.Destroy(entity);     // its own entities, so the buffers are freed before they go
            }
            level.MeshBuilt = false;
        };
    }

    public void Run(in SystemContext ctx)
    {
        for (int i = 0; i < _levels.Loaded.Count; i++)
        {
            var level = _levels.Loaded[i];
            if (level.MeshBuilt || !level.Placed) continue;   // placed by the simulation half first
            level.MeshBuilt = true;
            Build(level);
        }
    }

    private void Build(MapLevel level)
    {
        _byTexture.Clear();
        foreach (var brush in level.Brushes)
            foreach (var face in brush.Faces)
            {
                if (!_byTexture.TryGetValue(face.Texture, out var faces))
                    _byTexture[face.Texture] = faces = new List<LevelFace>();
                faces.Add(face);
            }

        _built.Clear();
        foreach (var (texture, faces) in _byTexture)
        {
            var handle = BuildGroup(faces, $"{level.Record} {texture}", out var bounds);
            if (handle.IsEmpty) continue;

            var material = MaterialFor(level, texture);

            var entity = _world.Create(Transform.At(level.Position), $"{level.Record} {texture}");
            _world.Add(entity, new MapGeometry { Level = level.Record });
            _world.Add(entity, new MeshRenderer { Handle = handle, Material = material });
            _built.Add(entity);
        }

        // Counted before the solids are built, because they reuse the same grouping table.
        int levelMeshes = _built.Count, levelTextures = _byTexture.Count;
        foreach (var solid in level.Solids) BuildSolid(level, solid);

        Log.Info(LogCat.Level, $"{level.Source}: {levelMeshes} mesh(es) for {levelTextures} texture(s)"
                             + (level.Solids.Count > 0 ? $", plus {level.Solids.Count} solid entit(ies)" : ""));
    }

    // A door's geometry belongs to the door. Its meshes are **children** of the spawned entity with
    // positions relative to it, so opening the door moves them: transform propagation does the work, and
    // nothing rebuilds a buffer to animate a piece of level (03 §3.6, 15 §10a).
    private void BuildSolid(MapLevel level, SolidEntity solid)
    {
        if (solid.Spawned.IsNull || !_world.IsAlive(solid.Spawned)) return;

        _byTexture.Clear();
        foreach (var brush in solid.Brushes)
            foreach (var face in brush.Faces)
            {
                if (!_byTexture.TryGetValue(face.Texture, out var faces))
                    _byTexture[face.Texture] = faces = new List<LevelFace>();
                faces.Add(face);
            }

        foreach (var (texture, faces) in _byTexture)
        {
            // Relative to the entity's own origin, which is where its hull is too.
            var handle = BuildGroup(faces, $"{level.Record} {texture}", solid.Origin, out var bounds);
            if (handle.IsEmpty) continue;

            var material = MaterialFor(level, texture);
            var child = _world.Create(Transform.At(System.Numerics.Vector3.Zero), $"{solid.Source.ClassName} {texture}");
            _world.Add(child, new MapGeometry { Level = level.Record });
            _world.Add(child, new MeshRenderer { Handle = handle, Material = material });
            _world.SetParent(child, solid.Spawned);
        }
    }

    // Texture name to material id: "wall" in a map whose record is `sandbox:hut` looks for
    // `sandbox:wall`. A texture with no material draws as the default lit material rather than the error
    // checkerboard — a mapper blocking out a room with untextured brushes is working, not making a
    // mistake, and a room of magenta says the opposite.
    private RecordId MaterialFor(MapLevel level, string texture)
    {
        var material = new RecordId(level.MaterialNamespace, Sanitise(texture));
        if (_world.Records().Exists(material)) return material;

        Log.Debug(LogCat.Level, $"{level.Source}: texture '{texture}' has no material '{material}'; drawing it plain");
        return MaterialRecord.Default;
    }

    private MeshHandle BuildGroup(List<LevelFace> faces, string name, out BoundingSphere bounds) =>
        BuildGroup(faces, name, System.Numerics.Vector3.Zero, out bounds);

    private MeshHandle BuildGroup(List<LevelFace> faces, string name, System.Numerics.Vector3 relativeTo, out BoundingSphere bounds)
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
                var p = new Vector3(face.Positions[i].X - relativeTo.X,
                                    face.Positions[i].Y - relativeTo.Y,
                                    face.Positions[i].Z - relativeTo.Z);
                _vertices.Add(new VertexPositionNormalTexture(p, normal, new Vector2(face.Uvs[i].X, face.Uvs[i].Y)));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            // The geometry is wound so a face's winding agrees with its normal, which is the convention
            // everything else reads. This renderer wants the other one — the same flip `GltfLoader`
            // does, in the same place: where the buffers are made, once, rather than in a render state
            // that every material would have to remember.
            int before = _indices.Count;
            BrushGeometry.Triangulate(face.Positions.Length, first, _indices);
            for (int i = before; i + 2 < _indices.Count; i += 3)
                (_indices[i + 1], _indices[i + 2]) = (_indices[i + 2], _indices[i + 1]);
        }

        if (_indices.Count == 0) { bounds = default; return default; }

        var centre = (min + max) * 0.5f;
        bounds = new BoundingSphere(centre, Vector3.Distance(centre, max));
        return _renderer.CreateMesh(CollectionsMarshal.AsSpan(_vertices), CollectionsMarshal.AsSpan(_indices), bounds, name);
    }

    // Texture names come from a mapper's folders: "brick/wall_01" is one material, named `brick_wall_01`,
    // because a record id has no slashes in it.
    private static string Sanitise(string texture) => texture.Replace('/', '_').Replace('\\', '_').ToLowerInvariant();
}
