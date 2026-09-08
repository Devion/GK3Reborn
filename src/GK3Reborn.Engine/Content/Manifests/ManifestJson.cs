using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GK3Reborn.Content.Manifests;

/// <summary>
/// Shared JSON settings for every manifest.
/// </summary>
public static class ManifestJson
{
    /// <summary>The one serializer configuration manifests use.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        NewLine = "\n",
        IndentCharacter = ' ',
        IndentSize = 2,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new Vector3JsonConverter(), new Vector4JsonConverter() },
    };
}
