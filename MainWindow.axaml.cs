using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Newtonsoft.Json;
using SharpHook;
using SharpHook.Native;
using SkiaSharp;
using Autodraw.Configuration;

namespace Autodraw;

public class ActionDisp
{
    public string Text { get; set; }
    public InputAction boundAction { get; set; }
    public int Speed { get; set; }
    public int Delay { get; set; }
}

public class LayerDisp
{
    public string HexColor { get; set; }
    public IBrush HexColorBrush { get; set; }
    public Bitmap Thumbnail { get; set; }
    public SKBitmap RawBitmap { get; set; }
    public SKColor Color { get; set; }
    public int PixelCount { get; set; }
    public string PixelCountText => $"{PixelCount} pixels";
    public bool Enabled { get; set; } = true;
}

public partial class MainWindow : Window
{
    public static MainWindow? CurrentMainWindow;
    private OpenAIPrompt? _aiPrompt;
    private DevTest? _devwindow;
    private Bitmap? _displayedBitmap;
    private bool _inChange;

    private readonly Regex _numberRegex = new(@"[^0-9]");

    // Automation
    public ObservableCollection<ActionDisp> ActionsContext { get; set; } = new();
    public ObservableCollection<LayerDisp> LayersContext { get; set; } = new();
    private List<InputAction> _actionStack = new();
    List<ColorLayerResult> _layersStack = new();

    // HexedDrawer integration
    private HexedDrawWindow? _hexedDrawWindow;
    private bool _hexedDrawRunning;
    private bool _quantizing;
    private int _quantizeGeneration;
    private bool _imageEnhanced;
    private bool _enhanceFailed;
    private bool _hasImportedImage;

    public bool HasImportedImage => _hasImportedImage && _preFxBitmap is { Width: > 1, Height: > 1 };
    public string? ImportedImagePath { get; private set; }
    public Bitmap? QuantizedPreview { get; private set; }
    public bool IsCapturingColorPicker => _capturingColorPicker;
    public event EventHandler? QuantizedPreviewChanged;
    public event EventHandler? QuantizedPreviewInvalidated;

    private long _lastMem;
    private long _lastTime = DateTime.Now.ToFileTime();
    private int _maxBlackThreshold = 127;
    private int _alphaThresh = 200;

    private int _minBlackThreshold;
    private SKBitmap? _preFxBitmap = new(318, 318, true);
    private SKBitmap? _processedBitmap;

    private SKBitmap? _rawBitmap = new(318, 318, true);

    private Settings? _settings;
    private readonly List<Vector2> _colorPickerCalibrationPoints = new();
    private bool _capturingColorPicker;

    /// <summary>Which calibration scheme the next captured clicks belong to.</summary>
    private enum ColorPickerCalibrationMode
    {
        Wheel,
        Slider,
        Circle,
        Brightness,
        BoxHue,
        Box
    }

    private ColorPickerCalibrationMode _calibrationMode = ColorPickerCalibrationMode.Wheel;

    public int widthLock = 0;
    public int heightLock = 0;
    public int widthNumber = 1;
    public int heightNumber = 1;

    public MainWindow()
    {
        DataContext = this; // This stupid piece of shit is required, unlike normal Xaml, fuck it.
        
        InitializeComponent();
        UpdateColorPickerModeStatus();

        if (Design.IsDesignMode) return;

        this.AttachDevTools();

        // Set language to user-specified language 
        var installedLanguage = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        Thread.CurrentThread.CurrentCulture = new CultureInfo(Config.GetEntry("userlang") ?? installedLanguage);
        Thread.CurrentThread.CurrentUICulture = new CultureInfo(Config.GetEntry("userlang") ?? installedLanguage);
        Utils.Log(installedLanguage);

        CurrentMainWindow = this;
        // Onboarding
        //if (!File.Exists(Config.ConfigPath)) 
        //new Onboarding(CurrentMainWindow);
        Config.init();
        LocalizationManager.Load(Config.GetEntry("userlang") ?? installedLanguage);
        DdConfigService.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyDdConfigToUi);
        ApplyDdConfigToUi();

        UpdateActionsContext();

        // Taskbar
        CloseAppButton.Click += (_, _) => Close();
        MinimizeAppButton.Click += MinimizeAppOnClick;
        SettingsButton.Click += OpenSettingsOnClick;
        DevButton.Click += (_, _) => OpenDevWindow();
        ColorPickerHelpButton.Click += ShowColorPickerHelp;
        HexedDrawButton.Click += (_, _) => OpenHexedDrawWindow();
        UpdateColorPickerModeStatus();

        // Base
        Closing += (_, _) => Cleanup();
        OpenButton.Click += OpenButtonOnClick;
        ProcessButton.Click += ProcessButtonOnClick;
        RunButton.Click += RunButtonOnClick;

        ImageAIGeneration.Click += ImageAIGenerationOnClick;
        ImageSaveImage.Click += ImageSaveImageOnClick;
        ImageClearImage.Click += ImageClearImageOnClick;

        // Inputs
        SizeSlider.ValueChanged += SizeSliderOnValueChanged;
        WidthInput.TextChanging += WidthInputOnTextChanged;
        HeightInput.TextChanging += HeightInputOnTextChanged;
        
        WidthLock.Click += WidthLockOnClick;
        HeightLock.Click += HeightLockOnClick;
        
        PercentageNumber.TextChanging += PercentageNumberOnTextChanged;

        DrawIntervalElement.TextChanging += DrawIntervalOnTextChanging;
        ClickDelayElement.TextChanging += ClickDelayOnTextChanging;
        minBlackThresholdElement.TextChanging += minBlackThresholdElementOnTextChanging;
        maxBlackThresholdElement.TextChanging += maxBlackThresholdElementOnTextChanging;
        AlphaThresholdElement.TextChanging += AlphaThresholdOnTextChanging;

        FreeDrawCheckbox.Click += FreeDrawCheckboxOnClick;
        AlgorithmSelection.SelectionChanged += AlgorithmSelectionOnSelectionChanged;

        EventHandler<TextChangingEventArgs> textChangeEvent = (sender, e) => HandleTextChange(e);
        HorizontalFilterText.TextChanging += textChangeEvent;
        VerticalFilterText.TextChanging += textChangeEvent;
        BorderAdvancedText.TextChanging += textChangeEvent;
        OutlineAdvancedText.TextChanging += textChangeEvent;
        InlineAdvancedText.TextChanging += textChangeEvent;
        InlineBorderAdvancedText.TextChanging += textChangeEvent;
        ErosionAdvancedText.TextChanging += textChangeEvent;

        // Config
        RefreshConfigsButton.Click += RefreshConfigList;
        SelectFolderElement.Click += SetConfigFolderViaDialog;
        SaveConfigButton.Click += SaveConfigViaDialog;
        OpenConfigElement.Click += LoadConfigViaDialog;
        LoadSelectButton.Click += LoadSelectedConfig;
        RefreshConfigList(this, null);

