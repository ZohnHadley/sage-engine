#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage.Simulation;

// An entity handle is not data (09 §3.1). A prefab has nothing to point at when it is written, and
// a handle means nothing outside the world that issued it, so reading one gives the null entity and
// writing one gives null. It needs a converter at all because `Entity` exposes a ref struct (`Tags`),
// which System.Text.Json refuses to look at — without this, any component holding one (`AIState`,
// `ActiveEffect`) cannot be read from a prefab at all. Saves give entities stable ids instead (F27).
internal sealed class EntityJsonConverter : JsonConverter<Friflo.Engine.ECS.Entity>
{
    public override Friflo.Engine.ECS.Entity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, Friflo.Engine.ECS.Entity value, JsonSerializerOptions options) =>
        writer.WriteNullValue();
}
