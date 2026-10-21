using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Autodraw.Configuration;

/// <summary>Portable dd.json configuration.  Property names intentionally match the public file format.</summary>
public sealed class AutoDrawConfig
{
    [JsonProperty("language")] public string Language { get; set; } = "English";
    [JsonProperty("monitor_index")] public int MonitorIndex { get; set; }
    [JsonProperty("mode")] public string Mode { get; set; } = "color";
    [JsonProperty("draw_method")] public string DrawMethod { get; set; } = "1:1 Exact (AutoDraw)";
    [JsonProperty("algorithm")] public string Algorithm { get; set; } = "DFS (Default)";
    [JsonProperty("layer_order")] public string LayerOrder { get; set; } = "Brightness to darkness";
    [JsonProperty("pixel_size")] public int PixelSize { get; set; } = 1;
    [JsonProperty("k_clusters")] public int KClusters { get; set; } = 50;
    [JsonProperty("interval")] public long Interval { get; set; } = 45000;
    [JsonProperty("click_delay")] public double ClickDelay { get; set; } = .03;
    /// <summary>HexedDrawer-compatible draw delay in seconds. When present it is the canonical
    /// import value for <see cref="Interval"/>; the native tick field remains supported.</summary>
    [JsonProperty("draw_delay")] public double? DrawDelaySeconds { get; set; }
    [JsonProperty("brush_size")] public int BrushSize { get; set; } = 1;
    [JsonProperty("auto_quantize")] public bool AutoQuantize { get; set; }
    [JsonProperty("enhance_image")] public bool EnhanceImage { get; set; }
    [JsonProperty("quantize_style")] public string QuantizeStyle { get; set; } = "Pixelated (Classic)";
    [JsonProperty("dfs_mode")] public string DfsMode { get; set; } = "8-Directional";
    [JsonProperty("stray_removal_mode")] public string StrayRemovalMode { get; set; } = "Disabled";
    [JsonProperty("drawer_module")] public string DrawerModule { get; set; } = "New (A* Bridge)";
    [JsonProperty("color_picking_method")] public string ColorPickingMethod { get; set; } = "Circle";
    [JsonProperty("quantize_method")] public string QuantizeMethod { get; set; } = "NeuQuant Neural-Net";
    [JsonProperty("kmeans_algo")] public string KmeansAlgorithm { get; set; } = "KMeans++ (Original)";
    /// <summary>Color space used for palette distance: CIELAB, RGB, or OKLAB. Null falls back to CIELAB.</summary>
    [JsonProperty("color_distance_method")] public string? ColorDistanceMethod { get; set; }
    [JsonProperty("lab_lum_weight")] public double LabLumWeight { get; set; } = 1;
    [JsonProperty("lab_chroma_weight")] public double LabChromaWeight { get; set; } = 1;
    [JsonProperty("lab_hue_weight")] public double LabHueWeight { get; set; } = 1;
    [JsonProperty("lab_contrast_thresh")] public double LabContrastThreshold { get; set; } = 10;
    [JsonProperty("lab_spatial_sigma")] public double LabSpatialSigma { get; set; } = 10;
    [JsonProperty("lab_spatial_iter")] public int LabSpatialIterations { get; set; } = 1;
    [JsonProperty("lab_edge_preservation")] public double LabEdgePreservation { get; set; } = 50;
    [JsonProperty("lab_range_sigma")] public double LabRangeSigma { get; set; } = 20;
    [JsonProperty("use_bilateral")] public bool UseBilateral { get; set; } = true;
    [JsonProperty("bi_d")] public int BilateralDiameter { get; set; } = 9;
    [JsonProperty("bi_sc")] public double BilateralColorSigma { get; set; } = 75;
    [JsonProperty("bi_ss")] public double BilateralSpaceSigma { get; set; } = 75;
    [JsonProperty("use_seeds")] public bool UseSeeds { get; set; }
    [JsonProperty("seeds_count")] public int SeedsCount { get; set; } = 8000;
    [JsonProperty("remove_stray")] public bool RemoveStray { get; set; } = true;
    [JsonProperty("picker_type")] public string PickerType { get; set; } = "RGB";
    /// <summary>True when the picker type is "Palette Match" (space-insensitive, e.g. LxPainter's "PaletteMatch").</summary>
    [JsonIgnore] public bool IsPaletteMatchPicker => string.Equals(PickerType?.Replace(" ", string.Empty), "PaletteMatch", StringComparison.OrdinalIgnoreCase);
    [JsonProperty("hex_coord"), JsonConverter(typeof(NullableScreenPointConverter))] public ScreenPoint? HexCoordinate { get; set; }
    [JsonProperty("rgb_coords")] public List<ScreenPoint> RgbCoordinates { get; set; } = new();
    [JsonProperty("hsv_coords")] public List<ScreenPoint> HsvCoordinates { get; set; } = new();
    [JsonProperty("wheel_center"), JsonConverter(typeof(NullableScreenPointConverter))] public ScreenPoint? WheelCenter { get; set; }
    [JsonProperty("wheel_edge"), JsonConverter(typeof(NullableScreenPointConverter))] public ScreenPoint? WheelEdge { get; set; }
    [JsonProperty("wheel_dark"), JsonConverter(typeof(NullableScreenPointConverter))] public ScreenPoint? WheelDark { get; set; }
    [JsonProperty("wheel_bright"), JsonConverter(typeof(NullableScreenPointConverter))] public ScreenPoint? WheelBright { get; set; }
    [JsonProperty("wheel_dir")] public string WheelDirection { get; set; } = "CCW";
    [JsonProperty("wheel_angle")] public double WheelAngle { get; set; } = 180;
    [JsonProperty("box_hue_start"), JsonConverter(typeof(NullableScreenPointConverter))] public ScreenPoint? BoxHueStart { get; set; }
    [JsonProperty("box_hue_end"), JsonConverter(typeof(NullableScreenPointConverter))] public ScreenPoint? BoxHueEnd { get; set; }
    [JsonProperty("box_tl"), JsonConverter(typeof(NullableScreenPointConverter))] public ScreenPoint? BoxTopLeft { get; set; }
    [JsonProperty("box_br"), JsonConverter(typeof(NullableScreenPointConverter))] public ScreenPoint? BoxBottomRight { get; set; }
    [JsonProperty("enable_macros")] public bool EnableMacros { get; set; }
    [JsonProperty("delay_open")] public double DelayOpen { get; set; } = .01;
    [JsonProperty("delay_input")] public double DelayInput { get; set; } = .01;
    [JsonProperty("delay_close")] public double DelayClose { get; set; } = .01;
    [JsonProperty("open_picker_coord"), JsonConverter(typeof(NullableScreenPointConverter))] public ScreenPoint? OpenPickerCoordinate { get; set; }
    [JsonProperty("close_picker_coord"), JsonConverter(typeof(NullableScreenPointConverter))] public ScreenPoint? ClosePickerCoordinate { get; set; }
    [JsonProperty("captured_palette")] public List<string> CapturedPalette { get; set; } = new();
    /// <summary>Palette Match slots: parallel structure to <see cref="CapturedPalette"/> holding the screen coordinate of each swatch.</summary>
    [JsonProperty("palette_slots")] public List<PaletteSlot> PaletteSlots { get; set; } = new();
    [JsonProperty("bg_colors_to_remove")] public List<string> BackgroundColorsToRemove { get; set; } = new();
    [JsonProperty("search_limit")] public int SearchLimit { get; set; } = 20;
    [JsonProperty("safe_search")] public string SafeSearch { get; set; } = "off";
    [JsonProperty("location_mode")] public int LocationMode { get; set; }
    [JsonProperty("lock_shape")] public bool LockShape { get; set; }
    [JsonProperty("custom_w")] public int CustomWidth { get; set; }
    [JsonProperty("custom_h")] public int CustomHeight { get; set; }
    [JsonProperty("draw_region")] public ScreenRect DrawRegion { get; set; } = new(607, 92, 706, 709);
    [JsonProperty("area_selected")] public bool AreaSelected { get; set; }
    [JsonProperty("stencil_enabled")] public bool StencilEnabled { get; set; }
    [JsonProperty("stencil_opacity")] public int StencilOpacity { get; set; } = 35;
}

