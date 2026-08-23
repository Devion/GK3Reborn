using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GK3Reborn.Content.Manifests;

/// <summary>
/// Shared JSON settings for every manifest.
/// </summary>
/// <remarks>
/// Plan/02-content-pipeline.md section 3: "Ordering and serialized JSON must be
/// deterministic." Two importer runs over identical inputs must produce
/// byte-identical manifests, so nothing here may depend on culture, hash ordering
/// or machine state.
/// </remarks>
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
        Converters = { new Vector3JsonConverter() },
    };
}
