using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveWall.Controls;
using LiveWall.Models;
using LiveWall.Services;
using Microsoft.Win32;

namespace LiveWall;

public partial class MainWindow : Window
{
    private readonly WallpaperEngine _engine;
    private readonly WallpaperSettings _settings;
    private readonly ObservableCollection<MonitorTarget> _targets = new ObservableCollection<MonitorTarget>();

    private bool _suppressEditorEvents;
    private bool _exitRequested;
    private DateTime _lastDeactivatedLogUtc = DateTime.MinValue;

    public MainWindow(WallpaperEngine engine, WallpaperSettings settings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        MonitorList.ItemsSource = _targets;
        ApplySavedPlacement();
        Loaded += MainWindow_Loaded;
        _engine.StatusChanged += Engine_StatusChanged;
    }

    /// <summary>Set by the application before closing the window for a real exit (otherwise X hides to tray).</summary>
    public bool ExitRequested
    {
        get { return _exitRequested; }
        set { _exitRequested = value; }
    }

    public void RefreshFromEngine()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshFromEngine));
            return;
        }

        UpdateStatuses();
    }

    // ── Startup ──

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _suppressEditorEvents = true;
        try
        {
            SameForAllCheckBox.IsChecked = _settings.SameForAllMonitors;
            PauseMaximizedCheckBox.IsChecked = _settings.PauseWhenMaximized;
            PauseFullscreenCheckBox.IsChecked = _settings.PauseWhenFullscreen;
            StartMinimizedCheckBox.IsChecked = _settings.StartMinimized;
            StartWithWindowsCheckBox.IsChecked = StartupService.IsRegistered();
        }
        finally
        {
            _suppressEditorEvents = false;
        }

        if (_engine.Monitors.Count == 0)
        {
            _engine.RefreshMonitors();
        }

        SyncMonitorTargets();
        LoadEditor();
        UpdateStatuses();
    }

    private void Engine_StatusChanged(object? sender, EventArgs e)
    {
        RefreshFromEngine();
    }

    // ── Monitor list ──

    /// <summary>
    /// Mirrors the engine's monitor list into the bound collection WITHOUT ever clearing it: replacing the
    /// items (or reassigning the ListBox selection) takes the focus away from whatever the user is doing,
    /// which is what closed the drop-downs while it ran from the status refresh.
    /// </summary>
    private void SyncMonitorTargets()
    {
        IReadOnlyList<MonitorDescriptor> monitors = _engine.Monitors;
        bool changed = false;

        for (int index = _targets.Count - 1; index >= 0; index--)
        {
            bool stillPresent = false;
            for (int monitorIndex = 0; monitorIndex < monitors.Count; monitorIndex++)
            {
                if (string.Equals(monitors[monitorIndex].DeviceName, _targets[index].DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    stillPresent = true;
                    break;
                }
            }

            if (!stillPresent)
            {
                _targets.RemoveAt(index);
                changed = true;
            }
        }

        for (int monitorIndex = 0; monitorIndex < monitors.Count; monitorIndex++)
        {
            MonitorDescriptor monitor = monitors[monitorIndex];
            MonitorTarget? existing = null;
            foreach (MonitorTarget target in _targets)
            {
                if (string.Equals(target.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    existing = target;
                    break;
                }
            }

            if (existing == null)
            {
                MonitorTarget created = new MonitorTarget(monitor.DeviceName, monitor.DisplayName);
                _targets.Insert(Math.Min(monitor.Index, _targets.Count), created);
                changed = true;
            }
            else if (!string.Equals(existing.DisplayName, monitor.DisplayName, StringComparison.Ordinal))
            {
                existing.DisplayName = monitor.DisplayName;
            }
        }

        if (changed && MonitorList.SelectedItem == null && _targets.Count > 0)
        {
            _suppressEditorEvents = true;
            try
            {
                MonitorList.SelectedIndex = 0;
            }
            finally
            {
                _suppressEditorEvents = false;
            }
        }
    }

    private void MonitorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEditorEvents)
        {
            return;
        }

        LoadEditor();
    }

    private void SameForAllCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents)
        {
            return;
        }

        _settings.SameForAllMonitors = SameForAllCheckBox.IsChecked == true;
        LoadEditor();
        ApplyEditorChanges();
    }

    // ── Editor <-> model ──

    private WallpaperSource CurrentSource
    {
        get
        {
            if (_settings.SameForAllMonitors)
            {
                return _settings.Shared;
            }

            MonitorTarget? target = MonitorList.SelectedItem as MonitorTarget;
            if (target == null)
            {
                return _settings.Shared;
            }

            return _settings.GetOrCreateMonitorSource(target.DeviceName);
        }
    }

    private void LoadEditor()
    {
        WallpaperSource source = CurrentSource;

        _suppressEditorEvents = true;
        try
        {
            SelectComboItemByTag(SourceKindComboBox, source.Kind.ToString());
            SelectComboItemByTag(StretchComboBox, source.Stretch.ToString());
            PathTextBox.Text = source.Kind == WallpaperKind.Slideshow ? source.FolderPath : source.FilePath;
            ColorTextBox.Text = source.BackgroundColor;
            SubfoldersCheckBox.IsChecked = source.IncludeSubfolders;
            ShuffleCheckBox.IsChecked = source.Shuffle;
            IntervalSlider.Value = source.SlideshowIntervalSeconds;
            MuteCheckBox.IsChecked = source.MuteVideo;
            VolumeSlider.Value = source.VideoVolume;
            IntervalValueTextBlock.Text = "Change every " + source.SlideshowIntervalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";
            VolumeValueTextBlock.Text = "Volume " + source.VideoVolume.ToString(CultureInfo.InvariantCulture) + "%";
            EditorHeaderTextBlock.Text = _settings.SameForAllMonitors
                ? "Wallpaper for every monitor"
                : "Wallpaper for " + SelectedDisplayName();
        }
        finally
        {
            _suppressEditorEvents = false;
        }

        UpdateEditorVisibility();
    }

    private void SaveEditor()
    {
        WallpaperSource source = CurrentSource;
        WallpaperKind kind = ParseKind();

        source.Kind = kind;
        source.Stretch = ParseStretch();
        string path = PathTextBox.Text == null ? string.Empty : PathTextBox.Text.Trim();
        if (kind == WallpaperKind.Slideshow)
        {
            source.FolderPath = path;
        }
        else
        {
            source.FilePath = path;
        }

        source.BackgroundColor = string.IsNullOrWhiteSpace(ColorTextBox.Text) ? "#000000" : ColorTextBox.Text.Trim();
        source.IncludeSubfolders = SubfoldersCheckBox.IsChecked == true;
        source.Shuffle = ShuffleCheckBox.IsChecked == true;
        source.SlideshowIntervalSeconds = (int)Math.Round(IntervalSlider.Value);
        source.MuteVideo = MuteCheckBox.IsChecked == true;
        source.VideoVolume = (int)Math.Round(VolumeSlider.Value);
    }

    private void UpdateEditorVisibility()
    {
        WallpaperKind kind = ParseKind();
        SlideshowPanel.Visibility = kind == WallpaperKind.Slideshow ? Visibility.Visible : Visibility.Collapsed;
        VideoPanel.Visibility = kind == WallpaperKind.Video ? Visibility.Visible : Visibility.Collapsed;
        ColorPanel.Visibility = kind == WallpaperKind.SolidColor ? Visibility.Visible : Visibility.Collapsed;
        PathTextBox.IsEnabled = kind != WallpaperKind.SolidColor;

        switch (kind)
        {
            case WallpaperKind.Image:
                PathLabelTextBlock.Text = "Image file";
                break;
            case WallpaperKind.Slideshow:
                PathLabelTextBlock.Text = "Slideshow folder";
                break;
            case WallpaperKind.Video:
                PathLabelTextBlock.Text = "Video file";
                break;
            default:
                PathLabelTextBlock.Text = "Not used for a solid colour";
                break;
        }
    }

    private string SelectedDisplayName()
    {
        MonitorTarget? target = MonitorList.SelectedItem as MonitorTarget;
        return target != null ? target.DisplayName : "all monitors";
    }

    private WallpaperKind ParseKind()
    {
        ComboBoxItem? item = SourceKindComboBox.SelectedItem as ComboBoxItem;
        string? tag = item == null ? null : item.Tag as string;
        WallpaperKind kind;
        if (tag != null && Enum.TryParse(tag, true, out kind))
        {
            return kind;
        }

        return WallpaperKind.SolidColor;
    }

    private WallpaperStretch ParseStretch()
    {
        ComboBoxItem? item = StretchComboBox.SelectedItem as ComboBoxItem;
        string? tag = item == null ? null : item.Tag as string;
        WallpaperStretch stretch;
        if (tag != null && Enum.TryParse(tag, true, out stretch))
        {
            return stretch;
        }

        return WallpaperStretch.Fill;
    }

    private static void SelectComboItemByTag(ComboBox comboBox, string tag)
    {
        foreach (object item in comboBox.Items)
        {
            ComboBoxItem? comboBoxItem = item as ComboBoxItem;
            if (comboBoxItem == null)
            {
                continue;
            }

            string? itemTag = comboBoxItem.Tag as string;
            if (itemTag != null && string.Equals(itemTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = comboBoxItem;
                return;
            }
        }
    }

    // ── Status ──

    private void UpdateStatuses()
    {
        SyncMonitorTargets();

        foreach (MonitorTarget target in _targets)
        {
            target.StatusLine = _engine.GetMonitorStatus(target.DeviceName);
        }

        string engineState = _engine.IsRunning
            ? (_engine.IsManualPause ? "Engine running — paused manually" : "Engine running")
            : "Engine stopped";
        SetTextIfChanged(EngineStateTextBlock, engineState);
        SetTextIfChanged(StatusTextBlock, _engine.GetGlobalStatusText());

        string pauseLabel = _engine.IsManualPause ? "Resume all" : "Pause all";
        if (!string.Equals(TogglePauseButton.Content as string, pauseLabel, StringComparison.Ordinal))
        {
            TogglePauseButton.Content = pauseLabel;
        }
    }

    private static void SetTextIfChanged(TextBlock textBlock, string text)
    {
        if (!string.Equals(textBlock.Text, text, StringComparison.Ordinal))
        {
            textBlock.Text = text;
        }
    }

    // ── Commands ──

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SaveEditor();
        _settings.SameForAllMonitors = SameForAllCheckBox.IsChecked == true;
        _settings.PauseWhenMaximized = PauseMaximizedCheckBox.IsChecked == true;
        _settings.PauseWhenFullscreen = PauseFullscreenCheckBox.IsChecked == true;
        _settings.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        StartupService.SetEnabled(_settings.StartWithWindows);
        SettingsService.Save(_settings);
        _engine.ApplySettings(_settings);
        UpdateStatuses();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        WallpaperKind kind = ParseKind();
        if (kind == WallpaperKind.Slideshow)
        {
            OpenFolderDialog folderDialog = new OpenFolderDialog();
            folderDialog.Title = "Choose a slideshow folder";
            if (!string.IsNullOrWhiteSpace(PathTextBox.Text) && Directory.Exists(PathTextBox.Text))
            {
                folderDialog.InitialDirectory = PathTextBox.Text;
            }

            bool? folderResult = folderDialog.ShowDialog(this);
            if (folderResult == true)
            {
                PathTextBox.Text = folderDialog.FolderName;
                ApplyEditorChanges();
            }

            return;
        }

        OpenFileDialog fileDialog = new OpenFileDialog();
        if (kind == WallpaperKind.Video)
        {
            fileDialog.Title = "Choose a video file";
            fileDialog.Filter = "Video files|*.mp4;*.wmv;*.avi;*.mkv;*.mov;*.m4v;*.webm|All files|*.*";
        }
        else
        {
            fileDialog.Title = "Choose an image file";
            fileDialog.Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*";
        }

        bool? fileResult = fileDialog.ShowDialog(this);
        if (fileResult == true)
        {
            PathTextBox.Text = fileDialog.FileName;
            ApplyEditorChanges();
        }
    }

    /// <summary>
    /// Saves the editor and pushes it to the engine right away. This is what makes changing the source from a
    /// video to an image unload the video instead of leaving it decoding behind the desktop.
    /// </summary>
    private void ApplyEditorChanges()
    {
        if (_suppressEditorEvents)
        {
            return;
        }

        SaveEditor();
        SettingsService.Save(_settings);
        _engine.ApplySettings(_settings);
        UpdateStatuses();
    }

    private void PathTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyEditorChanges();
    }

    private void StretchComboBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        ApplyEditorChanges();
    }

    private void MuteCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ApplyEditorChanges();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsWindow window = new DiagnosticsWindow(_engine);
        window.Owner = this;
        window.ShowDialog();
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        Color initial = ColorParser.ParseHexColor(ColorTextBox.Text, Colors.Black);
        Color picked;
        if (!ColorPickerWindow.TryPick(this, initial, out picked))
        {
            return;
        }

        ColorTextBox.Text = ColorParser.FormatHex(picked);
        UpdateColorPreview();
        ApplyEditorChanges();
    }

    private void ColorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateColorPreview();
    }

    private void UpdateColorPreview()
    {
        if (ColorPreviewBorder == null)
        {
            return;
        }

        Color color = ColorParser.ParseHexColor(ColorTextBox.Text, Colors.Black);
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        ColorPreviewBorder.Background = brush;
    }

    private void TogglePause_Click(object sender, RoutedEventArgs e)
    {
        _engine.IsManualPause = !_engine.IsManualPause;
        UpdateStatuses();
    }

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowPlacement();
        Hide();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLog.DataDirectory);
            ProcessStartInfo startInfo = new ProcessStartInfo("explorer.exe", AppLog.DataDirectory);
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            AppLog.WriteException("opening the settings folder failed", exception);
        }
    }

    private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents)
        {
            return;
        }

        bool enabled = StartWithWindowsCheckBox.IsChecked == true;
        _settings.StartWithWindows = enabled;
        StartupService.SetEnabled(enabled);
        SettingsService.Save(_settings);
    }

    private void SourceKindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEditorEvents)
        {
            return;
        }

        UpdateEditorVisibility();
        ClearPathIfWrongKind();
        ApplyEditorChanges();
    }

    /// <summary>
    /// Switching from "video file" to "image file" must not leave the old .mp4 in the box: the engine would
    /// be asked for an image at a video path. The path is dropped and the status says no file is selected.
    /// </summary>
    private void ClearPathIfWrongKind()
    {
        WallpaperKind kind = ParseKind();
        string path = PathTextBox.Text == null ? string.Empty : PathTextBox.Text.Trim();
        if (kind == WallpaperKind.SolidColor || path.Length == 0)
        {
            return;
        }

        bool matchesKind;
        if (kind == WallpaperKind.Slideshow)
        {
            matchesKind = Directory.Exists(path);
        }
        else
        {
            string extension = Path.GetExtension(path);
            if (kind == WallpaperKind.Video)
            {
                matchesKind = IsExtensionOneOf(extension, ".mp4", ".wmv", ".avi", ".mkv", ".mov", ".m4v", ".webm");
            }
            else
            {
                matchesKind = IsExtensionOneOf(extension, ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp");
            }
        }

        if (matchesKind)
        {
            return;
        }

        bool wasSuppressed = _suppressEditorEvents;
        _suppressEditorEvents = true;
        try
        {
            PathTextBox.Text = string.Empty;
        }
        finally
        {
            _suppressEditorEvents = wasSuppressed;
        }
    }

    private static bool IsExtensionOneOf(string extension, params string[] allowed)
    {
        for (int index = 0; index < allowed.Length; index++)
        {
            if (string.Equals(extension, allowed[index], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IntervalValueTextBlock == null)
        {
            return;
        }

        IntervalValueTextBlock.Text = "Change every " + ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture) + " seconds";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeValueTextBlock == null)
        {
            return;
        }

        VolumeValueTextBlock.Text = "Volume " + ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture) + "%";
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        SaveWindowPlacement();

        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // Diagnostic for "the drop-down closes by itself": if this window is losing activation in a loop,
        // crash.log says so instead of leaving it to guesswork.
        DateTime now = DateTime.UtcNow;
        if (now - _lastDeactivatedLogUtc < TimeSpan.FromSeconds(3.0))
        {
            return;
        }

        _lastDeactivatedLogUtc = now;
        AppLog.Write("settings window deactivated");
    }

    // ── Window placement ──

    /// <summary>
    /// Positions the window explicitly instead of relying on WindowStartupLocation.CenterScreen, and
    /// restores the saved rectangle when it is still on a connected monitor.
    /// </summary>
    private void ApplySavedPlacement()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;

        double width = _settings.WindowWidth.HasValue && _settings.WindowWidth.Value >= MinWidth ? _settings.WindowWidth.Value : Width;
        double height = _settings.WindowHeight.HasValue && _settings.WindowHeight.Value >= MinHeight ? _settings.WindowHeight.Value : Height;
        Width = width;
        Height = height;

        if (_settings.WindowLeft.HasValue
            && _settings.WindowTop.HasValue
            && IsPlacementVisible(_settings.WindowLeft.Value, _settings.WindowTop.Value, width))
        {
            Left = _settings.WindowLeft.Value;
            Top = _settings.WindowTop.Value;
            return;
        }

        Left = SystemParameters.VirtualScreenLeft + Math.Max((SystemParameters.VirtualScreenWidth - width) / 2.0, 0.0);
        Top = SystemParameters.VirtualScreenTop + Math.Max((SystemParameters.VirtualScreenHeight - height) / 3.0, 0.0);
    }

    private static bool IsPlacementVisible(double left, double top, double width)
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        return left + 120.0 <= virtualRight
            && left >= virtualLeft - width + 120.0
            && top >= virtualTop
            && top + 60.0 <= virtualBottom;
    }

    /// <summary>Remembers where the window is, so the next launch (and the next Show) keeps that place.</summary>
    public void SaveWindowPlacement()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top) || double.IsNaN(Width) || double.IsNaN(Height))
        {
            // Started with --tray and never shown: there is no rectangle to remember yet, and Rect would throw.
            return;
        }

        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width < 100.0 || bounds.Height < 100.0)
        {
            return;
        }

        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        SettingsService.Save(_settings);
    }
}