        Input.Start();
        Input.taskHook.KeyReleased += ConfigureColorPicker;
        Input.taskHook.KeyPressed += HexedDrawHotkeys;
    }

    /// <summary>Global HexedDrawer hotkeys: F1 area, F2 start/pause, F3 stop, F4 reset, F6 quantize.</summary>
    private void HexedDrawHotkeys(object? sender, KeyboardHookEventArgs e)
    {
        if (_capturingColorPicker) return;
        switch (e.Data.KeyCode)
        {
            case KeyCode.VcF1:
                Dispatcher.UIThread.Post(async () => await SelectDrawAreaAsync());
                break;
            case KeyCode.VcF2:
                Dispatcher.UIThread.Post(async () => await StartPauseHexedDrawAsync());
                break;
            case KeyCode.VcF3:
                Dispatcher.UIThread.Post(() =>
                {
                    var wasDrawing = Drawing.IsDrawing || _hexedDrawRunning;
                    StopHexedDraw();
                    if (!wasDrawing) _hexedDrawWindow?.Close();
                });
                break;
            case KeyCode.VcF4:
                Dispatcher.UIThread.Post(ResetHexedDraw);
                break;
            case KeyCode.VcF6:
                Dispatcher.UIThread.Post(async () => await QuantizeForHexedDrawAsync());
                break;
        }
    }

    // User Configuration Handles

    //*
    public static FilePickerFileType ConfigsFileFilter { get; } = new("AutoDraw Config Files")
    {
        Patterns = new[] { "*.drawcfg" }
    };
    public static FilePickerFileType DdConfigFileFilter { get; } = new("dd.json Configuration") { Patterns = new[] { "*.json" } };

    public static FilePickerFileType PngFileFilter { get; } = new("Portable Network Graphics")
    {
        Patterns = new[] { "*.png" }
    };

    private void ImageAIGenerationOnClick(object? sender, RoutedEventArgs e)
    {
        if (_aiPrompt is not null) return;
        _aiPrompt = new OpenAIPrompt();
        _aiPrompt.Show();
        _aiPrompt.Closed += AiPromptOnClosed;
    }

    private void AiPromptOnClosed(object? sender, EventArgs e)
    {
        _aiPrompt = null;
    }

    // Core Functions

    public void Cleanup()
    {
        _devwindow?.Close();
        _settings?.Close();
        _aiPrompt?.Close();
        if (Utils.LogObject != null) Utils.LogObject.Close();
        Input.Stop();
        Drawing.Halt();
    }

    /// <summary>
    /// Listens for the calibration hotkeys on the global hook.
    ///   F5 -> capture four points for the wheel color picker (center, rim, brightness top, brightness bottom).
    ///   F7 -> capture six points for the slider color picker (min/max of the Hue, Saturation, and Value sliders).
    /// HexedDrawer keeps F6 for Quantize and exposes the separated circle/brightness/box
    /// calibrations through the Settings tab buttons.
    /// </summary>
    private void ConfigureColorPicker(object? sender, KeyboardHookEventArgs e)
    {
        if (_capturingColorPicker) return;
        if (e.Data.KeyCode == KeyCode.VcF5)
        {
            StartColorPickerCalibration(ColorPickerCalibrationMode.Wheel);
        }
        else if (e.Data.KeyCode == KeyCode.VcF7)
        {
            StartColorPickerCalibration(ColorPickerCalibrationMode.Slider);
        }
    }

    /// <summary>Starts a calibration capture programmatically (used by the HexedDrawer Settings tab).</summary>
    public void BeginColorPickerCalibration(string mode)
    {
        if (_capturingColorPicker) return;
        var parsed = mode switch
        {
            "Circle" => ColorPickerCalibrationMode.Circle,
            "Brightness" => ColorPickerCalibrationMode.Brightness,
            "Slider" => ColorPickerCalibrationMode.Slider,
            "BoxHue" => ColorPickerCalibrationMode.BoxHue,
            "Box" => ColorPickerCalibrationMode.Box,
            _ => ColorPickerCalibrationMode.Wheel
        };
        StartColorPickerCalibration(parsed);
    }

    private void StartColorPickerCalibration(ColorPickerCalibrationMode mode)
    {
        _capturingColorPicker = true;
        _calibrationMode = mode;
        _colorPickerCalibrationPoints.Clear();
        Input.taskHook.MouseClicked += CaptureColorPickerPoint;
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() => WindowState = WindowState.Minimized);
    }

    private void CaptureColorPickerPoint(object? sender, MouseHookEventArgs e)
    {
        if (e.Data.Button != SharpHook.Native.MouseButton.Button1) return;
        _colorPickerCalibrationPoints.Add(new Vector2(e.Data.X, e.Data.Y));

        var requiredPoints = _calibrationMode switch
        {
            ColorPickerCalibrationMode.Wheel => 4,
            ColorPickerCalibrationMode.Slider => 6,
            _ => 2
        };
        if (_colorPickerCalibrationPoints.Count < requiredPoints) return;

        switch (_calibrationMode)
        {
            case ColorPickerCalibrationMode.Wheel:
                ColorPicker.SaveWheelCalibration(
                    _colorPickerCalibrationPoints[0], _colorPickerCalibrationPoints[1],
                    _colorPickerCalibrationPoints[2], _colorPickerCalibrationPoints[3]);
                break;
            case ColorPickerCalibrationMode.Slider:
                ColorPicker.SaveSliderCalibration(
                    _colorPickerCalibrationPoints[0], _colorPickerCalibrationPoints[1],
                    _colorPickerCalibrationPoints[2], _colorPickerCalibrationPoints[3],
                    _colorPickerCalibrationPoints[4], _colorPickerCalibrationPoints[5]);
                break;
            case ColorPickerCalibrationMode.Circle:
                ColorPicker.SaveCircleCalibration(_colorPickerCalibrationPoints[0], _colorPickerCalibrationPoints[1]);
                break;
            case ColorPickerCalibrationMode.Brightness:
                ColorPicker.SaveBrightnessCalibration(_colorPickerCalibrationPoints[0], _colorPickerCalibrationPoints[1]);
                break;
            case ColorPickerCalibrationMode.BoxHue:
                SaveBoxHueCalibration(_colorPickerCalibrationPoints[0], _colorPickerCalibrationPoints[1]);
                break;
            case ColorPickerCalibrationMode.Box:
                SaveBoxCalibration(_colorPickerCalibrationPoints[0], _colorPickerCalibrationPoints[1]);
                break;
        }

        Input.taskHook.MouseClicked -= CaptureColorPickerPoint;
        _capturingColorPicker = false;
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
        {
            WindowState = WindowState.Normal;
            UpdateColorPickerModeStatus();
            var message = _calibrationMode switch
            {
                ColorPickerCalibrationMode.Wheel => "Wheel calibration saved. The Set Color Hex action will now select colors using the calibrated wheel.",
                ColorPickerCalibrationMode.Slider => "Slider calibration saved. The Set Color Hex action will now drive the Hue, Saturation, and Value sliders in order.",
                ColorPickerCalibrationMode.Circle => "Color circle centre and rim saved.",
                ColorPickerCalibrationMode.Brightness => "Brightness slider endpoints saved.",
                ColorPickerCalibrationMode.BoxHue => "Color box hue slider endpoints saved.",
                _ => "Color box corners saved."
            };
            new MessageBox().ShowMessageBox("Color picker configured", message);
        });
    }

    private static void SaveBoxHueCalibration(Vector2 start, Vector2 end)
    {
        var config = DdConfigService.Current;
        config.ColorPickingMethod = "Box";
        config.PickerType = "Box";
        config.BoxHueStart = new ScreenPoint((int)start.X, (int)start.Y);
        config.BoxHueEnd = new ScreenPoint((int)end.X, (int)end.Y);
        if (!DdConfigService.TryApply(config, out var error)) Utils.Log("Box hue calibration rejected: " + error);
    }

    private static void SaveBoxCalibration(Vector2 topLeft, Vector2 bottomRight)
    {
        var config = DdConfigService.Current;
        config.ColorPickingMethod = "Box";
        config.PickerType = "Box";
        config.BoxTopLeft = new ScreenPoint((int)topLeft.X, (int)topLeft.Y);
        config.BoxBottomRight = new ScreenPoint((int)bottomRight.X, (int)bottomRight.Y);
        if (!DdConfigService.TryApply(config, out var error)) Utils.Log("Box calibration rejected: " + error);
    }

    private void UpdateColorPickerModeStatus()
    {
        var mode = Config.GetEntry("ColorPickerMode");
        ColorPickerModeLabel.Content = mode switch
        {
            "Wheel" => "Color Picker: Wheel",
            "Slider" => "Color Picker: Slider (press F6 to re-calibrate)",
            _ => "Color Picker: Wheel (press F5 to calibrate)"
        };
    }

    private void ShowColorPickerHelp(object? sender, RoutedEventArgs e)
    {
        new MessageBox().ShowMessageBox("Color Picker Setup", ColorPicker.GetUsageInstructions());
    }
    
    public ImageProcessing.Filters GetSelectFilters() // This has practically become an Update _CurrentFilters if anything, but aight.
    {
        // Generic Filters
        ImageProcessing._currentFilters.MinThreshold = (byte)_minBlackThreshold;
        ImageProcessing._currentFilters.MaxThreshold = (byte)_maxBlackThreshold;
        ImageProcessing._currentFilters.AlphaThreshold = (byte)_alphaThresh;

        // Primary Filters

        //// Generic Filters
        ImageProcessing._currentFilters.Invert = InvertFilterCheck.IsChecked ?? false;
        ImageProcessing._currentFilters.Outline = OutlineFilterCheck.IsChecked ?? false;

        //// Pattern Filters
        ImageProcessing._currentFilters.Crosshatch = CrosshatchFilterCheck.IsChecked ?? false;
        ImageProcessing._currentFilters.DiagCrosshatch = DiagCrossFilterCheck.IsChecked ?? false;
        ImageProcessing._currentFilters.HorizontalLines = int.Parse(HorizontalFilterText.Text ?? "0");
        ImageProcessing._currentFilters.VerticalLines = int.Parse(VerticalFilterText.Text ?? "0");

        //// Experimental Filters
        ImageProcessing._currentFilters.BorderAdvanced = int.Parse(BorderAdvancedText.Text ?? "0");
        ImageProcessing._currentFilters.OutlineAdvanced = int.Parse(OutlineAdvancedText.Text ?? "0");
        ImageProcessing._currentFilters.InlineAdvanced = int.Parse(InlineAdvancedText.Text ?? "0");
        ImageProcessing._currentFilters.InlineBorderAdvanced = int.Parse(InlineBorderAdvancedText.Text ?? "0");
        ImageProcessing._currentFilters.ErosionAdvanced = int.Parse(ErosionAdvancedText.Text ?? "0");

        // Dither Filters
        // **Yet to be implemented**

        return ImageProcessing._currentFilters;
    }

    // External Window Opening/Closing Handles

    private void OpenSettingsOnClick(object? sender, RoutedEventArgs e)
    {
        if (_settings is not null) return;
        _settings = new Settings();
        _settings.Show();
        _settings.Closed += Settings_Closed;
    }

    private void Settings_Closed(object? sender, EventArgs e)
    {
        _settings = null;
    }

    private void OpenDevWindow()
    {
        _devwindow ??= new DevTest();
        _devwindow.Show();
        _devwindow.Closed += DevWindow_Closed;
    }

    private void DevWindow_Closed(object? sender, EventArgs e)
    {
        _devwindow = null;
    }
    
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.GetPosition(this).Y <= 20)
            BeginMoveDrag(e);
    }

    // Base UI Handles

    private void ProcessButtonOnClick(object? sender, RoutedEventArgs e)
    {
        if (_preFxBitmap.IsNull) return;
        _processedBitmap?.Dispose();
        _displayedBitmap?.Dispose();

        _processedBitmap = ImageProcessing.Process(_preFxBitmap, GetSelectFilters());
        _displayedBitmap = _processedBitmap.ConvertToAvaloniaBitmap();
        ImagePreview.Image = _displayedBitmap;
    }

    public void ImportImage(string? path, byte[]? img = null)
    {
        foreach (var layer in LayersContext)
        {
            layer.RawBitmap?.Dispose();
        }
        LayersContext.Clear();
        _layersStack.Clear();

        _rawBitmap?.Dispose();
        _rawBitmap = img is null ? SKBitmap.Decode(path).NormalizeColor() : SKBitmap.Decode(img).NormalizeColor();
        _preFxBitmap?.Dispose();
        _preFxBitmap = _rawBitmap.Copy();
        _displayedBitmap?.Dispose();
        _displayedBitmap = _rawBitmap.NormalizeColor().ConvertToAvaloniaBitmap();
        _processedBitmap?.Dispose();
        _processedBitmap = null;
        ImagePreview.Image = _displayedBitmap;

        _hasImportedImage = true;
        ImportedImagePath = path;
        _imageEnhanced = false;
        _enhanceFailed = false;
        _quantizeGeneration++;
        QuantizedPreviewInvalidated?.Invoke(this, EventArgs.Empty);
        QuantizedPreview?.Dispose();
        QuantizedPreview = null;
        QuantizedPreviewChanged?.Invoke(this, EventArgs.Empty);
        Drawing.ResetProgress();

        _inChange = true;
        SizeSlider.Value = 100;

        PercentageNumber.Text = $"{Math.Round(SizeSlider.Value)}%";
        WidthInput.Text = widthLock > 0 ? widthLock.ToString() : _displayedBitmap.Size.Width.ToString();
        HeightInput.Text =  heightLock > 0 ? heightLock.ToString() : _displayedBitmap.Size.Height.ToString();
        _inChange = false;
        
        if (widthLock > 0 || heightLock > 0) ResizeImage(_displayedBitmap.Size.Width, _displayedBitmap.Size.Height);
    }

    private async void OpenButtonOnClick(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Image",
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
            AllowMultiple = false
        });

        if (file.Count == 1) ImportImage(file[0].TryGetLocalPath());
    }

    private void RunButtonOnClick(object? sender, RoutedEventArgs e)
    {
        _layersStack.Clear();
        if (LayersContext != null && LayersContext.Count > 0)
        {
            foreach (var layer in LayersContext)
            {
                if (layer.Enabled)
                {
                    _layersStack.Add(new ColorLayerResult
                    {
                        Bitmap = layer.RawBitmap,
                        Color = layer.Color,
                        PixelCount = layer.PixelCount
                    });
                }
            }
        }

        if (_processedBitmap == null && _layersStack.Count == 0)
        {
            new MessageBox().ShowMessageBox("Error!", "Please select and process an image beforehand.", "error");
            return;
        }

        // Windows doesn't ask for permissions before mouse movement, Linux (wayland) and macOS require it.
        // We just create an empty Mouse Movement to trigger the popup
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var hook = new TaskPoolGlobalHook();
            hook.MouseMoved += (o, args) => { }; // Start listening for input
            hook.RunAsync();
        }
        if (Drawing.IsDrawing) return;
        Drawing.ChosenAlgorithm = (byte)AlgorithmSelection.SelectedIndex;
        Drawing.ClipToRegion = false;

        if (_layersStack.Count > 0)
        {
            new Preview().ReadyStackDraw(_preFxBitmap, _layersStack, _actionStack);
        }
        else
        {
            new Preview().ReadyDraw(_processedBitmap);
        }
        WindowState = WindowState.Minimized;
    }

    private async void ImageSaveImageOnClick(object? sender, RoutedEventArgs e)
    {
        if (_processedBitmap is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Processed Image",
            FileTypeChoices = new[] { PngFileFilter }
        });

        if (file is not null)
        {
            var encodedData = _processedBitmap.Encode(SKEncodedImageFormat.Png, 100);
            await using var stream = await file.OpenWriteAsync();

            encodedData.SaveTo(stream);
        }
    }

    private void ImageClearImageOnClick(object? sender, RoutedEventArgs e)
    {
        foreach (var layer in LayersContext)
        {
            layer.RawBitmap?.Dispose();
        }
        LayersContext.Clear();
        _layersStack.Clear();

        _rawBitmap?.Dispose();
        _preFxBitmap?.Dispose();
        _processedBitmap?.Dispose();
        _displayedBitmap?.Dispose();
        _rawBitmap = new SKBitmap(318, 318, true);
        _preFxBitmap = new SKBitmap(318, 318, true);
        _processedBitmap = null;
        _displayedBitmap = null;
        _hasImportedImage = false;
        ImportedImagePath = null;
        _imageEnhanced = false;
        _enhanceFailed = false;
        _quantizeGeneration++;
        QuantizedPreviewInvalidated?.Invoke(this, EventArgs.Empty);
        QuantizedPreview?.Dispose();
        QuantizedPreview = null;
        QuantizedPreviewChanged?.Invoke(this, EventArgs.Empty);
        Drawing.ResetProgress();
        ImagePreview.Image = null;
    }

    // Inputs Handles

    private void HandleTextChange(TextChangingEventArgs e)
    {
        var source = (TextBox)e.Source;
        source.Text = _numberRegex.Replace(source.Text, "");
        e.Handled = true;

        if (source.Text.Length < 1) source.Text = "0";
    }

    private void ResizeImage(double width, double height)
    {
        foreach (var layer in LayersContext)
        {
            layer.RawBitmap?.Dispose();
        }
        LayersContext.Clear();
        _layersStack.Clear();

        width = widthLock > 0 ? widthLock : Math.Max(1, width);
        height = heightLock > 0 ? heightLock :  Math.Max(1, height);

        if (widthLock == 0) widthNumber = (int)width;
        if (heightLock == 0) heightNumber = (int)height;
        
        if (GC.GetTotalMemory(false) < _lastMem) GC.RemoveMemoryPressure(_lastMem);
        _lastMem = GC.GetTotalMemory(false);

        if (_processedBitmap == null)
        {
            var resizedBitmap = _rawBitmap.Resize(new SKSizeI((int)width, (int)height), SKFilterQuality.High);
            _preFxBitmap.Dispose();
            _preFxBitmap = resizedBitmap;
            _displayedBitmap?.Dispose();
            _displayedBitmap = resizedBitmap.ConvertToAvaloniaBitmap();
            ImagePreview.Image = _displayedBitmap;
            GC.AddMemoryPressure(resizedBitmap.ByteCount);
        }
        else if (_processedBitmap != null)
        {
            var resizedBitmap = _rawBitmap.Resize(new SKSizeI((int)width, (int)height), SKFilterQuality.High);
            _preFxBitmap.Dispose();
            _preFxBitmap = resizedBitmap;
            var postProcessBitmap = ImageProcessing.Process(resizedBitmap, GetSelectFilters());
            _processedBitmap.Dispose();
            _processedBitmap = postProcessBitmap;
            _displayedBitmap?.Dispose();
            _displayedBitmap = postProcessBitmap.ConvertToAvaloniaBitmap();
            ImagePreview.Image = _displayedBitmap;
            GC.AddMemoryPressure(resizedBitmap.ByteCount);
        }
    }

    private void SizeSliderOnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_inChange) return;
        if (DateTime.Now.ToFileTime() - _lastTime < 333_333) return;
        _lastTime = DateTime.Now.ToFileTime();

        ResizeImage(_rawBitmap.Width * SizeSlider.Value / 100, _rawBitmap.Height * SizeSlider.Value / 100);

        _inChange = true;
        PercentageNumber.Text = $"{Math.Round(SizeSlider.Value)}%";
        WidthInput.Text = _displayedBitmap.Size.Width.ToString();
        HeightInput.Text = _displayedBitmap.Size.Height.ToString();
        _inChange = false;
    }

    private void PercentageNumberOnTextChanged(object? sender, TextChangingEventArgs e)
    {
        if (PercentageNumber.Text == null) return;
        if (_inChange) return;
        var numberText = _numberRegex.Replace(PercentageNumber.Text, "");
        PercentageNumber.Text = numberText + "%";
        e.Handled = true;

        if (numberText.Length < 1) return;
        var setNumber = int.Parse(numberText);
        if (setNumber < 1) return;
        if (setNumber > 500)
        {
            PercentageNumber.Text = "500%";
            return;
        }

        ResizeImage(_rawBitmap.Width * setNumber / 100, _rawBitmap.Height * setNumber / 100);

        _inChange = true;
        WidthInput.Text = _displayedBitmap.Size.Width.ToString();
        HeightInput.Text = _displayedBitmap.Size.Height.ToString();
        _inChange = false;
    }

    private void HeightInputOnTextChanged(object? sender, TextChangingEventArgs e)
    {
        if (HeightInput.Text == null) return;
        if (_inChange) return;
        var numberText = _numberRegex.Replace(HeightInput.Text, "");
        _inChange = true;
        HeightInput.Text = numberText;
        _inChange = false;
        e.Handled = true;

        if (numberText.Length < 1) return;
        _inChange = true;
        double ratio = (double)_rawBitmap.Width / _rawBitmap.Height; // STUPID STUPID STUPID!!!

        int _heightNumber =  int.Parse(_numberRegex.Replace(HeightInput.Text, ""));
        int _widthNumber = (bool)UnlockAspectRatioCheckBox.IsChecked! ? int.Parse(WidthInput.Text) : (int)(_heightNumber * ratio);

        if(_widthNumber > 4096)
        {
            _heightNumber = (int)(4096 / ratio);
            _widthNumber = 4096;
        }
        
        _widthNumber = Math.Max(Math.Min(_widthNumber, 4096), 1);
        _heightNumber = Math.Max(Math.Min(_heightNumber, 4096), 1);
        
        if (UnlockAspectRatioCheckBox.IsChecked ?? false) ResizeImage(int.Parse(WidthInput.Text), _heightNumber);
        else ResizeImage(_widthNumber, _heightNumber);

        PercentageNumber.Text = $"{Math.Round((decimal)_heightNumber / _rawBitmap.Height * 100)}%";
        WidthInput.Text = _widthNumber.ToString();
        HeightInput.Text = _heightNumber.ToString();
        _inChange = false;
    }

    private void WidthInputOnTextChanged(object? sender, TextChangingEventArgs e)
    {
        if (WidthInput.Text == null) return;
        if (_inChange) return;
        var numberText = _numberRegex.Replace(WidthInput.Text, "");
        _inChange = true;
        WidthInput.Text = numberText;
        _inChange = false;
        e.Handled = true;

        if (numberText.Length < 1) return;
        _inChange = true;
        double ratio = (double)_rawBitmap.Height / _rawBitmap.Width; // STUPID STUPID STUPID!!!

        int _widthNumber = int.Parse(_numberRegex.Replace(WidthInput.Text, ""));
        int _heightNumber = (bool)UnlockAspectRatioCheckBox.IsChecked! ? int.Parse(HeightInput.Text) : (int)(_widthNumber * ratio);
        Utils.Log(_heightNumber);
        Utils.Log(ratio);

        if(_heightNumber > 4096)
        {
            _widthNumber = (int)(4096 / ratio);
            _heightNumber = 4096;
        }
        
        _widthNumber = Math.Max(Math.Min(_widthNumber, 4096), 1);
        _heightNumber = Math.Max(Math.Min(_heightNumber, 4096), 1);

        if (UnlockAspectRatioCheckBox.IsChecked ?? false) ResizeImage(_widthNumber, int.Parse(HeightInput.Text));
        else ResizeImage(_widthNumber, _heightNumber);

        PercentageNumber.Text = $"{Math.Round((decimal)_widthNumber / _rawBitmap.Width * 100)}%";
        WidthInput.Text = _widthNumber.ToString();
        HeightInput.Text = _heightNumber.ToString();
        _inChange = false;
    }

    private void HeightLockOnClick(object? sender, RoutedEventArgs e)
    {
        heightLock = heightLock > 0 ? 0 : heightNumber;
        HeightLockImage.Classes.Clear();
        HeightLockImage.Classes.Add(heightLock > 0 ? "LockedIcon" : "UnlockedIcon");
    }

    private void WidthLockOnClick(object? sender, RoutedEventArgs e)
    {
        widthLock = widthLock > 0 ? 0 : widthNumber;
        WidthLockImage.Classes.Clear();
        WidthLockImage.Classes.Add(widthLock > 0 ? "LockedIcon" : "UnlockedIcon");
    }

    private void DrawIntervalOnTextChanging(object? sender, TextChangingEventArgs e)
    {
        HandleTextChange(e);
        Drawing.Interval = int.TryParse(DrawIntervalElement.Text, out var interval) ? interval : 10000;
    }

    private void ClickDelayOnTextChanging(object? sender, TextChangingEventArgs e)
    {
        HandleTextChange(e);
        Drawing.ClickDelay = int.TryParse(ClickDelayElement.Text, out var clickDelay) ? clickDelay : 1000;
    }

    private void minBlackThresholdElementOnTextChanging(object? sender, TextChangingEventArgs e)
    {
        HandleTextChange(e);
        _minBlackThreshold = int.TryParse(minBlackThresholdElement.Text, out var black) ? black : 127;
    }

    private void maxBlackThresholdElementOnTextChanging(object? sender, TextChangingEventArgs e)
    {
        HandleTextChange(e);
        _maxBlackThreshold = int.TryParse(maxBlackThresholdElement.Text, out var black) ? black : 127;
    }

    private void AlphaThresholdOnTextChanging(object? sender, TextChangingEventArgs e)
    {
        HandleTextChange(e);
        _alphaThresh = int.TryParse(AlphaThresholdElement.Text, out var alpha) ? alpha : 127;
    }

    private void FreeDrawCheckboxOnClick(object? sender, RoutedEventArgs e)
    {
        Drawing.FreeDraw2 = FreeDrawCheckbox.IsChecked ?? false;
    }

    // Toolbar Handles

    private void MinimizeAppOnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    public async void PasteControl()
    {
        var clipboard = Clipboard;
        async void writeDump()
        {
            string dump = JsonConvert.SerializeObject(await clipboard.GetFormatsAsync(), Formatting.Indented);
            Utils.Log(dump);
            dump = JsonConvert.SerializeObject(await clipboard.GetTextAsync(), Formatting.Indented);
            Utils.Log(dump);
            dump = JsonConvert.SerializeObject(await clipboard.GetDataAsync(DataFormats.FileNames), Formatting.Indented);
            Utils.Log(dump);
            dump = JsonConvert.SerializeObject(await clipboard.GetDataAsync(DataFormats.Text), Formatting.Indented);
            Utils.Log(dump);
        }
        try
        {
            var file = await clipboard.GetDataAsync(DataFormats.Files) as IEnumerable<IStorageItem>;
            var img = await clipboard.GetDataAsync("PNG");
            string d = JsonConvert.SerializeObject(await clipboard.GetFormatsAsync(), Formatting.Indented);
            Utils.Log(d);
            if (file is not null) {ImportImage(file.First().Path.LocalPath);}
            else if (img is not null) {ImportImage("",(byte[]?)img);}
            else
            {
                new MessageBox().ShowMessageBox("Error!", "Invalid Image to Paste!", "error");
                Utils.Log("Error with PasteControl(): No image found in clipboard! Dumping clipboard.");
                writeDump();
            }
            
        }
        catch (Exception ex)
        {
            new MessageBox().ShowMessageBox("Error!", "Invalid Image to Paste!", "error");
            Utils.Log("Error with PasteControl(): " + ex);
            writeDump();
        }
    }

    public async void SetConfigFolderViaDialog(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folder.Count != 1) return;
        Config.SetEntry("ConfigFolder", folder[0].TryGetLocalPath());
        RefreshConfigList(this, null);
    }

    public void LoadConfig(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            if (!DdConfigService.TryLoad(path, out var ddConfig, out var error) || !DdConfigService.TryApply(ddConfig!, out error))
                new MessageBox().ShowMessageBox("Invalid dd.json", error, "error");
            else { SelectedConfigLabel.Content = $"dd.json - {Path.GetFileName(path)}"; ApplyDdConfigToUi(); }
            return;
        }
        // TODO: use the warning box (Not implemented yet) system to make it return a "This config does not exist!"
        if (!path.EndsWith(".drawcfg")) return;
        var lines = File.ReadAllLines(path);
        SelectedConfigLabel.Content =
            $"{Properties.Resources.ConfigSelected} - {Path.GetFileNameWithoutExtension(path)}";

        DrawIntervalElement.Text = lines.Length > 0 ? lines[0] : "10000";

        ClickDelayElement.Text = lines.Length > 1 ? lines[1] : "1000";

        //maxBlackThresholdElement.Text = lines.Length > 2 ? lines[2] : "127";
        //AlphaThresholdElement.Text = lines.Length > 3 ? lines[3] : "200";
        // Silly!!

        if (lines.Length <= 4) return;
        if (!bool.TryParse(lines[4], out var _fd2)) return;
        FreeDrawCheckbox.IsChecked = _fd2;
        Drawing.FreeDraw2 = _fd2;

        if (lines.Length <= 5) return;
        //if (!int.TryParse(lines[5], out var _path)) return;

        //minBlackThresholdElement.Text = lines.Length > 6 ? lines[6] : "0";
    }

    public async void SaveConfigViaDialog(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Config",
            FileTypeChoices = new[] { ConfigsFileFilter, DdConfigFileFilter }
        });

        if (file is not null)
        {
            if (Path.GetExtension(file.TryGetLocalPath()).Equals(".json", StringComparison.OrdinalIgnoreCase)) { DdConfigService.Export(file.TryGetLocalPath()); return; }
            await using var stream = await file.OpenWriteAsync();
            await using var streamWriter = new StreamWriter(stream);

            string?[] values =
            {
                DrawIntervalElement.Text,
                ClickDelayElement.Text,
                maxBlackThresholdElement.Text,
                AlphaThresholdElement.Text,
                FreeDrawCheckbox.IsChecked.ToString(),
                "", //We should've used Json for future compatibility and freedom to change and remove config variables @gz9.
                minBlackThresholdElement.Text
            };

            await streamWriter.WriteAsync(string.Join("\r\n", values));
        }
    }

    public async void LoadConfigViaDialog(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Config",
            FileTypeFilter = new[] { ConfigsFileFilter, DdConfigFileFilter },
            AllowMultiple = false
        });

        if (file.Count == 1) LoadConfig(file[0].TryGetLocalPath());
    }

    public void RefreshConfigList(object? sender, RoutedEventArgs? e)
    {
        var configFolder = Config.GetEntry("ConfigFolder");
        if (configFolder == null) return;
        if (!Directory.Exists(configFolder)) return;
        var files = Directory.GetFiles(configFolder, "*.drawcfg");
        var fileNames = files.Select(f => Path.GetFileNameWithoutExtension(f)).ToArray();
        ConfigsListBox.ClearValue(ItemsControl.ItemsSourceProperty);
        ConfigsListBox.Items.Clear();
        ConfigsListBox.ItemsSource = fileNames;
    }

    public void LoadSelectedConfig(object? sender, RoutedEventArgs e)
    {
        if (ConfigsListBox.SelectedItem == null) return;
        var selectedItem = ConfigsListBox.SelectedItem.ToString();
        if (selectedItem == null) return;
        LoadConfig($"{Path.Combine(Config.GetEntry("ConfigFolder"), selectedItem)}.drawcfg");
    }

    private void ApplyDdConfigToUi()
    {
        var config = DdConfigService.Current;
        _inChange = true;
        DrawIntervalElement.Text = config.Interval.ToString(CultureInfo.InvariantCulture);
        ClickDelayElement.Text = Math.Round(config.ClickDelay * 1000).ToString(CultureInfo.InvariantCulture);
        AlgorithmSelection.SelectedIndex = AlgorithmIndexFromName(config.Algorithm);
        PaletteSizeInput.Text = config.KClusters.ToString(CultureInfo.InvariantCulture);
        _inChange = false;
        if (config.LockShape) { widthLock = config.CustomWidth; heightLock = config.CustomHeight; WidthInput.Text = config.CustomWidth.ToString(); HeightInput.Text = config.CustomHeight.ToString(); }
        if (config.CapturedPalette.Count > 0) HexColorsInput.Text = string.Join(", ", config.CapturedPalette);
        Drawing.LastPos = new Vector2(config.DrawRegion.Left, config.DrawRegion.Top);
    }

    /// <summary>Maps a persisted algorithm name onto the AlgorithmSelection ComboBox index.
    /// Display labels differ from config names ("Edge-Following" vs "Edge Traversal"), so the
    /// config name is the single source of truth here.</summary>
    private static int AlgorithmIndexFromName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "edge traversal" or "edge-following" => 1,
        "concentric" => 2,
        _ => 0
    };

    /// <summary>Writes the algorithm choice back into the shared config so it persists and
    /// round-trips through exported dd.json files.</summary>
    private void AlgorithmSelectionOnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_inChange || Design.IsDesignMode) return;
        var name = AlgorithmSelection.SelectedIndex switch
        {
            1 => "Edge Traversal",
            2 => "Concentric",
            _ => "DFS (Default)"
        };
        var config = DdConfigService.Current;
        if (config.Algorithm.Equals(name, StringComparison.OrdinalIgnoreCase)) return;
        config.Algorithm = name;
        if (!DdConfigService.TryApply(config, out var error))
            Utils.Log("Algorithm config write-back rejected: " + error);
    }
    
    // Actions
    ActionPrompt _actionPrompt = new();
    
    public void ClickActionObject(InputAction Action)
    {
        // TODO: add action prompt stuff lol
        Console.WriteLine("Yellow");
    }

    public void AddAction()
    {
        if (!_actionPrompt.IsActive) _actionPrompt = new();
        _actionPrompt.Show();
        _actionPrompt.Callback = _addActionCallback;
        Console.WriteLine("Black");
    }

    public void _addActionCallback()
    {
        Console.WriteLine("Recv Message!");
        if (_actionPrompt.Action is not null) // Empty the compartments of your pantaloons.
        {
            Console.WriteLine("+1 Crime Level");
            Console.WriteLine(_actionPrompt.Action);
            _actionStack.Add(_actionPrompt.Action);
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(UpdateActionsContext);
        }
        // Mothership: "Fuck you." *obliterates your ass*
        _actionPrompt.Close();
    }

    private void UpdateActionsContext()
    {
        if (ActionsContext is null) return;

        // Clear the existing items in ActionsContext
        ActionsContext.Clear();

        // Populate ActionsContext with relevant data from _actionStack
        foreach (var action in _actionStack)
        {
            var _ActionType = "";
            if (action.Action == InputAction.ActionType.KeyDown) _ActionType = "Key Down";
            else if (action.Action == InputAction.ActionType.KeyUp) _ActionType = "Key Up";
            else if (action.Action == InputAction.ActionType.LeftClick) _ActionType = "Left Click";
            else if (action.Action == InputAction.ActionType.RightClick) _ActionType = "Right Click";
            else if (action.Action == InputAction.ActionType.WriteString) _ActionType = "Write String";
            else if (action.Action == InputAction.ActionType.MoveTo) _ActionType = "Move to";
            else if (action.Action == InputAction.ActionType.SetColorHex) _ActionType = "Set color hex";
            
            var _ActionData = action.Data is null ? $" @ x={action.Position.Value.X}, y={action.Position.Value.Y}" : $" - {action.Data}";
            
            var actionDisp = new ActionDisp
            { 
                Text = $"{_ActionType}{_ActionData}",
                boundAction = action,
            };

            ActionsContext.Add(actionDisp);
        }
    }

    public void GenerateLayersButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_preFxBitmap == null || _preFxBitmap.Width <= 1 || _preFxBitmap.Height <= 1)
        {
            new MessageBox().ShowMessageBox("Error!", "Please import an image first.", "error");
            return;
        }

        var matches = Regex.Matches(HexColorsInput.Text ?? "", @"#?([a-fA-F0-9]{6})");
        var targetColors = new List<SKColor>();
        var seenColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            var hex = match.Groups[1].Value.ToUpper();
            if (seenColors.Contains(hex)) continue;
            seenColors.Add(hex);
            
            try
            {
                byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                targetColors.Add(new SKColor(r, g, b));
            }
            catch { }
        }

        if (targetColors.Count == 0)
        {
            new MessageBox().ShowMessageBox("Error!", "Please enter at least one valid hex color.", "error");
            return;
        }

        using var prepared = ImageProcessing.PrepareColorImage(_preFxBitmap, DdConfigService.Current);
        PopulateLayers(ImageProcessing.SplitImageByColors(prepared, targetColors, (byte)_alphaThresh, DdConfigService.Current));
    }

    public void AutoGenerateLayersButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_preFxBitmap == null || _preFxBitmap.Width <= 1 || _preFxBitmap.Height <= 1)
        {
            new MessageBox().ShowMessageBox("Error!", "Please import an image first.", "error");
            return;
        }

        if (!int.TryParse(PaletteSizeInput.Text, out var paletteSize) || paletteSize is < 1 or > 64)
        {
            new MessageBox().ShowMessageBox("Error!", "Palette size must be a number from 1 to 64.", "error");
            return;
        }

        var config = DdConfigService.Current;
        using var prepared = ImageProcessing.PrepareColorImage(_preFxBitmap, config);
        var targetColors = config.CapturedPalette.Count > 0
            ? config.CapturedPalette.Select(SKColor.Parse).ToList()
            : ImageProcessing.ExtractPalette(prepared, paletteSize, (byte)_alphaThresh, config);
        if (targetColors.Count == 0)
        {
            new MessageBox().ShowMessageBox("Warning", "No opaque pixels were found in the image.", "warning");
            return;
        }

        PopulateLayers(ImageProcessing.SplitImageByColors(prepared, targetColors, (byte)_alphaThresh, config));
    }

    private void PopulateLayers(List<ColorLayerResult> splitResults, bool showWarnings = true)
    {
        foreach (var layer in LayersContext)
        {
            layer.RawBitmap?.Dispose();
        }
        LayersContext.Clear();
        _layersStack.Clear();

        foreach (var res in splitResults)
        {
            if (res.PixelCount == 0)
            {
                // Discard empty layers
                res.Bitmap.Dispose();
                continue;
            }

            var hexStr = $"#{res.Color.Red:X2}{res.Color.Green:X2}{res.Color.Blue:X2}";
            var color = Color.FromRgb(res.Color.Red, res.Color.Green, res.Color.Blue);
            var avaloniaBrush = new SolidColorBrush(color);
            var thumbnail = res.Bitmap.ConvertToAvaloniaBitmap();

            LayersContext.Add(new LayerDisp
            {
                HexColor = hexStr,
                HexColorBrush = avaloniaBrush,
                Thumbnail = thumbnail,
                RawBitmap = res.Bitmap,
                Color = res.Color,
                PixelCount = res.PixelCount
            });
        }

        if (LayersContext.Count == 0 && showWarnings)
        {
            new MessageBox().ShowMessageBox("Warning", "No pixels matched the specified hex colors.", "warning");
        }
    }

    // ------------------------------------------------------------------
    // HexedDrawer integration API used by HexedDrawWindow and global hotkeys.
    // ------------------------------------------------------------------

    private void HexedDrawMenu_OnClick(object? sender, RoutedEventArgs e) => OpenHexedDrawWindow();

    public void OpenHexedDrawWindow()
    {        if (_hexedDrawWindow is { IsVisible: true })
        {
            _hexedDrawWindow.Activate();
            return;
        }
        _hexedDrawWindow = new HexedDrawWindow();
        _hexedDrawWindow.Closed += (_, _) => _hexedDrawWindow = null;
        _hexedDrawWindow.Show(this);
    }

    private void HexedStatus(string key, params object?[] args)
    {
        var text = args.Length == 0 ? LocalizationManager.T(key) : LocalizationManager.F(key, args);
        Utils.Log(text);
        Dispatcher.UIThread.Post(() => _hexedDrawWindow?.SetStatus(text));
    }

    public async Task SelectHexedDrawImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationManager.T("dialog_select_image"),
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        });
        if (files.Count != 1) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;
        ImportImage(path);
        HexedStatus("status_loaded_image", Path.GetFileName(path));
    }

    public async Task SelectDrawAreaAsync()
    {
        try
        {
            var rect = await AreaSelector.SelectAsync(this);
            if (rect is null)
            {
                HexedStatus("status_area_selection_cancel");
                return;
            }
            var config = DdConfigService.Current;
            config.DrawRegion = rect.Value;
            config.AreaSelected = true;
            if (!DdConfigService.TryApply(config, out var error))
            {
                HexedStatus("status_area_error", error);
                return;
            }
            Drawing.LastPos = new Vector2(rect.Value.Left, rect.Value.Top);
            Drawing.ClipToRegion = true;
            HexedStatus("status_area_selected", rect.Value.Left, rect.Value.Top, rect.Value.Width, rect.Value.Height);
        }
        catch (Exception ex)
        {
            HexedStatus("status_area_selection_error", ex.Message);
        }
    }

    public async Task QuantizeForHexedDrawAsync()
    {
        if (!HasImportedImage)
        {
            HexedStatus("status_error_no_area_image");
            return;
        }
        if (Drawing.IsDrawing)
        {
            HexedStatus("status_error_drawing_active");
            return;
        }
        if (_quantizing) return;
        _quantizing = true;
        var generation = ++_quantizeGeneration;
        try
        {
            var config = DdConfigService.Current;
            HexedStatus("status_processing_image");

            if (config.EnhanceImage && !_imageEnhanced && !_enhanceFailed)
            {
                HexedStatus("status_enhancing_image");
                try
                {
                    var enhanced = await ImageEnhancer.EnhanceAsync(_preFxBitmap!);
                    if (generation != _quantizeGeneration)
                    {
                        enhanced.Dispose();
                        return;
                    }
                    _preFxBitmap?.Dispose();
                    _preFxBitmap = enhanced;
                    _displayedBitmap?.Dispose();
                    _displayedBitmap = enhanced.ConvertToAvaloniaBitmap();
                    ImagePreview.Image = _displayedBitmap;
                    _imageEnhanced = true;
                }
                catch (Exception ex)
                {
                    _enhanceFailed = true;
                    Utils.Log("Image enhancement failed: " + ex.Message);
                    HexedStatus("status_esrgan_fail");
                }
            }

            var paletteSize = Math.Clamp(config.KClusters, 1, 256);
            List<ColorLayerResult>? layers = null;
            SKBitmap? composite = null;
            using var source = _preFxBitmap!.Copy();
            await Task.Run(() =>
            {
                using var prepared = ImageProcessing.PrepareColorImage(source, config);
                var colors = config.CapturedPalette.Count > 0
                    ? config.CapturedPalette.Select(SKColor.Parse).ToList()
                    : ImageProcessing.ExtractPalette(prepared, paletteSize, (byte)_alphaThresh, config);
                if (colors.Count == 0) throw new InvalidOperationException(LocalizationManager.T("status_image_transparent"));

                if (config.Mode.Equals("bw", StringComparison.OrdinalIgnoreCase))
                {
                    using var gray = ImageProcessing.ToGrayscale(prepared);
                    colors = colors.Select(color =>
                    {
                        var luma = (byte)Math.Clamp((int)Math.Round(color.Red * .2126 + color.Green * .7152 + color.Blue * .0722), 0, 255);
                        return new SKColor(luma, luma, luma);
                    }).Distinct().ToList();
                    layers = ImageProcessing.SplitImageByColors(gray, colors, (byte)_alphaThresh, config);
                    composite = ImageProcessing.BuildComposite(layers, gray.Width, gray.Height);
                }
                else
                {
                    layers = ImageProcessing.SplitImageByColors(prepared, colors, (byte)_alphaThresh, config);
                    composite = ImageProcessing.BuildComposite(layers, prepared.Width, prepared.Height);
                }
            });

            if (generation != _quantizeGeneration)
            {
                if (layers is not null) foreach (var layer in layers) layer.Bitmap.Dispose();
                composite?.Dispose();
                return;
            }

            if (layers is null || composite is null) return;
            PopulateLayers(layers, showWarnings: false);
            if (LayersContext.Count == 0)
            {
                composite.Dispose();
                HexedStatus("status_image_transparent");
                return;
            }
            QuantizedPreviewInvalidated?.Invoke(this, EventArgs.Empty);
            QuantizedPreview?.Dispose();
            QuantizedPreview = composite.ConvertToAvaloniaBitmap();
            composite.Dispose();
            QuantizedPreviewChanged?.Invoke(this, EventArgs.Empty);
            HexedStatus("status_image_processed");
        }
        catch (Exception ex)
        {
            Utils.Log("Quantize failed: " + ex);
            HexedStatus("status_processing_error", ex.Message);
        }
        finally
        {
            _quantizing = false;
        }
    }

    public async Task StartPauseHexedDrawAsync()
    {
        if (_hexedDrawRunning)
        {
            if (Drawing.IsDrawing)
            {
                Drawing.IsPaused = !Drawing.IsPaused;
                HexedStatus(Drawing.IsPaused ? "status_paused" : "status_drawing");
            }
            return;
        }
        if (Drawing.IsDrawing)
        {
            Drawing.IsPaused = !Drawing.IsPaused;
            HexedStatus(Drawing.IsPaused ? "status_paused" : "status_drawing");
            return;
        }
        if (!HasImportedImage)
        {
            HexedStatus("status_error_no_area_image");
            return;
        }
        if (!DdConfigService.Current.AreaSelected)
        {
            HexedStatus("status_select_area_f1");
            return;
        }

        if (LayersContext.Count == 0) await QuantizeForHexedDrawAsync();
        if (LayersContext.Count == 0) return;

        _layersStack.Clear();
        foreach (var layer in LayersContext)
        {
            if (!layer.Enabled || layer.RawBitmap is null) continue;
            _layersStack.Add(new ColorLayerResult
            {
                Bitmap = layer.RawBitmap,
                Color = layer.Color,
                PixelCount = layer.PixelCount
            });
        }
        if (_layersStack.Count == 0) return;

        var config = DdConfigService.Current;
        var region = config.DrawRegion;
        var origin = new Vector2(region.Left, region.Top);
        Drawing.LastPos = origin;
        Drawing.ClipToRegion = true;
        Drawing.ChosenAlgorithm = (byte)AlgorithmIndexFromName(config.Algorithm);
        var hexedWasVisible = _hexedDrawWindow is { IsVisible: true };
        var showPopup = Drawing.ShowPopup;
        Drawing.ShowPopup = false;
        _hexedDrawRunning = true;
        if (hexedWasVisible) _hexedDrawWindow!.WindowState = WindowState.Minimized;
        WindowState = WindowState.Minimized;
        HexedStatus("status_drawing");
        try
        {
            var layers = _layersStack.ToList();
            var actions = _actionStack.ToList();
            await Task.Run(() => Drawing.DrawStack(layers, actions, origin));
        }
        catch (Exception ex)
        {
            Utils.Log("Draw failed: " + ex);
        }
        finally
        {
            Drawing.ShowPopup = showPopup;
            _hexedDrawRunning = false;
            Dispatcher.UIThread.Post(() =>
            {
                WindowState = WindowState.Normal;
                Activate();
                if (hexedWasVisible && _hexedDrawWindow is { } window)
                {
                    window.WindowState = WindowState.Normal;
                    window.Activate();
                }
                HexedStatus(Drawing.ProgressPercent >= 99.5 ? "status_drawing_complete" : "status_drawing_stopped");
                _hexedDrawWindow?.RefreshFromConfig();
            });
        }
    }

    public void ResetHexedDraw()
    {
        Drawing.Halt();
        Drawing.ResetProgress();
        Drawing.ClipToRegion = false;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        HexedStatus("status_reset_progress");
    }

    public void StopHexedDraw()
    {
        Drawing.Halt();
        Drawing.IsPaused = false;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        HexedStatus("status_drawing_stopped");
    }

    public void ClearLayersButton_OnClick(object? sender, RoutedEventArgs e)
    {
        foreach (var layer in LayersContext)
        {
            layer.RawBitmap?.Dispose();
        }
        LayersContext.Clear();
        _layersStack.Clear();
    }
}