[JsonConverter(typeof(ScreenPointConverter))]
public readonly record struct ScreenPoint(int X, int Y);
[JsonConverter(typeof(ScreenRectConverter))]
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left + 1;
    public int Height => Bottom - Top + 1;
}

/// <summary>One Palette Match slot: the screen coordinate of a captured swatch and the colour it produces.
/// Serialized as {"coord": [x, y], "hex": "#RRGGBB"}.</summary>
[JsonConverter(typeof(PaletteSlotConverter))]
public readonly record struct PaletteSlot(ScreenPoint Coord, string Hex);

public sealed class ScreenPointConverter : JsonConverter<ScreenPoint>
{
    public override void WriteJson(JsonWriter writer, ScreenPoint value, JsonSerializer serializer) => new JArray(value.X, value.Y).WriteTo(writer);
    public override ScreenPoint ReadJson(JsonReader reader, Type type, ScreenPoint existing, bool hasExisting, JsonSerializer serializer)
    {
        var a = JArray.Load(reader);
        if (a.Count != 2 || a[0]?.Type != JTokenType.Integer || a[1]?.Type != JTokenType.Integer) throw new JsonSerializationException("Coordinates must be [x, y].");
        return new ScreenPoint(a[0]!.Value<int>(), a[1]!.Value<int>());
    }
}

