using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GK3Reborn.Content.Manifests;

/// <summary>
/// Reads and writes <see cref="Vector3"/> as a three-element array.
/// </summary>
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

/// <summary>
/// Reads and writes <see cref="Vector4"/> as a four-element array.
/// </summary>
public sealed class Vector4JsonConverter : JsonConverter<Vector4>
{
    /// <inheritdoc/>
    public override Vector4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an array of four numbers for {typeToConvert.Name}.");
        }

        Span<float> values = stackalloc float[4];
        for (int i = 0; i < 4; i++)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException($"Expected four numbers for {typeToConvert.Name}.");
            }

            values[i] = reader.GetSingle();
        }

        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException($"Expected exactly four numbers for {typeToConvert.Name}.");
        }

        return new Vector4(values[0], values[1], values[2], values[3]);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Vector4 value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteNumberValue(value.W);
        writer.WriteEndArray();
    }
}
