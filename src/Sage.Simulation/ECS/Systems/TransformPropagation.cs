#nullable enable
using Friflo.Engine.ECS;

namespace Sage.Simulation;

// Computes GlobalTransform.Current from Transform through the hierarchy (docs/design/03 §3.6).
// Run by World at the end of PostPhysics and Late, and at the start of each tick it copies
// Current to Previous so rendering can interpolate.
internal sealed class TransformPropagation
{
    private readonly ArchetypeQuery<Transform, GlobalTransform> _all;

    public TransformPropagation(World world)
    {
        _all = world.Query<Transform, GlobalTransform>();
    }

    public void BeginTick()
    {
        foreach (var (_, globals, _) in _all.Chunks)
        {
            var g = globals.Span;
            for (int n = 0; n < g.Length; n++)
                g[n].Previous = g[n].Current;
        }
    }

    public void Propagate()
    {
        foreach (var (transforms, globals, entities) in _all.Chunks)
        {
            var t = transforms.Span;
            var g = globals.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var entity = entities.EntityAt(n);
                if (!entity.Parent.IsNull) continue;   // children are reached from their root
                g[n].Current = Pose.FromLocal(t[n]);
                if (entity.ChildCount > 0)
                    PropagateChildren(entity, g[n].Current);
            }
        }
    }

    private static void PropagateChildren(Entity parent, in Pose parentPose)
    {
        foreach (var child in parent.ChildEntities)
        {
            if (!child.TryGetComponent<Transform>(out var local) || !child.HasComponent<GlobalTransform>())
                continue;
            ref var global = ref child.GetComponent<GlobalTransform>();
            global.Current = Pose.Combine(parentPose, Pose.FromLocal(local));
            if (child.ChildCount > 0)
                PropagateChildren(child, global.Current);
        }
    }
}
