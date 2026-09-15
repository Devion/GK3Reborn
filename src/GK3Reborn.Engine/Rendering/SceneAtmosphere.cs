using System.Numerics;
using System.Text.Json;

namespace GK3Reborn.Rendering;

/// <summary>Optional scene-local lighting and fog for an authored render.</summary>
public sealed record SceneAtmosphere(Vector3? Ambient, FogVolume? Fog)
{
    /// <summary>Reads an atmosphere without changing the defaults of other scenes.</summary>
    public static SceneAtmosphere Read(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses linear ambient RGB and an optional height-fog layer.</summary>
    public static SceneAtmosphere Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Vector3? ambient = root.TryGetProperty("ambient", out JsonElement rgb) ? Colour(rgb) : null;
        FogVolume? fog = null;
        if (root.TryGetProperty("fog", out JsonElement layer))
        {
            float Number(string name, float fallback, float min, float max)
            {
                float value = layer.TryGetProperty(name, out JsonElement item) ? item.GetSingle() : fallback;
                if (!float.IsFinite(value) || value < min || value > max)
                {
                    throw new FormatException($"Atmosphere fog {name} must be between {min} and {max}.");
                }
                return value;
            }
            float steps = Number("steps", 32, 1, 128);
            if (steps != MathF.Truncate(steps))
            {
                throw new FormatException("Fog steps must be an integer.");
            }
            fog = new FogVolume(
                layer.TryGetProperty("colour", out JsonElement colour) ? Colour(colour) : new Vector3(.5f, .55f, .62f),
                Number("density", .002f, 0, 1), Number("top", 4, -100000, 100000),
                Number("falloff", 10, .001f, 10000), Number("anisotropy", .35f, -.95f, .95f),
                Number("ambient", 1, 0, 10), Number("noiseScale", 120, 0, 100000),
                Number("noiseDrift", 1, 0, 10000), Number("noiseStrength", .45f, 0, 1), (int)steps, Number("directLight", 1, 0, 1));
        }
        return new SceneAtmosphere(ambient, fog);
    }

    private static Vector3 Colour(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
        {
            throw new FormatException("Atmosphere colour must have three linear RGB components.");
        }
        float[] rgb = value.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        if (rgb.Any(v => !float.IsFinite(v) || v < 0 || v > 1))
        {
            throw new FormatException("Atmosphere colour components must be between zero and one.");
        }
        return new Vector3(rgb[0], rgb[1], rgb[2]);
    }
}
