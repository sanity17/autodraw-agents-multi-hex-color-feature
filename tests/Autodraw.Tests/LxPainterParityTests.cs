using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodraw.Configuration;
using Autodraw.Painting.Profiles;
using Autodraw.Painting.Recreate;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;

namespace Autodraw.Tests;

public class LxPainterConfigTests
{
    private const string DrawDonatePreset = """
    {
      "mode":"color", "algorithm":"DFS (Default)", "layer_order":"Brightness to darkness",
      "pixel_size":1, "k_clusters":35, "interval":45000, "click_delay":0.03,
      "quantize_method":"KMeans++", "kmeans_algo":"KMeans++ (Original)",
      "lab_lum_weight":1.0, "lab_chroma_weight":1.0, "lab_hue_weight":1.0,
      "lab_contrast_thresh":10.0, "lab_spatial_sigma":10.0, "lab_spatial_iter":1,
      "lab_edge_preservation":50.0, "lab_range_sigma":20.0,
      "use_bilateral":true, "bi_d":9, "bi_sc":75.0, "bi_ss":75.0,
      "use_seeds":false, "seeds_count":8000, "remove_stray":false,
      "picker_type":"Hex", "hex_coord":[465,625], "rgb_coords":[], "hsv_coords":[],
      "wheel_center":[137,639], "wheel_edge":[44,637], "wheel_dark":[285,731], "wheel_bright":[286,547],
      "wheel_dir":"CCW", "box_hue_start":null, "box_hue_end":null, "box_tl":null, "box_br":null,
      "search_limit":20, "safe_search":"off"
    }
    """;

    [Fact]
    public void SuppliedDrawDonatePreset_LoadsNativelyWithoutWheelMigration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lx-painter-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, DrawDonatePreset);
        try
        {
            Assert.False(HexedDrawConfigMapper.LooksLikeHexedDraw(JObject.Parse(DrawDonatePreset)));
            Assert.True(DdConfigService.TryLoad(path, out var config, out var error), error);
            Assert.NotNull(config);
            Assert.Equal("HEX", config!.PickerType);
            Assert.Equal(new ScreenPoint(465, 625), config.HexCoordinate);
            Assert.Equal(35, config.KClusters);
            Assert.Equal(45000, config.Interval);
            Assert.Equal(.03, config.ClickDelay, 8);
            Assert.Equal("KMeans++ (Classic)", config.QuantizeMethod);
            Assert.Equal("KMeans++ (Original)", config.KmeansAlgorithm);
            Assert.Equal(10, config.LabSpatialSigma);
            Assert.Equal(50, config.LabEdgePreservation);
            Assert.Equal(20, config.LabRangeSigma);
            Assert.True(config.UseBilateral);
            Assert.Equal(75, config.BilateralColorSigma);
            Assert.False(config.UseSeeds);
            Assert.False(config.RemoveStray);
            Assert.Equal(new ScreenPoint(137, 639), config.WheelCenter);
            Assert.Equal(new ScreenPoint(44, 637), config.WheelEdge);
            Assert.Equal("off", config.SafeSearch);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SchemaDetection_DoesNotTreatNativeSharedKeysAsHexedDrawer()
    {
        var root = JObject.Parse("""{"k_clusters":35,"draw_delay":0.02,"drawer_module":"New"}""");
        Assert.False(HexedDrawConfigMapper.LooksLikeHexedDraw(root));
        root["circle_params_calib"] = new JArray(1, 2, 3, 4);
        Assert.True(HexedDrawConfigMapper.LooksLikeHexedDraw(root));
    }

    [Fact]
    public void PaletteCaptureObjects_PreserveBothColoursAndCoordinates()
    {
        var root = JObject.Parse("""
        {
          "picker_type":"palette_match",
          "captured_palette":[
            {"coord":[10,20],"rgb":[255,0,0]},
            {"coord":[30,40],"color":"#00ff00"},
            [0,0,255]
          ]
        }
        """);
        var config = LxPainterConfigMapper.FromLxPainter(root);
        DdConfigService.Normalize(config);

        Assert.Equal("Palette Match", config.PickerType);
        Assert.Equal(new[] { "#FF0000", "#00FF00", "#0000FF" }, config.CapturedPalette);
        Assert.Equal(2, config.PaletteSlots.Count);
        Assert.Equal(new ScreenPoint(10, 20), config.PaletteSlots[0].Coord);
        Assert.Equal("#00FF00", config.PaletteSlots[1].Hex);
    }

    [Fact]
    public void FieldCoordinatesPaletteAndBackgroundTolerance_RoundTrip()
    {
        var original = new AutoDrawConfig
        {
            PickerType = "HSV",
            HexCoordinate = new ScreenPoint(1, 2),
            RgbCoordinates = { new ScreenPoint(3, 4), new ScreenPoint(5, 6), new ScreenPoint(7, 8) },
            HsvCoordinates = { new ScreenPoint(9, 10), new ScreenPoint(11, 12), new ScreenPoint(13, 14) },
            PaletteSlots = { new PaletteSlot(new ScreenPoint(15, 16), "#AABBCC") },
            BackgroundTolerance = 12.5
        };
        original.CapturedPalette.Add("#AABBCC");

        var loaded = JsonConvert.DeserializeObject<AutoDrawConfig>(JsonConvert.SerializeObject(original))!;
        Assert.Equal(original.HexCoordinate, loaded.HexCoordinate);
        Assert.Equal(original.RgbCoordinates, loaded.RgbCoordinates);
        Assert.Equal(original.HsvCoordinates, loaded.HsvCoordinates);
        Assert.Equal(original.PaletteSlots, loaded.PaletteSlots);
        Assert.Equal(12.5, loaded.BackgroundTolerance);
    }

    [Fact]
    public void Validation_RejectsIncompleteRgbOrHsvCalibration()
    {
        var config = new AutoDrawConfig { PickerType = "RGB" };
        config.RgbCoordinates.Add(new ScreenPoint(1, 2));
        Assert.False(DdConfigService.Validate(config, out var error));
        Assert.Contains("three", error, StringComparison.OrdinalIgnoreCase);
    }
}

