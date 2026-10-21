using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Autodraw.Configuration;

public static class DdConfigService
{
    private static readonly JsonSerializerSettings Settings = new() { NullValueHandling = NullValueHandling.Include, Formatting = Formatting.Indented };
    private static AutoDrawConfig _current = CreateFromLegacy();
    public static AutoDrawConfig Current => Clone(_current);
    public static event EventHandler? Changed;

    // Quantize methods the palette pipeline can actually honour.  Legacy "NeuQuant Neural-Net"
    // is normalized to the default KMeans++ path (matching historical silent behaviour); "median"
    // routes through MedianCut.
    private static readonly string[] SupportedQuantizers =
    {
        "NeuQuant Neural-Net",
        "KMeans++ (Original)",
        "KMeans (Random)",
        "KMeans++ (Classic)",
        "MiniBatchKMeans (Fast)",
        "Lab Color Space",
        "Median Cut",
        "median",
        "Div",
        "div",
    };

    private static readonly string[] SupportedLayerOrders =
    {
        "Brightness to darkness",
        "Bright to Dark",
        "Largest area first",
        "Largest Area First",
        "Darkness to brightness",
        "Dark to Bright",
    };

    private static readonly string[] SupportedQuantizeStyles =
    {
        "Pixelated (Classic)",
        "Vectorized (Smooth)",
        "Pixelated",
        "Vectorized",
    };

    private static readonly string[] SupportedDrawerModules =
    {
        "Old (No Bridge)",
        "New (A* Bridge)",
        "Old",
        "New",
    };

    public static AutoDrawConfig CreateFromLegacy()
    {
        var result = new AutoDrawConfig();
        result.Language = Config.GetEntry("userlang") ?? result.Language;
        if (long.TryParse(Config.GetEntry("DrawInterval"), out var interval)) result.Interval = interval;
        if (double.TryParse(Config.GetEntry("ClickDelay"), NumberStyles.Float, CultureInfo.InvariantCulture, out var delay)) result.ClickDelay = delay;
        result.Algorithm = Config.GetEntry("DrawingAlgorithm") ?? result.Algorithm;
        return result;
    }

    public static AutoDrawConfig Clone(AutoDrawConfig config) => JsonConvert.DeserializeObject<AutoDrawConfig>(JsonConvert.SerializeObject(config, Settings), Settings)!;
    public static bool TryLoad(string path, out AutoDrawConfig? config, out string error)
    {
        config = null; error = string.Empty;
        try { config = JsonConvert.DeserializeObject<AutoDrawConfig>(File.ReadAllText(path), Settings); if (config is null) throw new JsonSerializationException("The file contains no configuration."); return Validate(config, out error); }
        catch (Exception ex) { error = ex.Message; return false; }
    }
    public static bool TryApply(AutoDrawConfig candidate, out string error)
    {
        if (Drawing.IsDrawing) { error = "Stop drawing before applying configuration changes."; return false; }
        Normalize(candidate);
        if (!Validate(candidate, out error)) return false;
        _current = Clone(candidate); Changed?.Invoke(null, EventArgs.Empty); return true;
    }