public sealed class NullableScreenPointConverter : JsonConverter<ScreenPoint?>
{
    public override void WriteJson(JsonWriter writer, ScreenPoint? value, JsonSerializer serializer)
    {
        if (value is null) writer.WriteNull();
        else new JArray(value.Value.X, value.Value.Y).WriteTo(writer);
    }

    public override ScreenPoint? ReadJson(JsonReader reader, Type type, ScreenPoint? existing, bool hasExisting, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return null;
        var a = JArray.Load(reader);
        if (a.Count != 2 || a[0]?.Type != JTokenType.Integer || a[1]?.Type != JTokenType.Integer) throw new JsonSerializationException("Coordinates must be [x, y] or null.");
        return new ScreenPoint(a[0]!.Value<int>(), a[1]!.Value<int>());
    }
}

public sealed class ScreenRectConverter : JsonConverter<ScreenRect>
{
    public override void WriteJson(JsonWriter writer, ScreenRect value, JsonSerializer serializer) => new JArray(value.Left, value.Top, value.Right, value.Bottom).WriteTo(writer);
    public override ScreenRect ReadJson(JsonReader reader, Type type, ScreenRect existing, bool hasExisting, JsonSerializer serializer)
    {
        var a = JArray.Load(reader);
        if (a.Count != 4 || a.Any(v => v?.Type != JTokenType.Integer)) throw new JsonSerializationException("Draw region must be [left, top, right, bottom].");
        return new ScreenRect(a[0]!.Value<int>(), a[1]!.Value<int>(), a[2]!.Value<int>(), a[3]!.Value<int>());
    }
}

public sealed class PaletteSlotConverter : JsonConverter<PaletteSlot>
{
    public override void WriteJson(JsonWriter writer, PaletteSlot value, JsonSerializer serializer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("coord");
        new JArray(value.Coord.X, value.Coord.Y).WriteTo(writer);
        writer.WritePropertyName("hex");
        writer.WriteValue(value.Hex);
        writer.WriteEndObject();
    }

    public override PaletteSlot ReadJson(JsonReader reader, Type type, PaletteSlot existing, bool hasExisting, JsonSerializer serializer)
    {
        var o = JObject.Load(reader);
        if (o["coord"] is not JArray a || a.Count != 2 || a[0]?.Type != JTokenType.Integer || a[1]?.Type != JTokenType.Integer)
            throw new JsonSerializationException("Palette slot coord must be [x, y].");
        return new PaletteSlot(new ScreenPoint(a[0]!.Value<int>(), a[1]!.Value<int>()), o["hex"]?.Value<string>() ?? string.Empty);
    }
}
