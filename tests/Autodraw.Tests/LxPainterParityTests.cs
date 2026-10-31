using System;
using System.Collections.Generic;
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
        // LxPainter commits every typed field with Enter so the swatch repaints before verification.
        Assert.Equal(1, input.EnterCalls);
    }

    [Fact]
    public async Task RgbPicker_ReplacesThreeFieldsInOrder()
    {
        var input = new RecordingInputSimulator();
        var points = new[] { new ScreenPoint(1, 2), new ScreenPoint(3, 4), new ScreenPoint(5, 6) };
        await new RgbColorPicker(input, Options(), points).SelectColorAsync(new SKColor(1, 2, 255), CancellationToken.None);

        Assert.Equal(points, input.Clicks);
        Assert.Equal(new[] { "1", "2", "255" }, input.ReplacedTexts);
        Assert.Equal(3, input.EnterCalls);
    }

    [Fact]
    public async Task HsvPicker_UsesDegreesAndPercentages()
    {
        var input = new RecordingInputSimulator();
        var points = new[] { new ScreenPoint(1, 2), new ScreenPoint(3, 4), new ScreenPoint(5, 6) };
        await new HsvColorPicker(input, Options(), points).SelectColorAsync(SKColors.Red, CancellationToken.None);
        Assert.Equal(new[] { "0", "100", "100" }, input.ReplacedTexts);
        Assert.Equal(3, input.EnterCalls);
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
    public async Task PaletteMatch_UsesRgbDistanceByDefault()
    {
        var input = new RecordingInputSimulator();
        var slots = new[]
        {
            new PaletteSlot(new ScreenPoint(1, 1), "#2E8210"),
            new PaletteSlot(new ScreenPoint(2, 2), "#242A08")
        };
        var options = Options();
        options.QuantizeMethod = "KMeans++ (Classic)";

        await new PaletteMatchColorPicker(input, options, slots)
            .SelectColorAsync(new SKColor(117, 90, 92), CancellationToken.None);

        // RGB distance prefers the green swatch; ΔE2000 prefers the near-black swatch.
        Assert.Equal(new[] { new ScreenPoint(1, 1) }, input.Clicks);
    }

    [Fact]
    public async Task PaletteMatch_UsesCielabWhenThatQuantizerIsActive()
    {
        var input = new RecordingInputSimulator();
        var slots = new[]
        {
            new PaletteSlot(new ScreenPoint(1, 1), "#2E8210"),
            new PaletteSlot(new ScreenPoint(2, 2), "#242A08")
        };
        var options = Options();
        options.QuantizeMethod = "CIELAB Method";

        await new PaletteMatchColorPicker(input, options, slots)
            .SelectColorAsync(new SKColor(117, 90, 92), CancellationToken.None);

        Assert.Equal(new[] { new ScreenPoint(2, 2) }, input.Clicks);
    }

    [Fact]
    public async Task PaletteMatch_UsesOkLabWhenThatQuantizerIsActive()
    {
        var input = new RecordingInputSimulator();
        var slots = new[]
        {
            new PaletteSlot(new ScreenPoint(1, 1), "#4FD831"),
            new PaletteSlot(new ScreenPoint(2, 2), "#A87F83")
        };
        var options = Options();
        options.QuantizeMethod = "OKLAB Method";

        await new PaletteMatchColorPicker(input, options, slots)
            .SelectColorAsync(new SKColor(88, 146, 57), CancellationToken.None);

        Assert.Equal(new[] { new ScreenPoint(2, 2) }, input.Clicks);
    }

    [Fact]
    public void PaletteMatchColorSpace_FollowsQuantizerUnlessExplicit()
    {
        Assert.Equal("RGB", RecreateOptions.ResolvePaletteMatchColorSpace("Wu's Quantizer"));
        Assert.Equal("RGB", RecreateOptions.ResolvePaletteMatchColorSpace("KMeans++ (Classic)"));
        Assert.Equal("CIELAB", RecreateOptions.ResolvePaletteMatchColorSpace("CIELAB Method"));
        Assert.Equal("CIELAB", RecreateOptions.ResolvePaletteMatchColorSpace("Lab Color Space"));
        Assert.Equal("OKLAB", RecreateOptions.ResolvePaletteMatchColorSpace("OKLAB Method"));
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

public class LxPainterConfigMappingTests
{
    [Theory]
    [InlineData("Largest to smallest", "coverage-desc")]
    [InlineData("Smallest to largest", "coverage-asc")]
    [InlineData("Brightness to darkness", "luminance-desc")]
    [InlineData("Darkness to brightness", "luminance-asc")]
    [InlineData("Random", "manual")]
    public void LayerOrder_LabelsMapToRecreateOrder(string label, string expected)
    {
        var config = LxPainterConfigMapper.FromLxPainter(
            JObject.Parse($$"""{"layer_order":"{{label}}"}"""));
        DdConfigService.Normalize(config);

        Assert.Equal(expected, config.RecreateLayerOrder);
        Assert.True(DdConfigService.Validate(config, out var error), error);
    }

    [Fact]
    public void ExplicitRecreateLayerOrder_IsNotOverwrittenByLayerOrder()
    {
        var config = LxPainterConfigMapper.FromLxPainter(JObject.Parse("""
        {"layer_order":"Smallest to largest","recreate_layer_order":"luminance-asc"}
        """));

        Assert.Equal("luminance-asc", config.RecreateLayerOrder);
    }

    [Theory]
    [InlineData("Smallest to largest", "coverage-asc")]
    [InlineData("Largest to smallest", "coverage-desc")]
    [InlineData("Random", "manual")]
    public void RecreateLayerOrder_NormalizesLxPainterLabels(string value, string expected)
    {
        var config = new AutoDrawConfig { RecreateLayerOrder = value };
        DdConfigService.Normalize(config);

        Assert.Equal(expected, config.RecreateLayerOrder);
        Assert.True(DdConfigService.Validate(config, out var error), error);
    }

    [Theory]
    [InlineData("Edge Traversal", "DFS (Default)")]
    [InlineData("dfs", "DFS (Default)")]
    [InlineData("Concentric", "Concentric")]
    public void Algorithm_NormalizesToLxPainterChoices(string value, string expected)
        => Assert.Equal(expected, DdConfigService.NormalizeAlgorithm(value));

    [Theory]
    [InlineData("Line", "1:1 Exact (AutoDraw)")]
    [InlineData("1:1 Exact (AutoDraw)", "1:1 Exact (AutoDraw)")]
    [InlineData("Pixel Size Scaling", "Pixel Size Scaling")]
    [InlineData("pixel scaling", "Pixel Size Scaling")]
    public void DrawMethod_NormalizesToLxPainterChoices(string value, string expected)
        => Assert.Equal(expected, DdConfigService.NormalizeDrawMethod(value));

    [Theory]
    [InlineData("NeuQuant Neural-Net")]
    [InlineData("KMeans++ (Original)")]
    [InlineData("KMeans (Random)")]
    [InlineData("KMeans++ (Classic)")]
    [InlineData("MiniBatchKMeans (Fast)")]
    [InlineData("Lab Color Space")]
    [InlineData("CIELAB Method")]
    [InlineData("OKLAB Method")]
    [InlineData("RGB Method")]
    [InlineData("Wu's Quantizer")]
    [InlineData("Lcquant")]
    [InlineData("Median Cut")]
    [InlineData("Div")]
    public void Validation_AcceptsEveryLxPainterQuantizer(string method)
    {
        var config = new AutoDrawConfig { QuantizeMethod = method, UseBilateral = false };
        Assert.True(DdConfigService.Validate(config, out var error), error);
    }

    [Fact]
    public void Validation_RejectsUnknownQuantizerDrawMethodAndAlgorithm()
    {
        Assert.False(DdConfigService.Validate(
            new AutoDrawConfig { QuantizeMethod = "Magic" }, out var quantizerError));
        Assert.Contains("quantize", quantizerError, StringComparison.OrdinalIgnoreCase);

        Assert.False(DdConfigService.Validate(
            new AutoDrawConfig { DrawMethod = "Wobble" }, out var drawError));
        Assert.Contains("drawing method", drawError, StringComparison.OrdinalIgnoreCase);

        Assert.False(DdConfigService.Validate(
            new AutoDrawConfig { Algorithm = "Edge Traversal" }, out var algorithmError));
        Assert.Contains("algorithm", algorithmError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validation_BoundsPixelSizeAt64()
    {
        Assert.True(DdConfigService.Validate(new AutoDrawConfig { PixelSize = 64 }, out _));
        Assert.False(DdConfigService.Validate(new AutoDrawConfig { PixelSize = 65 }, out var error));
        Assert.Contains("range", error, StringComparison.OrdinalIgnoreCase);
    }
}

public class LxPainterQuantizerTests
{
    private static SKBitmap Gradient(int width = 16, int height = 16)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            bitmap.SetPixel(x, y, new SKColor(
                (byte)(x * 255 / Math.Max(1, width - 1)),
                (byte)(y * 255 / Math.Max(1, height - 1)),
                (byte)((x * 37 + y * 73) % 256)));
        return bitmap;
    }

    private static AutoDrawConfig QuantizerConfig(string method) => new()
    {
        QuantizeMethod = method,
        UseBilateral = false,
        UseSeeds = false,
        RemoveStray = false
    };

    [Fact]
    public void ExtractPalette_DoesNotStrideAwaySparsePixels()
    {
        using var source = new SKBitmap(16, 16, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        source.SetPixel(7, 9, new SKColor(255, 0, 0));

        var palette = ImageProcessing.ExtractPalette(
            source, 2, 1, QuantizerConfig("KMeans++ (Classic)"));

        var color = Assert.Single(palette);
        Assert.InRange(color.Red, 240, 255);
        Assert.InRange(color.Green, 0, 20);
        Assert.InRange(color.Blue, 0, 20);
    }

    [Fact]
    public void ExtractPalette_ReturnsEveryCapturedSwatchBeyondPaletteSize()
    {
        var config = QuantizerConfig("KMeans++ (Classic)");
        config.CapturedPalette.AddRange(new[] { "#FF0000", "#00FF00", "#0000FF" });
        using var source = Gradient(4, 4);

        var palette = ImageProcessing.ExtractPalette(source, 1, 0, config);

        Assert.Equal(3, palette.Count);
        Assert.Equal(new[] { SKColors.Red, SKColors.Lime, SKColors.Blue }, palette);
    }

    [Theory]
    [InlineData("Wu's Quantizer")]
    [InlineData("Median Cut")]
    [InlineData("Lcquant")]
    [InlineData("Div")]
    public void BoxQuantizers_ReturnTheRequestedPaletteSize(string method)
    {
        using var source = Gradient();
        var palette = ImageProcessing.ExtractPalette(source, 4, 0, QuantizerConfig(method));

        Assert.Equal(4, palette.Count);
        Assert.Equal(4, palette.Distinct().Count());
    }

    [Theory]
    [InlineData("NeuQuant Neural-Net")]
    [InlineData("KMeans++ (Original)")]
    [InlineData("KMeans (Random)")]
    [InlineData("KMeans++ (Classic)")]
    [InlineData("MiniBatchKMeans (Fast)")]
    [InlineData("Lab Color Space")]
    [InlineData("CIELAB Method")]
    [InlineData("OKLAB Method")]
    [InlineData("RGB Method")]
    public void EveryQuantizer_ProducesAUsableDistinctPalette(string method)
    {
        using var source = Gradient();
        var palette = ImageProcessing.ExtractPalette(source, 4, 0, QuantizerConfig(method));

        Assert.InRange(palette.Count, 1, 4);
        Assert.Equal(palette.Count, palette.Distinct().Count());
    }

    [Fact]
    public void ResolveColorSpace_FollowsQuantizerUntilAnExplicitSpaceWins()
    {
        Assert.Equal("RGB", ImageProcessing.ResolveColorSpace(QuantizerConfig("KMeans++ (Classic)")));
        Assert.Equal("RGB", ImageProcessing.ResolveColorSpace(QuantizerConfig("Wu's Quantizer")));
        Assert.Equal("CIELAB", ImageProcessing.ResolveColorSpace(QuantizerConfig("CIELAB Method")));
        Assert.Equal("CIELAB", ImageProcessing.ResolveColorSpace(QuantizerConfig("Lab Color Space")));
        Assert.Equal("OKLAB", ImageProcessing.ResolveColorSpace(QuantizerConfig("OKLAB Method")));

        var explicitRgb = QuantizerConfig("OKLAB Method");
        explicitRgb.ColorDistanceMethod = "RGB";
        Assert.Equal("RGB", ImageProcessing.ResolveColorSpace(explicitRgb));
    }

    [Fact]
    public void SeedsSuperpixels_AverageAWholeRegionIntoOneColour()
    {
        using var source = new SKBitmap(4, 4, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
            source.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.Black : SKColors.White);

        using var result = ImageProcessing.ApplySeedsSuperpixels(source, 1, iterations: 1);

        var color = result.GetPixel(0, 0);
        Assert.InRange(color.Red, 120, 135);
        Assert.Equal(color.Red, color.Green);
        Assert.Equal(color.Red, color.Blue);
        Assert.Equal(SKColors.Black, source.GetPixel(0, 0));
    }

    [Fact]
    public void SeedsAreSkippedForLcquantButAppliedForOtherQuantizers()
    {
        using var source = new SKBitmap(4, 4, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
            source.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.Black : SKColors.White);

        var config = QuantizerConfig("KMeans++ (Classic)");
        config.UseSeeds = true;
        config.SeedsCount = 1;
        using var smoothed = ImageProcessing.PrepareQuantizationSource(source, config);
        Assert.InRange(smoothed.GetPixel(0, 0).Red, 120, 135);

        config.QuantizeMethod = "Lcquant";
        using var skipped = ImageProcessing.PrepareQuantizationSource(source, config);
        Assert.Equal(SKColors.Black, skipped.GetPixel(0, 0));
    }

    [Fact]
    public void LabSpatialFilter_OnlyRunsForLabColorSpaceQuantizer()
    {
        using var source = new SKBitmap(6, 6, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < 6; x++)
            source.SetPixel(x, y, x < 3 ? SKColors.Black : SKColors.White);

        var plain = QuantizerConfig("KMeans++ (Classic)");
        using var untouched = ImageProcessing.PrepareQuantizationSource(source, plain);
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < 6; x++)
            Assert.Equal(source.GetPixel(x, y), untouched.GetPixel(x, y));

        var lab = QuantizerConfig("Lab Color Space");
        lab.LabContrastThreshold = 1000;
        lab.LabRangeSigma = 1000;
        using var filtered = ImageProcessing.PrepareQuantizationSource(source, lab);
        var changed = 0;
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < 6; x++)
            if (filtered.GetPixel(x, y) != source.GetPixel(x, y)) changed++;
        Assert.True(changed > 0, "LAB spatial filtering should blend pixels across the edge.");
    }

    [Fact]
    public void LabSpatialFilter_KeepsRedAndBlueChannelsInPlace()
    {
        // A uniform red field has zero gradient, so the edge mask is 0 and the output is the
        // bilateral average of an identical neighbourhood: red must stay red, not turn blue.
        using var source = Solid(SKColors.Red);
        var config = QuantizerConfig("Lab Color Space");
        using var filtered = ImageProcessing.ApplyLabSpatialFeatures(source, config);
        var color = filtered.GetPixel(4, 4);

        Assert.InRange(color.Red, 250, 255);
        Assert.InRange(color.Green, 0, 5);
        Assert.InRange(color.Blue, 0, 5);
    }

    [Fact]
    public void SeedsSuperpixels_KeepsRedAndBlueChannelsInPlace()
    {
        // Averaging a uniform blue region must reproduce blue, not its channel swap.
        using var source = Solid(SKColors.Blue);
        using var result = ImageProcessing.ApplySeedsSuperpixels(source, 1, iterations: 2);
        var color = result.GetPixel(4, 4);

        Assert.InRange(color.Red, 0, 5);
        Assert.InRange(color.Green, 0, 5);
        Assert.InRange(color.Blue, 250, 255);
    }

    private static SKBitmap Solid(SKColor color)
    {
        var bitmap = new SKBitmap(8, 8, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            bitmap.SetPixel(x, y, color);
        return bitmap;
    }

    private static List<ColorLayerResult> SplitWithStrayConfig(SKBitmap source, bool removeStray)
    {
        var config = new AutoDrawConfig
        {
            RemoveStray = removeStray,
            UseSeeds = false,
            QuantizeStyle = "Pixelated (Classic)",
            UseBilateral = false
        };
        return ImageProcessing.SplitImageByColors(
            source,
            new List<SKColor> { SKColors.Red, SKColors.Blue },
            0,
            config);
    }

    [Fact]
    public void StrayRemoval_MergesAnIsolatedPixelIntoItsDominantNeighbour()
    {
        using var source = new SKBitmap(5, 5, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < 5; y++)
        for (var x = 0; x < 5; x++)
            source.SetPixel(x, y, SKColors.Red);
        source.SetPixel(2, 2, SKColors.Blue);

        var layers = SplitWithStrayConfig(source, removeStray: true);
        try
        {
            Assert.Equal(25, layers[0].PixelCount);
            Assert.Equal(0, layers[1].PixelCount);
            Assert.Equal(SKColors.Red, layers[0].Bitmap.GetPixel(2, 2));
        }
        finally
        {
            foreach (var layer in layers) layer.Bitmap.Dispose();
        }
    }

    [Fact]
    public void StrayRemoval_DisabledKeepsTheIsolatedPixel()
    {
        using var source = new SKBitmap(5, 5, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < 5; y++)
        for (var x = 0; x < 5; x++)
            source.SetPixel(x, y, SKColors.Red);
        source.SetPixel(2, 2, SKColors.Blue);

        var layers = SplitWithStrayConfig(source, removeStray: false);
        try
        {
            Assert.Equal(24, layers[0].PixelCount);
            Assert.Equal(1, layers[1].PixelCount);
        }
        finally
        {
            foreach (var layer in layers) layer.Bitmap.Dispose();
        }
    }
}

public class LxPainterDrawMethodTests
{
    [Fact]
    public void ResizeForDrawMethod_ScalesDownOnlyForPixelSizeScaling()
    {
        using var source = new SKBitmap(20, 20, SKColorType.Bgra8888, SKAlphaType.Unpremul);

        using var exact = ImageProcessing.ResizeForDrawMethod(source, 20, 20, pixelScaling: false, pixelSize: 4);
        Assert.Equal(20, exact.Width);
        Assert.Equal(20, exact.Height);

        using var scaled = ImageProcessing.ResizeForDrawMethod(source, 20, 20, pixelScaling: true, pixelSize: 4);
        Assert.Equal(5, scaled.Width);
        Assert.Equal(5, scaled.Height);

        using var clamped = ImageProcessing.ResizeForDrawMethod(source, 3, 3, pixelScaling: true, pixelSize: 10);
        Assert.Equal(1, clamped.Width);
        Assert.Equal(1, clamped.Height);
    }

    [Fact]
    public void Analyze_PixelSizeScalingProcessesAtOnePixelPerBlock()
    {
        using var source = new SKBitmap(20, 20, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < 20; y++)
        for (var x = 0; x < 20; x++)
            source.SetPixel(x, y, x < 10 ? SKColors.Red : SKColors.Blue);

        var options = RecreateTestData.Options(new ScreenRect(0, 0, 19, 19));
        options.PaletteSize = 2;
        options.DrawMethod = "Pixel Size Scaling";
        options.PixelSize = 2;
        var config = new AutoDrawConfig { Mode = "color", UseBilateral = false, RemoveStray = false };

        var result = ImageAnalyzer.Analyze(source, options, config, CancellationToken.None);

        Assert.Equal(10, result.Width);
        Assert.Equal(10, result.Height);
        Assert.Equal(100, result.Layers.Sum(layer => layer.PixelCount));
        result.Dispose();
    }

    [Fact]
    public void Options_MapDrawMethodPixelSizeAlgorithmAndSearch()
    {
        var config = new AutoDrawConfig
        {
            DrawMethod = "Pixel Size Scaling",
            PixelSize = 3,
            Algorithm = "Edge Traversal",
            DfsMode = "4-Directional",
            QuantizeMethod = "OKLAB Method"
        };

        var options = RecreateOptions.FromConfig(config);

        Assert.Equal("Pixel Size Scaling", options.DrawMethod);
        Assert.True(options.IsPixelSizeScaling);
        Assert.Equal(3, options.PixelSize);
        Assert.Equal("DFS (Default)", options.Algorithm);
        Assert.Equal("4-Directional", options.DfsMode);
        Assert.Equal("OKLAB", options.PaletteColorSpace);
        Assert.Equal("OKLAB", options.PaletteMatchColorSpace);

        var plan = options.ToStrokePlanOptions();
        Assert.Equal(StrokeAlgorithm.Dfs, plan.Algorithm);
        Assert.True(plan.FourDirectional);
        Assert.Equal("Pixel Size Scaling", plan.DrawMethod);
        Assert.Equal(3, plan.PixelSize);
    }
}

public class LxPainterCanvasResolutionTests
{
    [Fact]
    public void AreaMode_RequiresASelectionWithPixels()
    {
        var config = new AutoDrawConfig { LocationMode = 0, AreaSelected = false };

        var error = Assert.Throws<InvalidOperationException>(
            () => RecreateOptions.ResolveCanvas(config, 100, 100));
        Assert.Contains("area", error.Message, StringComparison.OrdinalIgnoreCase);

        config.AreaSelected = true;
        config.DrawRegion = new ScreenRect(0, 0, -1, 10);
        Assert.Throws<InvalidOperationException>(() => RecreateOptions.ResolveCanvas(config, 100, 100));
    }

    [Fact]
    public void AreaMode_UsesTheSelectedRectangle()
    {
        var config = new AutoDrawConfig
        {
            LocationMode = 0,
            AreaSelected = true,
            DrawRegion = new ScreenRect(10, 20, 109, 69)
        };

        Assert.Equal(new ScreenRect(10, 20, 109, 69),
            RecreateOptions.ResolveCanvas(config, 500, 500));
    }

    [Fact]
    public void DragMode_SizesFromCustomDimensionsOrTheImage()
    {
        var config = new AutoDrawConfig
        {
            LocationMode = 1,
            AreaSelected = true,
            DrawRegion = new ScreenRect(10, 20, 109, 69),
            CustomWidth = 100,
            CustomHeight = 50
        };
        Assert.Equal(new ScreenRect(10, 20, 109, 69),
            RecreateOptions.ResolveCanvas(config, 500, 500));

        config.AreaSelected = false;
        config.CustomWidth = 0;
        config.CustomHeight = 0;
        Assert.Equal(new ScreenRect(0, 0, 499, 499),
            RecreateOptions.ResolveCanvas(config, 500, 500));
    }
}

public class LxPainterStrokeOrderingTests
{
    private static SKBitmap TwoComponents()
    {
        var mask = new SKBitmap(6, 6, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < 6; x++)
        {
            var opaque = (x < 3 && y < 3) || (x == 5 && y == 0);
            mask.SetPixel(x, y, opaque ? new SKColor(255, 0, 0, 255) : new SKColor(0, 0, 0, 0));
        }
        return mask;
    }

    [Fact]
    public void Dfs_VisitsTheLargestComponentFirstAndCoversEveryRun()
    {
        using var mask = TwoComponents();
        var mapper = new CanvasMapper(6, 6, new ScreenRect(0, 0, 5, 5));

        var strokes = StrokePlanner.Plan(mask, mapper, new StrokePlanOptions
        {
            AlphaThreshold = 200,
            BrushSize = 1,
            Algorithm = StrokeAlgorithm.Dfs
        });

        Assert.Equal(4, strokes.Count);
        Assert.All(strokes.Take(3), stroke => Assert.True(stroke.Start.Y <= 2));
        Assert.Equal(5, strokes[^1].Start.X);
        Assert.Equal(5, strokes[^1].End.X);
    }

    [Fact]
    public void Concentric_StartsAtTheComponentEdgeAndCoversEveryRun()
    {
        using var mask = RecreateTestData.Mask(5, 5, (x, y) => !(x == 2 && y == 2));
        var mapper = new CanvasMapper(5, 5, new ScreenRect(0, 0, 4, 4));

        var strokes = StrokePlanner.Plan(mask, mapper, new StrokePlanOptions
        {
            AlphaThreshold = 200,
            BrushSize = 1,
            Algorithm = StrokeAlgorithm.Concentric
        });

        Assert.Equal(6, strokes.Count);
        Assert.Equal(0, strokes[0].Start.Y);
        Assert.Equal(0, strokes[0].End.Y);
    }
}
