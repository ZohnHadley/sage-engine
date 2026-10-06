#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// The morph pass's GPU half (issue #363): per skinned renderer whose mesh has morph targets, a dynamic
// vertex buffer per morphed part, filled on the CPU by the headless MeshMorphing.Apply from the entity's
// pose weights before the GPU skins it. Re-uploaded only when the weights changed; a renderer whose
// weights are all at rest draws the mesh's own (static) buffers and costs nothing. Allocation free per
// frame once an entity's buffers exist; an entity not drawn for a while gives its buffers back (Sweep).
internal sealed class MorphedMeshes : IDisposable
{
    private const int KeepFrames = 120;

    private sealed class Entry
    {
        public int Entity, MeshId;
        public MeshData Mesh = null!;
        public DynamicVertexBuffer?[] Buffers = Array.Empty<DynamicVertexBuffer?>();
        public SkinnedMeshVertex[]?[] Scratch = Array.Empty<SkinnedMeshVertex[]?>();
        public float[] Weights = Array.Empty<float>();
        public float[] Last = Array.Empty<float>();
        public int[] Map = Array.Empty<int>();
        public Skeleton? MapFor;
        public bool Uploaded;
        public long Seen;
    }

    private readonly GraphicsDevice _device;
    private readonly Dictionary<long, int> _byKey = new();
    private readonly List<Entry?> _entries = new();
    private readonly Stack<int> _free = new();
    private long _frame;

    public MorphedMeshes(GraphicsDevice device) => _device = device;

    public int Count => _byKey.Count;

    // The handle (RenderItem.Morph) to draw `mesh` with for this entity in `pose`, 0 for its own buffers
    // (no morph targets, or all at rest). Uploads the morphed vertices when the weights changed.
    public int Update(int entity, int meshId, MeshData mesh, SkeletonPose pose)
    {
        if (mesh.Morphs is not { } geometry || pose.MorphCount == 0) return 0;
        int targets = geometry.MorphTargets.Length;
        long key = ((long)entity << 32) | (uint)meshId;
        Entry entry;
        if (_byKey.TryGetValue(key, out int slot)) entry = _entries[slot]!;
        else
        {
            entry = new Entry { Entity = entity, MeshId = meshId };
            if (_free.Count > 0) _entries[slot = _free.Pop()] = entry;
            else { slot = _entries.Count; _entries.Add(entry); }
            _byKey.Add(key, slot);
        }
        entry.Seen = _frame;
        if (!ReferenceEquals(entry.Mesh, mesh)) Reset(entry, mesh, targets);   // new, or the mesh was reloaded
        if (!ReferenceEquals(entry.MapFor, pose.Skeleton))
        {
            MeshMorphing.Map(geometry.MorphTargets, pose.Skeleton, entry.Map);
            entry.MapFor = pose.Skeleton;
            entry.Uploaded = false;
        }
        MeshMorphing.Weights(pose.MorphWeights, entry.Map, geometry.RestMorphWeights, entry.Weights);
        if (!MeshMorphing.Moved(entry.Weights, geometry.RestMorphWeights)) return 0;
        if (!entry.Uploaded || !entry.Weights.AsSpan().SequenceEqual(entry.Last))
        {
            for (int p = 0; p < entry.Buffers.Length; p++)
            {
                if (entry.Buffers[p] is not { } buffer) continue;
                var scratch = entry.Scratch[p]!;
                MeshMorphing.Apply(geometry.Parts[p], entry.Weights, geometry.RestMorphWeights, scratch);
                buffer.SetData(scratch, 0, scratch.Length, SetDataOptions.Discard);
            }
            entry.Weights.CopyTo(entry.Last, 0);
            entry.Uploaded = true;
        }
        return slot + 1;
    }

    // The buffer to draw `part` of the item with: the morphed one, or the part's own.
    public VertexBuffer BufferOf(int handle, int part, VertexBuffer own)
    {
        if (handle <= 0 || handle > _entries.Count || _entries[handle - 1] is not { } entry) return own;
        return part < entry.Buffers.Length && entry.Buffers[part] is { } morphed ? morphed : own;
    }

    // Once a frame, after extract: buffers of entities not drawn for KeepFrames frames go back.
    public void Sweep()
    {
        _frame++;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i] is not { } entry || _frame - entry.Seen < KeepFrames) continue;
            Release(entry);
            _byKey.Remove(((long)entry.Entity << 32) | (uint)entry.MeshId);
            _entries[i] = null;
            _free.Push(i);
        }
    }

    private void Reset(Entry entry, MeshData mesh, int targets)
    {
        Release(entry);
        var geometry = mesh.Morphs!;
        entry.Mesh = mesh;
        entry.Buffers = new DynamicVertexBuffer?[mesh.Parts.Length];
        entry.Scratch = new SkinnedMeshVertex[]?[mesh.Parts.Length];
        for (int p = 0; p < mesh.Parts.Length && p < geometry.Parts.Count; p++)
        {
            var part = geometry.Parts[p];
            if (part.Morphs == null || part.Skinned == null) continue;
            entry.Buffers[p] = new DynamicVertexBuffer(_device, VertexSkinned.VertexDeclaration, part.Skinned.Length, BufferUsage.WriteOnly);
            entry.Scratch[p] = new SkinnedMeshVertex[part.Skinned.Length];
        }
        entry.Weights = new float[targets];
        entry.Last = new float[targets];
        entry.Map = new int[targets];
        entry.MapFor = null;
        entry.Uploaded = false;
    }

    private static void Release(Entry entry)
    {
        foreach (var buffer in entry.Buffers) buffer?.Dispose();
        entry.Buffers = Array.Empty<DynamicVertexBuffer?>();
    }

    public void Dispose()
    {
        foreach (var entry in _entries) if (entry != null) Release(entry);
        _entries.Clear();
        _byKey.Clear();
        _free.Clear();
    }
}
