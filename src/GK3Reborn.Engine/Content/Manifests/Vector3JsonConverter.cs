using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GK3Reborn.Content.Manifests;

/// <summary>
/// Reads and writes <see cref="Vector3"/> as a three-element array.
/// </summary>
/// <remarks>
/// <see cref="Vector3"/> exposes X, Y and Z as fields rather than properties, so the
/// default serializer emits an empty object for it — silently, which is the worst
/// possible failure for a content document a human is expected to hand-edit.
/// <c>[0.5, 2.0, -1.25]</c> is also simply nicer to edit than three named members.
/// </remarks>
public sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    /// <inheritdoc/>
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an array of three numbers for {typeToConvert.Name}.");
        }

        Span<float> values = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException($"Expected three numbers for {typeToConvert.Name}.");
            }

            values[i] = reader.GetSingle();
        }

        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException($"Expected exactly three numbers for {typeToConvert.Name}.");
        }

        return new Vector3(values[0], values[1], values[2]);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }
}
