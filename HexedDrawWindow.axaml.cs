using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Autodraw.Configuration;

namespace Autodraw;

/// <summary>HexedDrawer feature port: a dedicated window with the Main / Image Finder /
/// Settings / Config / Language tabs. It drives the shared image-processing and drawing
/// services owned by <see cref="MainWindow"/> so both workflows use the same layers and config.</summary>
public partial class HexedDrawWindow : Window
{
    private static readonly string[] QuantizeStyles = { "Pixelated (Classic)", "Vectorized (Smooth)" };
    private static readonly string[] QuantizeMethods = { "MiniBatchKMeans (Fast)", "KMeans++ (Classic)", "Median Cut", "Div", "NeuQuant Neural-Net" };
    private static readonly string[] ColorSpaceValues = { "RGB", "OKLAB", "CIELAB" };
    private static readonly string[] Algorithms = { "DFS (Default)", "Edge Traversal", "Concentric" };
    private static readonly string[] LayerOrders = { "Brightness to darkness", "Largest area first", "Darkness to brightness" };
    private static readonly string[] StrayModes = { "Disabled", "Smart Merge (V5)" };
    private static readonly string[] DrawerModules = { "Old (No Bridge)", "New (A* Bridge)" };

    private readonly ObservableCollection<ImageSearchResult> _searchResults = new();
    private readonly DispatcherTimer _autoQuantizeTimer;
    private readonly DispatcherTimer _uiTimer;
    private List<string> _configPaths = new();
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private bool _updating;
    private bool _calibrationRestorePending;

    public HexedDrawWindow()
    {
        InitializeComponent();
        SearchResultsList.ItemsSource = _searchResults;

        _autoQuantizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _autoQuantizeTimer.Tick += async (_, _) =>
        {
            _autoQuantizeTimer.Stop();
            if (_updating) return;
            var main = MainWindow.CurrentMainWindow;
            if (main is null || !main.HasImportedImage || !DdConfigService.Current.AutoQuantize) return;
            await main.QuantizeForHexedDrawAsync();
        };

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) => RefreshLiveState();
        _uiTimer.Start();

        DdConfigService.Changed += OnConfigChanged;
        Drawing.ProgressChanged += OnProgressChanged;
        if (MainWindow.CurrentMainWindow is { } mainWindow)
        {
            mainWindow.QuantizedPreviewChanged += OnQuantizedPreviewChanged;
            mainWindow.QuantizedPreviewInvalidated += OnQuantizedPreviewInvalidated;
        }

        Closing += (_, _) =>
        {
            DdConfigService.Changed -= OnConfigChanged;
            Drawing.ProgressChanged -= OnProgressChanged;
            if (MainWindow.CurrentMainWindow is { } window)
            {
                window.QuantizedPreviewChanged -= OnQuantizedPreviewChanged;
                window.QuantizedPreviewInvalidated -= OnQuantizedPreviewInvalidated;
            }
            _searchCts?.Cancel();
            _autoQuantizeTimer.Stop();
            _uiTimer.Stop();
        };

