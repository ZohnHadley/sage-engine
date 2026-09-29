#nullable enable
using System;

namespace sage_engine;

// What a value with its own JSON converter looks like, as a JSON Schema: put on the converter (or on
// the type it reads) so that a schema can describe a colour's "#RRGGBB", which the metadata sees only
// as a uint. A converter without one gets an open schema: anything is accepted, and the load checks it.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class SchemaShapeAttribute : Attribute
{
    public SchemaShapeAttribute(string json) { Json = json; }
    public string Json { get; }
}