    /// <summary>Keeps the HexedDrawer compatibility aliases and the legacy booleans in sync.</summary>
    public static void Normalize(AutoDrawConfig c)
    {
        c.Mode = string.IsNullOrWhiteSpace(c.Mode) ? "color" : c.Mode;
        c.StrayRemovalMode = string.IsNullOrWhiteSpace(c.StrayRemovalMode) ? "Disabled" : c.StrayRemovalMode;
        c.QuantizeStyle = string.IsNullOrWhiteSpace(c.QuantizeStyle) ? "Pixelated (Classic)" : c.QuantizeStyle;
        c.DrawerModule = string.IsNullOrWhiteSpace(c.DrawerModule) ? "New (A* Bridge)" : c.DrawerModule;
        c.DfsMode = string.IsNullOrWhiteSpace(c.DfsMode) ? "8-Directional" : c.DfsMode;
        c.LayerOrder = string.IsNullOrWhiteSpace(c.LayerOrder) ? "Brightness to darkness" : c.LayerOrder;
        c.ColorPickingMethod = string.IsNullOrWhiteSpace(c.ColorPickingMethod) ? "Circle" : c.ColorPickingMethod;
        c.PickerType = string.IsNullOrWhiteSpace(c.PickerType) ? "Wheel" : c.PickerType;
        c.RemoveStray = c.RemoveStray || c.StrayRemovalMode.Contains("smart", StringComparison.OrdinalIgnoreCase);
        c.StrayRemovalMode = c.RemoveStray ? "Smart Merge (V5)" : "Disabled";
        if (c.DrawDelaySeconds.HasValue)
            c.Interval = (long)Math.Clamp(c.DrawDelaySeconds.Value * 10_000_000d, 0, long.MaxValue);
        else
            c.DrawDelaySeconds = c.Interval / 10_000_000d;
        if (c.Mode.Equals("Black & White", StringComparison.OrdinalIgnoreCase) ||
            c.Mode.Equals("Black and White", StringComparison.OrdinalIgnoreCase) ||
            c.Mode.Equals("bw", StringComparison.OrdinalIgnoreCase)) c.Mode = "bw";
        else if (c.Mode.Equals("Color", StringComparison.OrdinalIgnoreCase)) c.Mode = "color";
        if (c.ColorDistanceMethod?.Equals("sRGB", StringComparison.OrdinalIgnoreCase) == true) c.ColorDistanceMethod = "RGB";
        if (c.QuantizeStyle.Equals("Pixelated", StringComparison.OrdinalIgnoreCase)) c.QuantizeStyle = "Pixelated (Classic)";
        if (c.QuantizeStyle.Equals("Vectorized", StringComparison.OrdinalIgnoreCase)) c.QuantizeStyle = "Vectorized (Smooth)";
        if (c.DrawerModule.Equals("Old", StringComparison.OrdinalIgnoreCase)) c.DrawerModule = "Old (No Bridge)";
        if (c.DrawerModule.Equals("New", StringComparison.OrdinalIgnoreCase)) c.DrawerModule = "New (A* Bridge)";
        if (c.DfsMode.Equals("4dir", StringComparison.OrdinalIgnoreCase) || c.DfsMode.Equals("4", StringComparison.OrdinalIgnoreCase)) c.DfsMode = "4-Directional";
        if (c.DfsMode.Equals("8dir", StringComparison.OrdinalIgnoreCase) || c.DfsMode.Equals("8", StringComparison.OrdinalIgnoreCase)) c.DfsMode = "8-Directional";
        if (c.LayerOrder.Equals("Bright to Dark", StringComparison.OrdinalIgnoreCase)) c.LayerOrder = "Brightness to darkness";
        if (c.LayerOrder.Equals("Largest Area First", StringComparison.OrdinalIgnoreCase)) c.LayerOrder = "Largest area first";
        if (c.LayerOrder.Equals("Dark to Bright", StringComparison.OrdinalIgnoreCase)) c.LayerOrder = "Darkness to brightness";
        if (c.ColorPickingMethod.Equals("Wheel", StringComparison.OrdinalIgnoreCase)) c.ColorPickingMethod = "Circle";
        if (c.ColorPickingMethod.Equals("Circle", StringComparison.OrdinalIgnoreCase) && c.PickerType.Equals("Box", StringComparison.OrdinalIgnoreCase)) c.PickerType = "Wheel";
        if (c.ColorPickingMethod.Equals("Box", StringComparison.OrdinalIgnoreCase)) c.PickerType = "Box";
    }
    public static void Export(string path) => File.WriteAllText(path, JsonConvert.SerializeObject(_current, Settings));
    public static bool Validate(AutoDrawConfig c, out string error)
    {
        error = string.Empty;
        if (c.MonitorIndex < 0 || c.PixelSize < 1 || c.KClusters is < 1 or > 256 || c.Interval < 0 || c.ClickDelay < 0 || c.SeedsCount < 1 || c.BilateralDiameter < 1 || c.LabSpatialIterations < 1 || c.SearchLimit < 1) return Fail("One or more numeric values are outside their valid range.", out error);
        if (c.BrushSize is < 1 or > 64) return Fail("Brush size must be between 1 and 64 pixels.", out error);
        if (c.DrawDelaySeconds is < 0) return Fail("Draw delay cannot be negative.", out error);
        if (c.StencilOpacity is < 0 or > 100 || c.DelayOpen < 0 || c.DelayInput < 0 || c.DelayClose < 0) return Fail("Opacity must be 0–100 and delays cannot be negative.", out error);
        if (c.LockShape && (c.CustomWidth < 1 || c.CustomHeight < 1)) return Fail("Locked shapes require positive custom dimensions.", out error);
        if (c.DrawRegion.Width < 1 || c.DrawRegion.Height < 1) return Fail("Draw region must have positive width and height.", out error);
        if (!new[] { "DFS (Default)", "Edge Traversal", "Concentric" }.Contains(c.Algorithm)) return Fail("Unsupported drawing algorithm.", out error);
        if (!new[] { "RGB", "HEX", "HSV", "Wheel", "Box", "Palette Match", "PaletteMatch" }.Contains(c.PickerType, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported picker type.", out error);
        if (!new[] { "Circle", "Box", "Wheel" }.Contains(c.ColorPickingMethod, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported color picker style.", out error);
        if (!new[] { "color", "bw" }.Contains(c.Mode, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported drawing mode.", out error);
        if (!new[] { "8-Directional", "4-Directional" }.Contains(c.DfsMode, StringComparer.OrdinalIgnoreCase)) return Fail("DFS search must be 8-Directional or 4-Directional.", out error);
        if (!SupportedQuantizeStyles.Contains(c.QuantizeStyle, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported quantize style.", out error);
        if (!SupportedDrawerModules.Contains(c.DrawerModule, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported drawer module.", out error);
        if (!SupportedLayerOrders.Contains(c.LayerOrder, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported layer order.", out error);
        if (!new[] { "KMeans++ (Original)", "KMeans (Random)", "MiniBatchKMeans (Fast)", "KMeans++ (Classic)" }.Contains(c.KmeansAlgorithm, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported KMeans method.", out error);
        if (c.ColorDistanceMethod is not null && !new[] { "CIELAB", "RGB", "sRGB", "OKLAB" }.Contains(c.ColorDistanceMethod, StringComparer.OrdinalIgnoreCase)) return Fail("Color space must be CIELAB, RGB/sRGB, or OKLAB.", out error);
        if (c.IsPaletteMatchPicker && c.PaletteSlots.Count == 0) return Fail("Palette Match requires at least one palette slot (colour + screen coordinate).", out error);
        if (!SupportedQuantizers.Contains(c.QuantizeMethod, StringComparer.OrdinalIgnoreCase)) return Fail($"Unsupported quantize method '{c.QuantizeMethod}'. Supported: {string.Join(", ", SupportedQuantizers)}.", out error);
        if (c.RgbCoordinates.AnyInvalidCount(0, 3) || c.HsvCoordinates.AnyInvalidCount(0, 3) || c.CapturedPalette.Concat(c.BackgroundColorsToRemove).Concat(c.PaletteSlots.Select(slot => slot.Hex)).Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x ?? "", "^#[0-9A-Fa-f]{6}$"))) return Fail("Palette colors must be #RRGGBB and picker coordinates contain an invalid count.", out error);
        return true;
    }
    private static bool AnyInvalidCount(this List<ScreenPoint> points, int min, int max) => points.Count != 0 && (points.Count < min || points.Count > max);
    private static bool Fail(string message, out string error) { error = message; return false; }
}
