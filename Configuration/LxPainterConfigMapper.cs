using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Autodraw.Configuration;

/// <summary>Schema-only LxPainter compatibility. Behaviour remains in AutoDraw's normal engine;
/// this class only ensures that no values are lost while reading a preset.</summary>
public static class LxPainterConfigMapper
{
    public static AutoDrawConfig FromLxPainter(JObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var normalized = NormalizeSchema(root);
        return normalized.ToObject<AutoDrawConfig>()
               ?? throw new JsonSerializationException("The file contains no configuration.");
    }

    /// <summary>Adds aliases that cannot be represented by a single JsonProperty attribute.</summary>
    public static JObject NormalizeSchema(JObject source)
    {
        var root = (JObject)source.DeepClone();

        if (root["bg_tolerance"] is null)
        {
            var tolerance = root["tolerance"] ?? root["remove_bg_tolerance"] ?? root["bg_remove_tolerance"];
            if (tolerance is not null) root["bg_tolerance"] = tolerance.DeepClone();
        }

        // Some LxPainter versions store swatch coordinates inside captured_palette rather than
        // in a separate palette_slots array. Preserve both representations before ColorListConverter
        // intentionally reduces captured_palette to colours for the quantizer.
        if (root["palette_slots"] is null && root["captured_palette"] is JArray palette &&
            palette.Any(token => token is JObject obj && (obj["coord"] is not null || obj["point"] is not null)))
        {
            root["palette_slots"] = new JArray(palette
                .Where(token => token is JObject obj && (obj["coord"] is not null || obj["point"] is not null))
                .Select(token => token.DeepClone()));
        }

        // LxPainter uses this short label; canonicalising it here also makes the raw dd.json
        // import path behave identically to the Config-tab import path.
        if (root["quantize_method"]?.Type == JTokenType.String &&
            string.Equals(root["quantize_method"]!.Value<string>(), "KMeans++", StringComparison.OrdinalIgnoreCase))
        {
            root["quantize_method"] = "KMeans++ (Classic)";
        }

        return root;
    }
}