public class LxPainterPickerTests
{
    private static RecreateOptions Options() => RecreateTestData.Options(new ScreenRect(0, 0, 10, 10));

    [Fact]
    public async Task HexPicker_ClicksFieldAndTypesCanonicalHex()
    {
        var input = new RecordingInputSimulator();
        var picker = new HexColorPicker(input, Options(), new ScreenPoint(10, 20));
        await picker.SelectColorAsync(new SKColor(0x12, 0xAB, 0xEF), CancellationToken.None);

        Assert.Equal(new[] { new ScreenPoint(10, 20) }, input.Clicks);
        Assert.Equal(new[] { "#12ABEF" }, input.ReplacedTexts);
        Assert.Equal(0, input.EnterCalls);
    }

    [Fact]
    public async Task RgbPicker_ReplacesThreeFieldsInOrder()
    {
        var input = new RecordingInputSimulator();
        var points = new[] { new ScreenPoint(1, 2), new ScreenPoint(3, 4), new ScreenPoint(5, 6) };
        await new RgbColorPicker(input, Options(), points).SelectColorAsync(new SKColor(1, 2, 255), CancellationToken.None);

        Assert.Equal(points, input.Clicks);
        Assert.Equal(new[] { "1", "2", "255" }, input.ReplacedTexts);
    }

    [Fact]
    public async Task HsvPicker_UsesDegreesAndPercentages()
    {
        var input = new RecordingInputSimulator();
        var points = new[] { new ScreenPoint(1, 2), new ScreenPoint(3, 4), new ScreenPoint(5, 6) };
        await new HsvColorPicker(input, Options(), points).SelectColorAsync(SKColors.Red, CancellationToken.None);
        Assert.Equal(new[] { "0", "100", "100" }, input.ReplacedTexts);
    }

    [Fact]
    public async Task PaletteMatch_ClicksNearestCapturedSwatch()
    {
        var input = new RecordingInputSimulator();
        var slots = new[]
        {
            new PaletteSlot(new ScreenPoint(10, 10), "#FF0000"),
            new PaletteSlot(new ScreenPoint(20, 20), "#00FF00")
        };
        await new PaletteMatchColorPicker(input, Options(), slots).SelectColorAsync(new SKColor(250, 10, 10), CancellationToken.None);
        Assert.Equal(new[] { new ScreenPoint(10, 10) }, input.Clicks);
    }

