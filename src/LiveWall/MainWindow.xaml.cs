using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    public MainWindow(WallpaperEngine engine, WallpaperSettings settings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        MonitorList.ItemsSource = _targets;
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

        RefreshMonitorTargets();
        LoadEditor();
        UpdateStatuses();
    }

    private void Engine_StatusChanged(object? sender, EventArgs e)
    {
        RefreshFromEngine();
    }

    // ── Monitor list ──

    private void RefreshMonitorTargets()
    {
        string selectedDeviceName = string.Empty;
        MonitorTarget? selected = MonitorList.SelectedItem as MonitorTarget;
        if (selected != null)
        {
            selectedDeviceName = selected.DeviceName;
        }

        _suppressEditorEvents = true;
        try
        {
            _targets.Clear();
            foreach (MonitorDescriptor monitor in _engine.Monitors)
            {
                MonitorTarget target = new MonitorTarget(monitor.DeviceName, monitor.DisplayName);
                target.StatusLine = _engine.GetMonitorStatus(monitor.DeviceName);
                _targets.Add(target);
            }

            if (_targets.Count > 0)
            {
                int selectedIndex = 0;
                for (int index = 0; index < _targets.Count; index++)
                {
                    if (string.Equals(_targets[index].DeviceName, selectedDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = index;
                        break;
                    }
                }

                MonitorList.SelectedIndex = selectedIndex;
            }
        }
        finally
        {
            _suppressEditorEvents = false;
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
        if (_targets.Count != _engine.Monitors.Count)
        {
            RefreshMonitorTargets();
        }

        foreach (MonitorTarget target in _targets)
        {
            target.StatusLine = _engine.GetMonitorStatus(target.DeviceName);
        }

        if (_engine.IsRunning)
        {
            EngineStateTextBlock.Text = _engine.IsManualPause ? "Engine running — paused manually" : "Engine running";
        }
        else
        {
            EngineStateTextBlock.Text = "Engine stopped";
        }

        StatusTextBlock.Text = _engine.GetGlobalStatusText();
        TogglePauseButton.Content = _engine.IsManualPause ? "Resume all" : "Pause all";
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
                SaveEditor();
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
            SaveEditor();
        }
    }

    private void TogglePause_Click(object sender, RoutedEventArgs e)
    {
        _engine.IsManualPause = !_engine.IsManualPause;
        UpdateStatuses();
    }

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
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
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