        LocalizationManager.Load(LocalizationManager.CurrentCode);
        ApplyLanguage();
        RefreshFromConfig();
        RefreshConfigList();
        SetStatus(LocalizationManager.T("status_ready"));
    }

    // ---------------------------------------------------------------- status

    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    private void OnConfigChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => { if (!_updating) RefreshFromConfig(); });

    private void OnProgressChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RefreshLiveState);

    private void OnQuantizedPreviewChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RefreshPreview);

    private void OnQuantizedPreviewInvalidated(object? sender, EventArgs e)
    {
        void Clear()
        {
            QuantizedPreviewImage.Source = null;
            PreviewHintText.IsVisible = true;
        }

        if (Dispatcher.UIThread.CheckAccess()) Clear();
        else Dispatcher.UIThread.Post(Clear);
    }

    private void RefreshLiveState()
    {
        if (_calibrationRestorePending && MainWindow.CurrentMainWindow is { IsCapturingColorPicker: false })
        {
            _calibrationRestorePending = false;
            WindowState = WindowState.Normal;
            Activate();
            RefreshFromConfig();
        }

        var main = MainWindow.CurrentMainWindow;
        DrawButton.Content = Drawing.IsDrawing
            ? (Drawing.IsPaused ? LocalizationManager.T("btn_draw") + " ▶" : LocalizationManager.T("btn_draw") + " ⏸")
            : LocalizationManager.T("btn_draw");
        DrawProgressBar.Value = Drawing.ProgressPercent;
        PercentText.Text = LocalizationManager.F("label_progress", Drawing.ProgressPercent.ToString("0.0", CultureInfo.CurrentCulture));
        PixelsText.Text = LocalizationManager.F("label_pixels_drawn", Drawing.ProgressDrawn, Drawing.ProgressTotal);
        if (main is not null && !_updating)
        {
            ImageInfoText.Text = main.HasImportedImage && !string.IsNullOrEmpty(main.ImportedImagePath)
                ? LocalizationManager.F("label_image_path", Path.GetFileName(main.ImportedImagePath))
                : LocalizationManager.T("label_no_image");
            var region = DdConfigService.Current.DrawRegion;
            AreaInfoText.Text = DdConfigService.Current.AreaSelected
                ? LocalizationManager.F("status_area_selected", region.Left, region.Top, region.Width, region.Height)
                : LocalizationManager.T("status_select_area_f1");
        }
    }

    private void RefreshPreview()
    {
        var preview = MainWindow.CurrentMainWindow?.QuantizedPreview;
        QuantizedPreviewImage.Source = preview;
        PreviewHintText.IsVisible = preview is null;
        if (preview is not null) PreviewHintText.Text = LocalizationManager.T("label_preview_area");
    }

    // ---------------------------------------------------------------- main tab

    private async void SelectImage_OnClick(object? sender, RoutedEventArgs e)
    {
        var main = MainWindow.CurrentMainWindow;
        if (main is null) return;
        await main.SelectHexedDrawImageAsync();
        RefreshLiveState();
        ScheduleAutoQuantize();
    }

    private async void SelectArea_OnClick(object? sender, RoutedEventArgs e)
    {
        var main = MainWindow.CurrentMainWindow;
        if (main is null) return;
        await main.SelectDrawAreaAsync();
        RefreshFromConfig();
        RefreshLiveState();
    }

    private async void Quantize_OnClick(object? sender, RoutedEventArgs e)
    {
        var main = MainWindow.CurrentMainWindow;
        if (main is null) return;
        await main.QuantizeForHexedDrawAsync();
        RefreshPreview();
    }

    private async void Draw_OnClick(object? sender, RoutedEventArgs e)
    {
        var main = MainWindow.CurrentMainWindow;
        if (main is null) return;
        await main.StartPauseHexedDrawAsync();
        RefreshLiveState();
    }

    private void Reset_OnClick(object? sender, RoutedEventArgs e)
    {
        MainWindow.CurrentMainWindow?.ResetHexedDraw();
        SetStatus(LocalizationManager.T("status_reset_progress"));
        RefreshLiveState();
    }

    private void Stop_OnClick(object? sender, RoutedEventArgs e)
    {
        var main = MainWindow.CurrentMainWindow;
        main?.StopHexedDraw();
        SetStatus(LocalizationManager.T("status_drawing_stopped"));
        Close();
    }

    private void Parameter_OnChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        var config = DdConfigService.Current;
        if (ReferenceEquals(sender, KClustersInput))
        {
            if (!int.TryParse(KClustersInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var clusters)) return;
            config.KClusters = Math.Clamp(clusters, 1, 256);
        }
        else if (ReferenceEquals(sender, BrushSizeInput))
        {
            if (!int.TryParse(BrushSizeInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var brush)) return;
            config.BrushSize = Math.Clamp(brush, 1, 64);
        }
        else if (ReferenceEquals(sender, DrawDelayInput))
        {
            if (!double.TryParse(DrawDelayInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return;
            config.DrawDelaySeconds = Math.Max(0, seconds);
            config.Interval = (long)Math.Clamp(seconds * 10_000_000d, 0, long.MaxValue);
        }
        else
        {
            return;
        }
        ApplyConfig(config);
        ScheduleAutoQuantize();
    }

    // ---------------------------------------------------------------- settings tab

    private void PickerStyle_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var box = BoxPickerRadio.IsChecked == true;
        var config = DdConfigService.Current;
        config.ColorPickingMethod = box ? "Box" : "Circle";
        config.PickerType = box ? "Box" : "Wheel";
        ApplyConfig(config);
        SetStatus(LocalizationManager.F("status_picker_set", box ? LocalizationManager.T("picker_style_box") : LocalizationManager.T("picker_style_circle")));
    }

    private void Mode_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var bw = BwModeRadio.IsChecked == true;
        var config = DdConfigService.Current;
        config.Mode = bw ? "bw" : "color";
        ApplyConfig(config);
        SetStatus(LocalizationManager.F("status_mode_changed", LocalizationManager.T(bw ? "mode_bw" : "mode_color")));
        ScheduleAutoQuantize();
    }

    private void AutoQuantize_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var config = DdConfigService.Current;
        config.AutoQuantize = AutoQuantizeCheck.IsChecked == true;
        ApplyConfig(config);
        SetStatus(LocalizationManager.T(config.AutoQuantize ? "status_autoq_enabled" : "status_autoq_disabled"));
        if (config.AutoQuantize) ScheduleAutoQuantize();
    }

    private void EnhanceImage_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var config = DdConfigService.Current;
        config.EnhanceImage = EnhanceImageCheck.IsChecked == true;
        ApplyConfig(config);
        SetStatus(LocalizationManager.T(config.EnhanceImage ? "status_enhance_enabled" : "status_enhance_disabled"));
        ScheduleAutoQuantize();
    }

    private void QuantizeStyle_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var value = QuantizeStyleCombo.SelectedIndex == 1 ? QuantizeStyles[1] : QuantizeStyles[0];
        var config = DdConfigService.Current;
        config.QuantizeStyle = value;
        ApplyConfig(config);
        SetStatus(LocalizationManager.F("status_qstyle_changed", LocalizationManager.T(value.Contains("vector") ? "qstyle_vector" : "qstyle_pixel")));
        ScheduleAutoQuantize();
    }

    private void QuantizeMethod_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var index = Math.Clamp(QuantizeMethodCombo.SelectedIndex, 0, QuantizeMethods.Length - 1);
        var config = DdConfigService.Current;
        config.QuantizeMethod = QuantizeMethods[index];
        config.KmeansAlgorithm = index switch
        {
            0 => "MiniBatchKMeans (Fast)",
            1 => "KMeans++ (Original)",
            _ => config.KmeansAlgorithm
        };
        ApplyConfig(config);
        SetStatus(LocalizationManager.F("status_qmethod_changed", QuantizeMethodCombo.SelectedItem?.ToString() ?? config.QuantizeMethod));
        ScheduleAutoQuantize();
    }

    private void ColorSpace_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var index = Math.Clamp(ColorSpaceCombo.SelectedIndex, 0, ColorSpaceValues.Length - 1);
        var config = DdConfigService.Current;
        config.ColorDistanceMethod = ColorSpaceValues[index];
        config.QuantizeMethod = index == 2 ? "Lab Color Space" : config.QuantizeMethod;
        ApplyConfig(config);
        SetStatus(LocalizationManager.F("status_colorspace_changed", ColorSpaceCombo.SelectedItem?.ToString() ?? config.ColorDistanceMethod));
        ScheduleAutoQuantize();
    }

    private void Algorithm_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var index = Math.Clamp(AlgorithmCombo.SelectedIndex, 0, Algorithms.Length - 1);
        var config = DdConfigService.Current;
        config.Algorithm = Algorithms[index];
        ApplyConfig(config);
    }

    private void DfsMode_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var four = Dfs4Radio.IsChecked == true;
        var config = DdConfigService.Current;
        config.DfsMode = four ? "4-Directional" : "8-Directional";
        ApplyConfig(config);
        SetStatus(LocalizationManager.F("status_dfs_changed", LocalizationManager.T(four ? "dfs_4dir" : "dfs_8dir")));
    }

    private void DrawOrder_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var index = Math.Clamp(DrawOrderCombo.SelectedIndex, 0, LayerOrders.Length - 1);
        var config = DdConfigService.Current;
        config.LayerOrder = LayerOrders[index];
        ApplyConfig(config);
        SetStatus(LocalizationManager.F("status_order_changed", DrawOrderCombo.SelectedItem?.ToString() ?? config.LayerOrder));
    }

    private void StrayRemoval_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var smart = StrayRemovalCombo.SelectedIndex == 1;
        var config = DdConfigService.Current;
        config.RemoveStray = smart;
        config.StrayRemovalMode = smart ? StrayModes[1] : StrayModes[0];
        ApplyConfig(config);
        SetStatus(LocalizationManager.F("status_stray_changed", LocalizationManager.T(smart ? "stray_v5" : "stray_disabled")));
        ScheduleAutoQuantize();
    }

    private void DrawerModule_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var isNew = DrawerModuleCombo.SelectedIndex == 1;
        var config = DdConfigService.Current;
        config.DrawerModule = isNew ? DrawerModules[1] : DrawerModules[0];
        ApplyConfig(config);
        SetStatus(LocalizationManager.F("status_drawer_changed", LocalizationManager.T(isNew ? "drawer_new" : "drawer_old")));
    }

    private void PaletteRotation_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var cw = PaletteRotationCombo.SelectedIndex == 1;
        var config = DdConfigService.Current;
        config.WheelDirection = cw ? "CW" : "CCW";
        ApplyConfig(config);
        SetStatus(LocalizationManager.F("status_rotation_set", LocalizationManager.T(cw ? "rot_cw" : "rot_ccw")));
    }

    private void Calibrate_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        var main = MainWindow.CurrentMainWindow;
        if (main is null) return;
        _calibrationRestorePending = true;
        WindowState = WindowState.Minimized;
        main.BeginColorPickerCalibration(mode);
    }

    // ---------------------------------------------------------------- finder tab

    private string SelectedSafeSearch()
        => SafeSearchCombo.SelectedIndex switch { 1 => "Strict", 2 => "Off", _ => "Moderate" };

    private void SafeSearch_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var config = DdConfigService.Current;
        config.SafeSearch = SelectedSafeSearch().ToLowerInvariant();
        ApplyConfig(config);
    }

    private void SearchQuery_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) FindImages_OnClick(sender, e);
    }

    private async void FindImages_OnClick(object? sender, RoutedEventArgs e)
    {
        var query = SearchQueryInput.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            SetStatus(LocalizationManager.T("status_enter_query"));
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var generation = ++_searchGeneration;
        _searchResults.Clear();
        SearchProgressBar.IsVisible = true;
        FindImagesButton.IsEnabled = false;
        SetStatus(LocalizationManager.F("status_searching", query));
        try
        {
            var results = await ImageFinder.SearchAsync(query, DdConfigService.Current.SearchLimit, SelectedSafeSearch(), token);
            if (token.IsCancellationRequested || generation != _searchGeneration) return;
            if (results.Count == 0)
            {
                SetStatus(LocalizationManager.F("status_no_results", query));
                return;
            }

            // Show text results immediately; thumbnails update through INotifyPropertyChanged.
            foreach (var result in results) _searchResults.Add(result);
            SetStatus(LocalizationManager.F("status_found_images", results.Count));

            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(results.Select(async result =>
            {
                await gate.WaitAsync(token);
                try { await ImageFinder.LoadThumbnailAsync(result, token); }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
                SetStatus(LocalizationManager.F("status_search_failed", ex.Message));
        }
        finally
        {
            if (generation == _searchGeneration)
            {
                SearchProgressBar.IsVisible = false;
                FindImagesButton.IsEnabled = true;
            }
        }
    }

    private async void UseSearchResult_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ImageSearchResult result }) return;
        var main = MainWindow.CurrentMainWindow;
        if (main is null) return;
        SetStatus(LocalizationManager.F("status_loaded_image", string.IsNullOrEmpty(result.Title) ? result.ImageUrl : result.Title));
        try
        {
            var path = await ImageFinder.DownloadAsync(result.ImageUrl, CancellationToken.None);
            main.ImportImage(path);
            ScheduleAutoQuantize();
            RefreshLiveState();
        }
        catch (Exception ex)
        {
            SetStatus(LocalizationManager.F("status_save_load_error", ex.Message));
        }
    }

    private void ClearCache_OnClick(object? sender, RoutedEventArgs e)
    {
        var count = ImageFinder.ClearCache();
        SetStatus(count == 0
            ? LocalizationManager.T("status_cache_empty")
            : LocalizationManager.F("status_cache_cleared", count));
    }

    // ---------------------------------------------------------------- config tab

    private void RefreshConfigList()
    {
        _configPaths = HexedDrawConfigMapper.ListConfigs().ToList();
        ConfigList.ItemsSource = _configPaths.Select(Path.GetFileNameWithoutExtension).ToArray();
        var current = DdConfigService.Current;
        CurrentConfigText.Text = $"{current.KClusters} colors | {current.Mode} | {current.Algorithm}";
    }

    private async void LoadConfig_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationManager.T("dialog_load_config"),
            AllowMultiple = false,
            FileTypeFilter = new[] { MainWindow.DdConfigFileFilter }
        });
        if (files.Count != 1) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;
        if (!HexedDrawConfigMapper.TryImport(path, out var error))
        {
            SetStatus(LocalizationManager.F("status_config_load_error", error));
            return;
        }
        RefreshFromConfig();
        SetStatus(LocalizationManager.F("status_config_loaded", Path.GetFileName(path)));
    }

    private void SaveConfig_OnClick(object? sender, RoutedEventArgs e)
    {
        HexedDrawConfigMapper.EnsureConfigDirectory();
        var path = ConfigList.SelectedIndex >= 0 && ConfigList.SelectedIndex < _configPaths.Count
            ? _configPaths[ConfigList.SelectedIndex]
            : Path.Combine(HexedDrawConfigMapper.ConfigDirectory, "config.json");
        if (!HexedDrawConfigMapper.SaveAutoDraw(path, out var error))
        {
            SetStatus(LocalizationManager.F("status_config_save_error", error));
            return;
        }
        RefreshConfigList();
        SetStatus(LocalizationManager.F("status_config_saved", Path.GetFileName(path)));
    }

    private async void SaveConfigAs_OnClick(object? sender, RoutedEventArgs e)
    {
        HexedDrawConfigMapper.EnsureConfigDirectory();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationManager.T("dialog_save_config"),
            SuggestedFileName = "config.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { MainWindow.DdConfigFileFilter }
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (path is null) return;
        if (!HexedDrawConfigMapper.SaveAutoDraw(path, out var error))
        {
            SetStatus(LocalizationManager.F("status_config_save_error", error));
            return;
        }
        RefreshConfigList();
        SetStatus(LocalizationManager.F("status_config_saved", Path.GetFileName(path)));
    }

    private async void ExportHexedConfig_OnClick(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export HexedDrawer Configuration",
            SuggestedFileName = "SpeedDraw.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { MainWindow.DdConfigFileFilter }
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (path is null) return;
        SetStatus(HexedDrawConfigMapper.SaveHexedDraw(path, out var error)
            ? LocalizationManager.F("status_config_saved", Path.GetFileName(path))
            : LocalizationManager.F("status_config_save_error", error));
    }

    private void ConfigList_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ConfigList.SelectedIndex < 0 || ConfigList.SelectedIndex >= _configPaths.Count) return;
        var path = _configPaths[ConfigList.SelectedIndex];
        if (!HexedDrawConfigMapper.TryImport(path, out var error))
        {
            SetStatus(LocalizationManager.F("status_config_load_error", error));
            return;
        }
        RefreshFromConfig();
        SetStatus(LocalizationManager.F("status_config_loaded", Path.GetFileName(path)));
    }

    // ---------------------------------------------------------------- language tab

    private void ApplyLanguage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is not UiLanguage language) return;
        LocalizationManager.Load(language.Code);
        ApplyLanguage();
        RefreshFromConfig();
        SetStatus(LocalizationManager.F("status_lang_updated", language.DisplayName));
    }

    private void MainWindow_OnClick(object? sender, RoutedEventArgs e)
    {
        if (MainWindow.CurrentMainWindow is not { } main) return;
        main.WindowState = WindowState.Normal;
        main.Activate();
    }

    // ---------------------------------------------------------------- sync / language

    private void ApplyConfig(AutoDrawConfig config)
    {
        if (!DdConfigService.TryApply(config, out var error))
        {
            SetStatus(error);
            RefreshFromConfig();
        }
    }

    private void ScheduleAutoQuantize()
    {
        if (_updating) return;
        if (!DdConfigService.Current.AutoQuantize) return;
        var main = MainWindow.CurrentMainWindow;
        if (main is null || !main.HasImportedImage || Drawing.IsDrawing) return;
        _autoQuantizeTimer.Stop();
        _autoQuantizeTimer.Start();
    }

    private static string Label(string key, string fallback)
    {
        var value = LocalizationManager.T(key);
        return value == key ? fallback : value;
    }

    public void RefreshFromConfig()
    {
        var config = DdConfigService.Current;
        _updating = true;
        KClustersInput.Text = config.KClusters.ToString(CultureInfo.InvariantCulture);
        BrushSizeInput.Text = config.BrushSize.ToString(CultureInfo.InvariantCulture);
        DrawDelayInput.Text = (config.DrawDelaySeconds ?? config.Interval / 10_000_000d).ToString("0.######", CultureInfo.InvariantCulture);

        CirclePickerRadio.IsChecked = !config.ColorPickingMethod.Equals("Box", StringComparison.OrdinalIgnoreCase);
        BoxPickerRadio.IsChecked = config.ColorPickingMethod.Equals("Box", StringComparison.OrdinalIgnoreCase);
        ColorModeRadio.IsChecked = !config.Mode.Equals("bw", StringComparison.OrdinalIgnoreCase);
        BwModeRadio.IsChecked = config.Mode.Equals("bw", StringComparison.OrdinalIgnoreCase);
        AutoQuantizeCheck.IsChecked = config.AutoQuantize;
        EnhanceImageCheck.IsChecked = config.EnhanceImage;
        Dfs8Radio.IsChecked = !config.DfsMode.Contains('4');
        Dfs4Radio.IsChecked = config.DfsMode.Contains('4');

        QuantizeStyleCombo.SelectedIndex = Math.Max(0, Array.IndexOf(QuantizeStyles, config.QuantizeStyle));
        QuantizeMethodCombo.SelectedIndex = Math.Max(0, Array.FindIndex(QuantizeMethods,
            method => method.Equals(config.QuantizeMethod, StringComparison.OrdinalIgnoreCase) ||
                      (method.StartsWith("MiniBatch") && config.KmeansAlgorithm.StartsWith("MiniBatch")) ||
                      (method.StartsWith("KMeans++") && config.KmeansAlgorithm.StartsWith("KMeans++"))));
        ColorSpaceCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ColorSpaceValues,
            config.ColorDistanceMethod?.ToUpperInvariant() ?? "RGB"));
        AlgorithmCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Algorithms, config.Algorithm));
        DrawOrderCombo.SelectedIndex = Math.Max(0, Array.FindIndex(LayerOrders,
            order => order.Equals(config.LayerOrder, StringComparison.OrdinalIgnoreCase)));
        StrayRemovalCombo.SelectedIndex = config.RemoveStray ? 1 : 0;
        DrawerModuleCombo.SelectedIndex = config.DrawerModule.Contains("old", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        PaletteRotationCombo.SelectedIndex = config.WheelDirection.Equals("CW", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        SafeSearchCombo.SelectedIndex = config.SafeSearch?.ToLowerInvariant() switch
        {
            "strict" => 1,
            "off" => 2,
            _ => 0
        };
        _updating = false;
        CurrentConfigText.Text = $"{config.KClusters} colors | {config.Mode} | {config.Algorithm}";
        RefreshPreview();
    }

    private void ApplyLanguage()
    {
        _updating = true;
        AppTitleText.Text = LocalizationManager.T("app_title");
        HotkeyHintText.Text = "F1 Area  |  F2 Start/Pause  |  F3 Stop  |  F4 Reset  |  F6 Quantize";
        MainWindowButton.Content = "AutoDraw";
        TabMain.Header = LocalizationManager.T("tab_main");
        TabFinder.Header = LocalizationManager.T("tab_finder");
        TabSettings.Header = LocalizationManager.T("tab_settings");
        TabConfig.Header = LocalizationManager.T("tab_config");
        TabLanguage.Header = LocalizationManager.T("tab_language");

        ControlHeaderText.Text = LocalizationManager.T("lf_control");
        SelectImageButton.Content = LocalizationManager.T("btn_select_image");
        SelectAreaButton.Content = LocalizationManager.T("btn_select_area");
        QuantizeButton.Content = LocalizationManager.T("btn_quantize");
        DrawButton.Content = LocalizationManager.T("btn_draw");
        ResetButton.Content = LocalizationManager.T("btn_reset");
        StopButton.Content = LocalizationManager.T("btn_stop");
        ParametersHeaderText.Text = LocalizationManager.T("lf_parameters");
        KClustersLabel.Text = LocalizationManager.T("label_k_clusters");
        DrawDelayLabel.Text = LocalizationManager.T("label_draw_delay");
        BrushSizeLabel.Text = LocalizationManager.T("label_brush_size");
        PreviewHeaderText.Text = LocalizationManager.T("lf_preview");
        PreviewHintText.Text = LocalizationManager.T("label_preview_area");

        FindImagesButton.Content = LocalizationManager.T("btn_find");
        ClearCacheButton.Content = LocalizationManager.T("btn_clear_cache");
        SearchQueryInput.Watermark = LocalizationManager.T("label_search");

        ColorPickerHeaderText.Text = LocalizationManager.T("lf_color_picker_style");
        CirclePickerRadio.Content = LocalizationManager.T("picker_style_circle");
        BoxPickerRadio.Content = LocalizationManager.T("picker_style_box");
        CircleCalibrationHeaderText.Text = LocalizationManager.T("lf_calib_circle");
        CalibrateCircleButton.Content = LocalizationManager.T("btn_calib_circle");
        CalibrateBrightnessButton.Content = LocalizationManager.T("btn_calib_slider");
        PaletteRotationLabel.Text = LocalizationManager.T("label_palette_rotation");
        BoxCalibrationHeaderText.Text = LocalizationManager.T("lf_calib_box");
        CalibrateHueButton.Content = LocalizationManager.T("btn_calib_rainbow");
        CalibrateBoxButton.Content = LocalizationManager.T("btn_calib_box");
        ModeHeaderText.Text = LocalizationManager.T("lf_mode");
        ColorModeRadio.Content = LocalizationManager.T("mode_color");
        BwModeRadio.Content = LocalizationManager.T("mode_bw");
        ProcessingHeaderText.Text = LocalizationManager.T("lf_processing");
        AutoQuantizeCheck.Content = LocalizationManager.T("check_auto_quantize");
        EnhanceImageCheck.Content = LocalizationManager.T("check_enhance_image");
        QuantizeStyleLabel.Text = LocalizationManager.T("label_quantize_style");
        QuantizeMethodLabel.Text = LocalizationManager.T("label_quantize_method");
        ColorSpaceLabel.Text = LocalizationManager.T("label_color_space");
        AlgorithmLabel.Text = Label("label_algorithm", "Drawing Algorithm:");
        DrawingAlgorithmHeaderText.Text = LocalizationManager.T("lf_drawing_algo");
        DfsSearchLabel.Text = LocalizationManager.T("label_dfs_search");
        Dfs8Radio.Content = LocalizationManager.T("dfs_8dir");
        Dfs4Radio.Content = LocalizationManager.T("dfs_4dir");
        DrawOrderLabel.Text = LocalizationManager.T("label_draw_order");
        StrayPixelLabel.Text = LocalizationManager.T("label_stray_pixel");
        DrawerModuleLabel.Text = LocalizationManager.T("label_drawer_module");

        ConfigManagementHeaderText.Text = LocalizationManager.T("lf_config_mgmt");
        LoadConfigButton.Content = LocalizationManager.T("btn_load_config");
        SaveConfigButton.Content = LocalizationManager.T("btn_save_config");
        SaveConfigAsButton.Content = LocalizationManager.T("btn_save_config_as");
        ExportHexedConfigButton.Content = Label("btn_export_hexed", "Export HexedDrawer JSON...");
        CurrentConfigLabel.Text = LocalizationManager.T("label_current_config");

        LanguageSelectHeaderText.Text = LocalizationManager.T("lf_language_select");
        SelectLanguageLabel.Text = LocalizationManager.T("label_select_language");
        ApplyLanguageButton.Content = LocalizationManager.T("btn_apply_language");
        LanguageNoteText.Text = LocalizationManager.T("label_lang_change_note");

        QuantizeStyleCombo.ItemsSource = new[] { Label("qstyle_pixel", QuantizeStyles[0]), Label("qstyle_vector", QuantizeStyles[1]) };
        QuantizeMethodCombo.ItemsSource = new[]
        {
            Label("qmethod_minibatch", QuantizeMethods[0]),
            Label("qmethod_kmeans", QuantizeMethods[1]),
            Label("qmethod_median", QuantizeMethods[2]),
            Label("qmethod_div", QuantizeMethods[3]),
            Label("qmethod_neuquant", QuantizeMethods[4])
        };
        ColorSpaceCombo.ItemsSource = new[] { "sRGB", "OKLAB", "CIELAB" };
        AlgorithmCombo.ItemsSource = Algorithms;
        DrawOrderCombo.ItemsSource = new[]
        {
            LocalizationManager.T("order_bright"),
            LocalizationManager.T("order_large"),
            LocalizationManager.T("order_dark")
        };
        StrayRemovalCombo.ItemsSource = new[]
        {
            LocalizationManager.T("stray_disabled"),
            LocalizationManager.T("stray_v5")
        };
        DrawerModuleCombo.ItemsSource = new[]
        {
            LocalizationManager.T("drawer_old"),
            LocalizationManager.T("drawer_new")
        };
        PaletteRotationCombo.ItemsSource = new[]
        {
            LocalizationManager.T("rot_ccw"),
            LocalizationManager.T("rot_cw")
        };
        SafeSearchCombo.ItemsSource = new[]
        {
            LocalizationManager.T("safe_search_moderate"),
            Label("safe_search_strict", "Strict"),
            LocalizationManager.T("safe_search_off")
        };
        LanguageCombo.ItemsSource = LocalizationManager.Languages;
        LanguageCombo.SelectedItem = LocalizationManager.Find(LocalizationManager.CurrentCode);
        if (SafeSearchCombo.SelectedIndex < 0) SafeSearchCombo.SelectedIndex = 0;
        _updating = false;
    }
}