    [Fact]
    public async Task MacroPicker_WrapsEveryPickerWithOpenAndClose()
    {
        var input = new RecordingInputSimulator();
        var inner = new RecordingColorPicker();
        var options = Options();
        options.MacrosEnabled = true;
        options.OpenPickerPoint = new ScreenPoint(1, 2);
        options.ClosePickerPoint = new ScreenPoint(3, 4);
        options.OpenPickerDelayMs = 0;
        options.ClosePickerDelayMs = 0;
        await new MacroColorPicker(inner, input, options).SelectColorAsync(SKColors.Red, CancellationToken.None);

        Assert.Equal(new[] { new ScreenPoint(1, 2), new ScreenPoint(3, 4) }, input.Clicks);
        Assert.Equal(new[] { SKColors.Red }, inner.Selected);
    }

    [Fact]
    public void CalibrationAndProfiles_SnapshotPaletteSlots()
    {
        var config = new AutoDrawConfig { PickerType = "Palette Match" };
        config.PaletteSlots.Add(new PaletteSlot(new ScreenPoint(10, 20), "#FF0000"));
        config.CapturedPalette.Add("#FF0000");
        var calibration = RecreateOptions.BuildCalibration(config, config.PickerType);
        var profile = GameProfile.Capture("Palette", config);
        config.PaletteSlots.Clear();

        Assert.Single(calibration.PaletteSlots);
        Assert.Single(profile.PaletteSlots);
        var target = new AutoDrawConfig();
        Assert.True(profile.TryApply(target, out var error), error);
        Assert.Equal("Palette Match", target.PickerType);
        Assert.Equal(new PaletteSlot(new ScreenPoint(10, 20), "#FF0000"), Assert.Single(target.PaletteSlots));
        Assert.Equal("#FF0000", Assert.Single(target.CapturedPalette));
    }
}

public class LxPainterProcessingTests
{
    [Fact]
    public void CapturedPalette_IsAHardConstraintForQuantization()
    {
        using var source = new SKBitmap(3, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        source.SetPixel(0, 0, new SKColor(250, 5, 5));
        source.SetPixel(1, 0, new SKColor(5, 250, 5));
        source.SetPixel(2, 0, new SKColor(5, 5, 250));
        var config = new AutoDrawConfig { UseBilateral = false, RemoveStray = false };
        config.CapturedPalette.AddRange(new[] { "#FF0000", "#00FF00" });

        var palette = ImageProcessing.ExtractPalette(source, 2, 0, config);
        Assert.Equal(new[] { SKColors.Red, SKColors.Lime }, palette);
    }

    [Fact]
    public void BackgroundTolerance_RemovesOnlyColoursWithinRgbDistance()
    {
        using var source = new SKBitmap(2, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        source.SetPixel(0, 0, new SKColor(100, 100, 100));
        source.SetPixel(1, 0, new SKColor(110, 100, 100));
        var config = new AutoDrawConfig
        {
            UseBilateral = false,
            RemoveBackground = true,
            BackgroundTolerance = 5
        };
        config.BackgroundColorsToRemove.Add("#646464");

        using var prepared = ImageProcessing.PrepareColorImage(source, config);
        Assert.Equal(0, prepared.GetPixel(0, 0).Alpha);
        Assert.Equal(255, prepared.GetPixel(1, 0).Alpha);
    }

    [Fact]
    public void BackgroundRemoval_CanBeDisabledWithoutClearingColours()
    {
        using var source = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        source.SetPixel(0, 0, new SKColor(100, 100, 100));
        var config = new AutoDrawConfig { UseBilateral = false, RemoveBackground = false, BackgroundTolerance = 5 };
        config.BackgroundColorsToRemove.Add("#646464");
        using var prepared = ImageProcessing.PrepareColorImage(source, config);
        Assert.Equal(255, prepared.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void CustomDimensions_SupportWidthOnlyAndHeightOnly()
    {
        using var source = new SKBitmap(100, 50);
        var config = new AutoDrawConfig { CustomWidth = 40, CustomHeight = 0 };
        using var widthOnly = ImageProcessing.ResizeToCustomDimensions(source, config);
        Assert.Equal(40, widthOnly.Width);
        Assert.Equal(20, widthOnly.Height);
        config.CustomWidth = 0;
        config.CustomHeight = 100;
        using var heightOnly = ImageProcessing.ResizeToCustomDimensions(source, config);
        Assert.Equal(200, heightOnly.Width);
        Assert.Equal(100, heightOnly.Height);
    }
}
