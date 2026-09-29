#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using F = Friflo.Engine.ECS;

namespace Sage.Simulation;

// Friflo builds its component schema (by scanning loaded assemblies) the first time an EntityStore
// is used, and prints "Assemblies loaded: ..." straight to stdout. We trigger that once, capture the
// line and send it to the log instead, so stdout stays clean in Shipping. (Console.Out is process-wide:
// the log's stdout sink keeps its own writer, so log lines written meanwhile aren't swallowed.)
internal static class EcsSchema
{
    private static readonly object Lock = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Lock)
        {
            if (_initialized) return;
            _initialized = true;

            var original = System.Console.Out;
            var captured = new StringWriter();
            try
            {
                System.Console.SetOut(captured);
                var schema = F.EntityStore.GetEntitySchema();
                System.Console.SetOut(original);
                Log.Debug(LogCat.World, $"ECS schema: {schema.Components.Length} component types, {schema.Tags.Length} tags. {captured.ToString().Trim()}");
            }
            finally
            {
                System.Console.SetOut(original);
            }
        }
    }

    // Every component and tag type in the schema: Sage's, every loaded game's and Friflo's own. For the
    // tests that check the scan found each assembly, without naming Friflo's schema types.
    internal static IReadOnlyCollection<Type> ComponentTypes()
    {
        EnsureInitialized();
        var schema = F.EntityStore.GetEntitySchema();
        return schema.ComponentTypeByType.Keys.Concat(schema.TagTypeByType.Keys).ToHashSet();
    }
}
