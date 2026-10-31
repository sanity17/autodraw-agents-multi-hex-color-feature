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

    /// <summary>Invisible auto-save location so users never touch a config file by hand.</summary>
    public static readonly string DefaultPath = Path.Combine(Config.FolderPath, "dd.json");

    /// <summary>Loads the auto-saved configuration created by the UI, if any.</summary>
    public static void AutoLoad()
    {
        try
        {
            if (!File.Exists(DefaultPath)) return;
            if (TryLoad(DefaultPath, out var config, out var error) && config is not null)
            {
                _current = config;
                Changed?.Invoke(null, EventArgs.Empty);
            }
            else Utils.Log("Auto-saved dd.json could not be loaded: " + error);
        }
        catch (Exception ex) { Utils.Log("Auto-loading dd.json failed: " + ex.Message); }
    }

    /// <summary>Writes the current configuration to <see cref="DefaultPath"/> so every UI change
    /// survives a restart without the user saving anything.</summary>
    private static void Persist()
    {
        try { File.WriteAllText(DefaultPath, JsonConvert.SerializeObject(_current, Settings)); }
        catch (Exception ex) { Utils.Log("Auto-saving dd.json failed: " + ex.Message); }
    }

    // Quantize methods the palette pipeline can actually honour.  LxPainter's own names are kept
    // so a preset imported from it does not lose the user's choice; the facade routes each one to
    // its real algorithm (Wu/NeuQuant/median/div/Lcquant/KMeans) in ImageProcessing.
    private static readonly string[] SupportedQuantizers =
    {
        "NeuQuant Neural-Net",
        "KMeans++ (Original)",
        "KMeans (Random)",
        "KMeans++ (Classic)",
        "MiniBatchKMeans (Fast)",
        "Lab Color Space",
        "CIELAB Method",
        "OKLAB Method",
        "RGB Method",
        "Wu's Quantizer",
        "Lcquant",
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
        "Largest to smallest",
        "Smallest to largest",
        "Darkness to brightness",
        "Dark to Bright",
        "Random",
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
        try
        {
            var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            config = LxPainterConfigMapper.FromLxPainter(root);
            if (config is null) throw new JsonSerializationException("The file contains no configuration.");
            // Migrate legacy aliases before validating, otherwise a dd.json written by an older
            // build (e.g. "picker_type": "hex") fails Validate and the whole file is discarded.
            Normalize(config);
            return Validate(config, out error);
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
    public static bool TryApply(AutoDrawConfig candidate, out string error, bool allowWhilePaused = false)
    {
        if (Drawing.IsDrawing && !(allowWhilePaused && Drawing.IsPaused)) { error = "Stop drawing before applying configuration changes."; return false; }
        Normalize(candidate);
        if (!Validate(candidate, out error)) return false;
        _current = Clone(candidate); Changed?.Invoke(null, EventArgs.Empty); Persist(); return true;
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
        c.RecreateLayerOrder = string.IsNullOrWhiteSpace(c.RecreateLayerOrder) ? "coverage-desc" : c.RecreateLayerOrder.Trim().ToLowerInvariant();
        c.RecreateStrokeOrder = string.IsNullOrWhiteSpace(c.RecreateStrokeOrder) ? "serpentine" : c.RecreateStrokeOrder.Trim().ToLowerInvariant();
        c.DrawMethod = NormalizeDrawMethod(c.DrawMethod);
        c.Algorithm = NormalizeAlgorithm(c.Algorithm);
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
        if (c.QuantizeMethod.Trim().Equals("KMeans++", StringComparison.OrdinalIgnoreCase)) c.QuantizeMethod = "KMeans++ (Classic)";
        c.RgbCoordinates ??= new List<ScreenPoint>();
        c.HsvCoordinates ??= new List<ScreenPoint>();
        c.CapturedPalette ??= new List<string>();
        c.PaletteSlots ??= new List<PaletteSlot>();
        c.BackgroundColorsToRemove ??= new List<string>();
        c.CapturedPalette = c.CapturedPalette.Select(NormalizeHexColor).ToList();
        c.BackgroundColorsToRemove = c.BackgroundColorsToRemove.Select(NormalizeHexColor).ToList();
        for (var i = 0; i < c.PaletteSlots.Count; i++)
            c.PaletteSlots[i] = c.PaletteSlots[i] with { Hex = NormalizeHexColor(c.PaletteSlots[i].Hex) };
        if (c.PaletteSlots.Count > 0 && c.CapturedPalette.Count == 0)
            c.CapturedPalette = c.PaletteSlots.Select(slot => slot.Hex).ToList();
        if (c.QuantizeStyle.Equals("Pixelated", StringComparison.OrdinalIgnoreCase)) c.QuantizeStyle = "Pixelated (Classic)";
        if (c.QuantizeStyle.Equals("Vectorized", StringComparison.OrdinalIgnoreCase)) c.QuantizeStyle = "Vectorized (Smooth)";
        if (c.DrawerModule.Equals("Old", StringComparison.OrdinalIgnoreCase)) c.DrawerModule = "Old (No Bridge)";
        if (c.DrawerModule.Equals("New", StringComparison.OrdinalIgnoreCase)) c.DrawerModule = "New (A* Bridge)";
        if (c.DfsMode.Equals("4dir", StringComparison.OrdinalIgnoreCase) || c.DfsMode.Equals("4", StringComparison.OrdinalIgnoreCase)) c.DfsMode = "4-Directional";
        if (c.DfsMode.Equals("8dir", StringComparison.OrdinalIgnoreCase) || c.DfsMode.Equals("8", StringComparison.OrdinalIgnoreCase)) c.DfsMode = "8-Directional";
        if (c.LayerOrder.Equals("Bright to Dark", StringComparison.OrdinalIgnoreCase)) c.LayerOrder = "Brightness to darkness";
        if (c.LayerOrder.Equals("Largest Area First", StringComparison.OrdinalIgnoreCase)) c.LayerOrder = "Largest area first";
        if (c.LayerOrder.Equals("Dark to Bright", StringComparison.OrdinalIgnoreCase)) c.LayerOrder = "Darkness to brightness";
        c.RecreateLayerOrder = c.RecreateLayerOrder switch
        {
            "largest" or "largest-area-first" or "largest area first" or "coverage" or "coverage-desc"
                => "coverage-desc",
            "smallest" or "smallest-area-first" or "coverage-asc" => "coverage-asc",
            "smallest to largest" => "coverage-asc",
            "largest to smallest" => "coverage-desc",
            "random" => "manual",
            "bright" or "brightness" or "brightness-to-darkness" or "luminance-desc" => "luminance-desc",
            "dark" or "darkness" or "darkness-to-brightness" or "luminance-asc" => "luminance-asc",
            _ => c.RecreateLayerOrder
        };
        c.RecreateStrokeOrder = c.RecreateStrokeOrder switch
        {
            "nearest" or "nearest-neighbor" or "nearest-neighbour" or "nearest neighbour" or "nearest neighbor"
                => "nearest",
            _ => "serpentine"
        };
        if (c.ColorPickingMethod.Equals("Wheel", StringComparison.OrdinalIgnoreCase)) c.ColorPickingMethod = "Circle";

        // Preserve every LxPainter picker. Unknown legacy values still fall back to Wheel so a
        // retired or misspelled picker cannot prevent the rest of a configuration from loading.
        c.PickerType = (c.PickerType ?? string.Empty).Trim().ToLowerInvariant().Replace("_", " ") switch
        {
            "hex" or "hex field" or "hex input" => "HEX",
            "rgb" or "rgb fields" or "rgb inputs" => "RGB",
            "hsv" or "hsv fields" or "hsv inputs" => "HSV",
            "box" or "color box" or "colour box" => "Box",
            "palette match" or "palettematch" or "palette grid" => "Palette Match",
            _ => "Wheel"
        };
        c.ColorPickingMethod = c.PickerType == "Box" ? "Box" : "Circle";
        c.WheelDirection = string.Equals(c.WheelDirection, "CW", StringComparison.OrdinalIgnoreCase) ? "CW" : "CCW";
        c.PickerProfile ??= string.Empty;
    }
    public static void Export(string path) => File.WriteAllText(path, JsonConvert.SerializeObject(_current, Settings));

    /// <summary>LxPainter drawing method. "Line" was this app's old label for the exact path.</summary>
    public static string NormalizeDrawMethod(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Contains("pixel", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("scaled", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("scale", StringComparison.OrdinalIgnoreCase))
            return "Pixel Size Scaling";
        return "1:1 Exact (AutoDraw)";
    }

    /// <summary>LxPainter only offers DFS and Concentric; the removed "Edge Traversal" maps to DFS.</summary>
    public static string NormalizeAlgorithm(string? value)
        => (value ?? string.Empty).Trim().Equals("Concentric", StringComparison.OrdinalIgnoreCase)
            ? "Concentric"
            : "DFS (Default)";

    private static string NormalizeHexColor(string? value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value ?? string.Empty, "^#?([0-9A-Fa-f]{6})$");
        return match.Success ? "#" + match.Groups[1].Value.ToUpperInvariant() : value ?? string.Empty;
    }

    public static bool Validate(AutoDrawConfig c, out string error)
    {
        error = string.Empty;
        if (c.MonitorIndex < 0 || c.PixelSize < 1 || c.PixelSize > 64 || c.KClusters is < 1 or > 256 || c.Interval < 0 || c.ClickDelay < 0 || c.SeedsCount < 1 || c.BilateralDiameter < 1 || c.LabSpatialIterations < 1 || c.SearchLimit < 1) return Fail("One or more numeric values are outside their valid range.", out error);
        if (c.BrushSize is < 1 or > 64) return Fail("Brush size must be between 1 and 64 pixels.", out error);
        if (c.RecreateStrokeDelayMs is < 0 or > 60_000) return Fail("Recreate stroke delay must be between 0 and 60000 milliseconds.", out error);
        if (c.RecreateMoveStepPixels is < 1 or > 512) return Fail("Recreate move step must be between 1 and 512 pixels.", out error);
        if (!double.IsFinite(c.RecreateVerifyTolerance) || c.RecreateVerifyTolerance is < 0 or > 100)
            return Fail("Color verification tolerance must be between 0 and 100 ΔE.", out error);
        if (c.RecreateVerifyRetries is < 0 or > 10) return Fail("Color verification retries must be between 0 and 10.", out error);
        if (c.RecreateSelectionAttempts is < 1 or > 10) return Fail("Colour selection attempts must be between 1 and 10.", out error);
        if (c.RecreateStrokeAttempts is < 1 or > 10) return Fail("Stroke attempts must be between 1 and 10.", out error);
        if (c.RecreateCheckpointEvery is < 1 or > 500) return Fail("Checkpoint interval must be between 1 and 500 strokes.", out error);
        if (c.RecreateAlphaThreshold is < 0 or > 255) return Fail("Recreate alpha threshold must be between 0 and 255.", out error);
        if (!new[] { "coverage-desc", "coverage-asc", "luminance-desc", "luminance-asc", "manual",
                     "largest", "largest-area-first", "largest area first", "coverage",
                     "smallest", "smallest-area-first",
                     "bright", "brightness", "brightness-to-darkness", "luminance-desc",
                     "dark", "darkness", "darkness-to-brightness" }
                .Contains(c.RecreateLayerOrder ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            return Fail("Unsupported recreate layer order.", out error);
        if (!new[] { "serpentine", "nearest", "nearest-neighbor", "nearest-neighbour", "nearest neighbor", "nearest neighbour" }
                .Contains(c.RecreateStrokeOrder ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            return Fail("Recreate stroke order must be serpentine or nearest.", out error);
        if (!Enum.IsDefined(typeof(LayerMaskMode), c.MaskMode))
            return Fail("Unsupported recreate mode.", out error);
        if (c.DrawDelaySeconds is < 0) return Fail("Draw delay cannot be negative.", out error);
        if (c.StencilOpacity is < 0 or > 100 || c.DelayOpen < 0 || c.DelayInput < 0 || c.DelayClose < 0) return Fail("Opacity must be 0–100 and delays cannot be negative.", out error);
        if (c.LockShape && (c.CustomWidth < 1 || c.CustomHeight < 1)) return Fail("Locked shapes require positive custom dimensions.", out error);
        if (c.DrawRegion.Width < 1 || c.DrawRegion.Height < 1) return Fail("Draw region must have positive width and height.", out error);
        if (!new[] { "DFS (Default)", "Concentric" }.Contains(c.Algorithm)) return Fail("Unsupported drawing algorithm.", out error);
        if (!new[] { "1:1 Exact (AutoDraw)", "Pixel Size Scaling" }.Contains(c.DrawMethod)) return Fail("Unsupported drawing method.", out error);
        if (!new[] { "HEX", "RGB", "HSV", "Wheel", "Box", "Palette Match" }.Contains(c.PickerType, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported picker type.", out error);
        if (!new[] { "Circle", "Box", "Wheel" }.Contains(c.ColorPickingMethod, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported color picker style.", out error);
        if (!new[] { "color", "bw" }.Contains(c.Mode, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported drawing mode.", out error);
        if (!new[] { "8-Directional", "4-Directional" }.Contains(c.DfsMode, StringComparer.OrdinalIgnoreCase)) return Fail("DFS search must be 8-Directional or 4-Directional.", out error);
        if (!SupportedQuantizeStyles.Contains(c.QuantizeStyle, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported quantize style.", out error);
        if (!SupportedDrawerModules.Contains(c.DrawerModule, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported drawer module.", out error);
        if (!SupportedLayerOrders.Contains(c.LayerOrder, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported layer order.", out error);
        if (!new[] { "KMeans++ (Original)", "KMeans (Random)", "MiniBatchKMeans (Fast)", "KMeans++ (Classic)" }.Contains(c.KmeansAlgorithm, StringComparer.OrdinalIgnoreCase)) return Fail("Unsupported KMeans method.", out error);
        if (c.ColorDistanceMethod is not null && !new[] { "CIELAB", "RGB", "sRGB", "OKLAB" }.Contains(c.ColorDistanceMethod, StringComparer.OrdinalIgnoreCase)) return Fail("Color space must be CIELAB, RGB/sRGB, or OKLAB.", out error);
        if (!SupportedQuantizers.Contains(c.QuantizeMethod, StringComparer.OrdinalIgnoreCase)) return Fail($"Unsupported quantize method '{c.QuantizeMethod}'. Supported: {string.Join(", ", SupportedQuantizers)}.", out error);
        if ((c.RgbCoordinates.Count != 0 && c.RgbCoordinates.Count != 3) || (c.HsvCoordinates.Count != 0 && c.HsvCoordinates.Count != 3))
            return Fail("RGB and HSV picker calibrations must contain either zero or three coordinates.", out error);
        if (c.BackgroundTolerance is < 0 or > 255 || (c.BackgroundTolerance.HasValue && !double.IsFinite(c.BackgroundTolerance.Value)))
            return Fail("Background tolerance must be between 0 and 255.", out error);
        var colors = c.CapturedPalette.Concat(c.BackgroundColorsToRemove).Concat(c.PaletteSlots.Select(slot => slot.Hex));
        if (colors.Any(color => !System.Text.RegularExpressions.Regex.IsMatch(color ?? string.Empty, "^#[0-9A-Fa-f]{6}$")))
            return Fail("Palette and background colours must use #RRGGBB format.", out error);
        var numeric = new[] { c.ClickDelay, c.LabLumWeight, c.LabChromaWeight, c.LabHueWeight,
            c.LabContrastThreshold, c.LabSpatialSigma, c.LabEdgePreservation, c.LabRangeSigma,
            c.BilateralColorSigma, c.BilateralSpaceSigma, c.DelayOpen, c.DelayInput, c.DelayClose };
        if (numeric.Any(value => !double.IsFinite(value) || value < 0)) return Fail("Timing, LAB and filter values must be finite and non-negative.", out error);
        if (c.BilateralColorSigma <= 0 || c.BilateralSpaceSigma <= 0 || c.LabSpatialSigma <= 0 || c.LabRangeSigma <= 0)
            return Fail("Sigma values must be greater than zero.", out error);
        return true;
    }
    private static bool Fail(string message, out string error) { error = message; return false; }
}
