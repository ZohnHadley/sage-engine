#nullable enable
using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage.Core;

// A colour in a record (issue #22), as a person writes one:
//
//   "#FFB0A0"            red, green, blue in hex; opaque
//   "#FFB0A080"          and alpha
//   [255, 176, 160]      the same as whole numbers 0-255; opaque
//   [255, 176, 160, 128] and alpha
//
// Stored packed in a uint the way the renderer takes it (red in the low byte, alpha in the high one, as
// MonoGame's `Color.PackedValue`), and a plain number is still read as that packed value, so older
// files keep loading. Whole numbers rather than 0-1 fractions because that is what every colour picker
// shows and what "#FFB0A0" means. Written back as "#RRGGBBAA".
//
// Put on a uint field with [JsonConverter(typeof(ColourJsonConverter))]: not every uint is a colour.
[SchemaShape("""
    {
      "description": "A colour: \"#RRGGBB\" or \"#RRGGBBAA\" in hex, or [r, g, b] / [r, g, b, a] as whole numbers 0-255.",
      "anyOf": [
        { "type": "string", "pattern": "^\\s*#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\\s*$" },
        { "type": "array", "items": { "type": "integer", "minimum": 0, "maximum": 255 }, "minItems": 3, "maxItems": 4 },
        { "type": "integer", "minimum": 0, "maximum": 4294967295 }
      ]
    }
    """)]
public sealed class ColourJsonConverter : JsonConverter<uint>
{
    private const string Expected = "a colour is \"#RRGGBB\", \"#RRGGBBAA\" or [r, g, b(, a)] with whole numbers 0-255";

    public static uint Pack(byte r, byte g, byte b, byte a = 255) => r | (uint)g << 8 | (uint)b << 16 | (uint)a << 24;

    public static string Format(uint packed) =>
        $"#{packed & 0xFF:X2}{(packed >> 8) & 0xFF:X2}{(packed >> 16) & 0xFF:X2}{packed >> 24:X2}";

    // "#RRGGBB" or "#RRGGBBAA"; the '#' is required, so a colour cannot be mistaken for anything else.
    public static bool TryParse(string text, out uint packed)
    {
        packed = 0;
        text = text.Trim();
        if (text.Length is not (7 or 9) || text[0] != '#') return false;
        if (!uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)) return false;
        if (text.Length == 7) value = value << 8 | 0xFF;
        packed = Pack((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public override uint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                string text = reader.GetString() ?? "";
                return TryParse(text, out uint packed) ? packed : throw new JsonException($"'{text}': {Expected}");
            }
            case JsonTokenType.Number:
                return reader.TryGetUInt32(out uint raw) ? raw : throw new JsonException($"{Expected} (or a packed number)");
            case JsonTokenType.StartArray:
            {
                Span<byte> parts = stackalloc byte[4];
                parts[3] = 255;
                int n = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (n == 4 || reader.TokenType != JsonTokenType.Number) throw new JsonException(Expected);
                    if (!reader.TryGetInt32(out int part) || part is < 0 or > 255)
                        throw new JsonException($"{Expected}, not {System.Text.Encoding.UTF8.GetString(reader.ValueSpan)}");
                    parts[n++] = (byte)part;
                }
                if (n < 3) throw new JsonException(Expected);
                return Pack(parts[0], parts[1], parts[2], parts[3]);
            }
            default:
                throw new JsonException(Expected);
        }
    }

    public override void Write(Utf8JsonWriter writer, uint value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value));
}
