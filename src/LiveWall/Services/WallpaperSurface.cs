using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// One monitor's wallpaper surface: a child window of the desktop host window that draws an image,
/// a slideshow or a looping video. It never activates, never shows in Alt+Tab and never takes clicks.
/// </summary>
public sealed class WallpaperSurface : IDisposable
{
    private const int PopupWindowStyle = unchecked((int)0x80000000);
    private const int ChildWindowStyle = 0x40000000;
    private const int VisibleWindowStyle = 0x10000000;
    private const int NoActivateExtendedStyle = 0x08000000;
    private const int ToolWindowExtendedStyle = 0x00000080;
    private const int FadeDurationMilliseconds = 450;

    private readonly string _deviceName;
    private readonly Grid _root;
    private readonly Image _backImage;
    private readonly Image _frontImage;
    private readonly MediaElement _video;
    private readonly DispatcherTimer _slideshowTimer;
    private readonly Random _random = new Random();

    private HwndSource? _interopSource;
    private WallpaperSource _config = new WallpaperSource();
    private List<string> _slideshowFiles = new List<string>();
    private int _slideIndex;
    private bool _paused;
    private bool _disposed;
    private int _pixelWidth = 1920;
    private int _pixelHeight = 1080;

    public WallpaperSurface(string deviceName)
    {
        _deviceName = deviceName;

        _root = new Grid();
        _root.Background = Brushes.Black;
        _root.ClipToBounds = true;

        _backImage = new Image();
        _backImage.Opacity = 0.0;
        ConfigureImageElement(_backImage);

        _frontImage = new Image();
        _frontImage.Opacity = 1.0;
        ConfigureImageElement(_frontImage);

        _video = new MediaElement();
        _video.LoadedBehavior = MediaState.Manual;
        _video.UnloadedBehavior = MediaState.Manual;
        _video.ScrubbingEnabled = true;
        _video.IsHitTestVisible = false;
        _video.Visibility = Visibility.Collapsed;
        _video.Stretch = Stretch.UniformToFill;
        _video.Volume = 0.0;
        _video.IsMuted = true;
        _video.MediaEnded += Video_MediaEnded;
        _video.MediaFailed += Video_MediaFailed;
        _video.MediaOpened += Video_MediaOpened;

        _root.Children.Add(_backImage);
        _root.Children.Add(_frontImage);
        _root.Children.Add(_video);

        _slideshowTimer = new DispatcherTimer();
        _slideshowTimer.Tick += SlideshowTimer_Tick;
    }

    public string DeviceName
    {
        get { return _deviceName; }
    }

    /// <summary>What this monitor is currently showing (used for the status line).</summary>
    public string ActiveDescription { get; private set; } = "nothing";

    public string AttachFailureReason { get; private set; } = string.Empty;

    public bool IsAttached
    {
        get { return _interopSource != null; }
    }

    public bool IsPaused
    {
        get { return _paused; }
    }

    /// <summary>
    /// Creates the child window inside the desktop host window and places it over the monitor.
    /// </summary>
    public bool Attach(IntPtr workerWindow, RECT monitorRectangle, RECT workerRectangle)
    {
        if (_interopSource != null)
        {
            return true;
        }

        _pixelWidth = Math.Max(monitorRectangle.Width, 1);
        _pixelHeight = Math.Max(monitorRectangle.Height, 1);

        try
        {
            HwndSourceParameters parameters = new HwndSourceParameters("LiveWallSurface_" + SanitizeWindowName(_deviceName));
            parameters.WindowStyle = PopupWindowStyle | ChildWindowStyle | VisibleWindowStyle;
            parameters.ExtendedWindowStyle = NoActivateExtendedStyle | ToolWindowExtendedStyle;
            parameters.ParentWindow = workerWindow;
            parameters.PositionX = 0;
            parameters.PositionY = 0;
            parameters.Width = 1;
            parameters.Height = 1;
            parameters.UsesPerPixelOpacity = false;

            _interopSource = new HwndSource(parameters);
            _interopSource.RootVisual = _root;

            if (NativeMethods.GetParent(_interopSource.Handle) != workerWindow)
            {
                NativeMethods.SetParent(_interopSource.Handle, workerWindow);
            }

            if (NativeMethods.GetParent(_interopSource.Handle) != workerWindow)
            {
                AttachFailureReason = "the desktop window rejected the wallpaper host (SetParent failed)";
                _interopSource.Dispose();
                _interopSource = null;
                return false;
            }

            PlaceWindow(monitorRectangle, workerRectangle);
            AppLog.Write("surface attached for " + _deviceName + " at " + monitorRectangle);
            return true;
        }
        catch (Exception exception)
        {
            AttachFailureReason = exception.Message;
            AppLog.WriteException("surface attach failed for " + _deviceName, exception);
            if (_interopSource != null)
            {
                _interopSource.Dispose();
                _interopSource = null;
            }

            return false;
        }
    }

    public void UpdateRectangle(RECT monitorRectangle, RECT workerRectangle)
    {
        _pixelWidth = Math.Max(monitorRectangle.Width, 1);
        _pixelHeight = Math.Max(monitorRectangle.Height, 1);
        PlaceWindow(monitorRectangle, workerRectangle);
    }

    /// <summary>
    /// Loads a new wallpaper definition and starts playing it (unless the surface is paused afterwards).
    /// </summary>
    public void ApplySource(WallpaperSource config)
    {
        _config = config;
        _paused = false;

        StopSlideshow();
        try
        {
            _video.Stop();
            _video.Close();
        }
        catch (Exception exception)
        {
            AppLog.WriteException("video close failed for " + _deviceName, exception);
        }

        _video.Source = null;
        _video.Visibility = Visibility.Collapsed;

        _frontImage.BeginAnimation(UIElement.OpacityProperty, null);
        _frontImage.Source = null;
        _backImage.Source = null;
        _frontImage.Opacity = 1.0;
        _backImage.Opacity = 0.0;
        _root.Background = CreateBackgroundBrush(config.BackgroundColor);
        ApplyStretch(config.Stretch);

        switch (config.Kind)
        {
            case WallpaperKind.SolidColor:
                ActiveDescription = "solid colour " + config.BackgroundColor;
                break;

            case WallpaperKind.Image:
                ShowImageLayers();
                ActiveDescription = string.IsNullOrWhiteSpace(config.FilePath)
                    ? "image — no file selected"
                    : "image " + Path.GetFileName(config.FilePath);
                ShowImageFile(config.FilePath);
                break;

            case WallpaperKind.Slideshow:
                ShowImageLayers();
                _slideshowFiles = SlideshowPlaylist.Collect(config);
                if (config.Shuffle)
                {
                    SlideshowPlaylist.Shuffle(_slideshowFiles, _random);
                }

                if (_slideshowFiles.Count == 0)
                {
                    ActiveDescription = "slideshow — no images found in " + (string.IsNullOrWhiteSpace(config.FolderPath) ? "(no folder selected)" : config.FolderPath);
                }
                else
                {
                    _slideIndex = 0;
                    ActiveDescription = "slideshow — " + _slideshowFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " image(s) every " + Math.Clamp(config.SlideshowIntervalSeconds, 5, 3600).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
                    ShowImageFile(_slideshowFiles[0]);
                    StartSlideshow();
                }

                break;

            case WallpaperKind.Video:
                StartVideo(config);
                break;
        }
    }

    /// <summary>
    /// Pauses / resumes only what can be paused: video playback and the slideshow timer.
    /// </summary>
    public void SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            return;
        }

        _paused = paused;

        switch (_config.Kind)
        {
            case WallpaperKind.Video:
                try
                {
                    if (paused)
                    {
                        _video.Pause();
                    }
                    else if (_video.Source != null)
                    {
                        _video.Play();
                    }
                }
                catch (Exception exception)
                {
                    AppLog.WriteException("video pause/resume failed for " + _deviceName, exception);
                }

                break;

            case WallpaperKind.Slideshow:
                if (paused)
                {
                    StopSlideshow();
                }
                else
                {
                    StartSlideshow();
                }

                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopSlideshow();

        try
        {
            _video.Stop();
            _video.Close();
        }
        catch (Exception)
        {
            // A closed media element is expected during shutdown.
        }

        try
        {
            if (_interopSource != null)
            {
                _interopSource.Dispose();
            }
        }
        catch (Exception)
        {
            // The desktop window may already be gone (Explorer restart).
        }

        _interopSource = null;
        _root.Children.Clear();
    }

    // ── Placement ──

    private void PlaceWindow(RECT monitorRectangle, RECT workerRectangle)
    {
        if (_interopSource == null)
        {
            return;
        }

        int x = monitorRectangle.Left - workerRectangle.Left;
        int y = monitorRectangle.Top - workerRectangle.Top;
        NativeMethods.SetWindowPos(
            _interopSource.Handle,
            IntPtr.Zero,
            x,
            y,
            Math.Max(monitorRectangle.Width, 1),
            Math.Max(monitorRectangle.Height, 1),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);
    }

    private static string SanitizeWindowName(string deviceName)
    {
        string cleaned = deviceName.Replace("\\", string.Empty).Replace(".", string.Empty).Replace(" ", string.Empty);
        return cleaned.Length == 0 ? "monitor" : cleaned;
    }

    // ── Images ──

    private static void ConfigureImageElement(Image image)
    {
        image.Stretch = Stretch.UniformToFill;
        image.HorizontalAlignment = HorizontalAlignment.Stretch;
        image.VerticalAlignment = VerticalAlignment.Stretch;
        image.IsHitTestVisible = false;
    }

    private void ShowImageLayers()
    {
        _frontImage.Visibility = Visibility.Visible;
        _backImage.Visibility = Visibility.Visible;
    }

    private void ShowImageFile(string path)
    {
        BitmapImage? bitmap = LoadBitmap(path);
        if (bitmap == null)
        {
            return;
        }

        bool hadPreviousImage = _frontImage.Source != null;
        _backImage.BeginAnimation(UIElement.OpacityProperty, null);
        _backImage.Source = _frontImage.Source;
        _backImage.Opacity = hadPreviousImage ? 1.0 : 0.0;

        _frontImage.BeginAnimation(UIElement.OpacityProperty, null);
        _frontImage.Source = bitmap;
        _frontImage.Visibility = Visibility.Visible;

        if (hadPreviousImage)
        {
            _frontImage.Opacity = 0.0;
            DoubleAnimation fade = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(FadeDurationMilliseconds)));
            _frontImage.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        else
        {
            _frontImage.Opacity = 1.0;
        }
    }

    private BitmapImage? LoadBitmap(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ActiveDescription = "image — file not found: " + path;
                return null;
            }

            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (_pixelWidth > 0)
            {
                bitmap.DecodePixelWidth = _pixelWidth;
            }

            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception)
        {
            AppLog.WriteException("image load failed: " + path, exception);
            ActiveDescription = "image — could not be decoded: " + Path.GetFileName(path);
            return null;
        }
    }

    private void ApplyStretch(WallpaperStretch stretch)
    {
        Stretch imageStretch = Stretch.UniformToFill;
        HorizontalAlignment horizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment verticalAlignment = VerticalAlignment.Stretch;

        switch (stretch)
        {
            case WallpaperStretch.Fit:
                imageStretch = Stretch.Uniform;
                break;

            case WallpaperStretch.Stretch:
                imageStretch = Stretch.Fill;
                break;

            case WallpaperStretch.Center:
                imageStretch = Stretch.None;
                horizontalAlignment = HorizontalAlignment.Center;
                verticalAlignment = VerticalAlignment.Center;
                break;
        }

        _frontImage.Stretch = imageStretch;
        _backImage.Stretch = imageStretch;
        _frontImage.HorizontalAlignment = horizontalAlignment;
        _backImage.HorizontalAlignment = horizontalAlignment;
        _frontImage.VerticalAlignment = verticalAlignment;
        _backImage.VerticalAlignment = verticalAlignment;
        _video.Stretch = imageStretch == Stretch.None ? Stretch.Uniform : imageStretch;
    }

    private static Brush CreateBackgroundBrush(string colorText)
    {
        try
        {
            object converted = ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(colorText) ? "#000000" : colorText);
            if (converted is Color color)
            {
                SolidColorBrush brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch (Exception)
        {
            // An invalid colour string falls back to black; nothing to log beyond that.
        }

        return Brushes.Black;
    }

    // ── Video ──

    private void StartVideo(WallpaperSource config)
    {
        string path = config.FilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ActiveDescription = "video — file not found: " + path;
            return;
        }

        try
        {
            _video.IsMuted = config.MuteVideo;
            _video.Volume = Math.Clamp(config.VideoVolume, 0, 100) / 100.0;
            _video.Visibility = Visibility.Visible;
            _video.Source = new Uri(path, UriKind.Absolute);
            _video.Play();
            ActiveDescription = "video " + Path.GetFileName(path) + (config.MuteVideo ? " (muted)" : string.Empty);
        }
        catch (Exception exception)
        {
            AppLog.WriteException("video start failed: " + path, exception);
            ActiveDescription = "video — could not be started: " + Path.GetFileName(path);
        }
    }

    private void Video_MediaEnded(object? sender, RoutedEventArgs e)
    {
        try
        {
            _video.Position = TimeSpan.Zero;
            if (!_paused)
            {
                _video.Play();
            }
        }
        catch (Exception exception)
        {
            AppLog.WriteException("video loop failed for " + _deviceName, exception);
        }
    }

    private void Video_MediaOpened(object? sender, RoutedEventArgs e)
    {
        if (_paused)
        {
            _video.Pause();
        }
    }

    private void Video_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        string message = e.ErrorException != null ? e.ErrorException.Message : "unknown media error";
        ActiveDescription = "video — playback failed: " + message;
        AppLog.Write("video playback failed for " + _deviceName + ": " + message);
    }

    // ── Slideshow ──

    private void StartSlideshow()
    {
        if (_slideshowFiles.Count == 0)
        {
            return;
        }

        _slideshowTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_config.SlideshowIntervalSeconds, 5, 3600));
        _slideshowTimer.Start();
    }

    private void StopSlideshow()
    {
        _slideshowTimer.Stop();
    }

    private void SlideshowTimer_Tick(object? sender, EventArgs e)
    {
        if (_paused || _slideshowFiles.Count == 0)
        {
            return;
        }

        _slideIndex = (_slideIndex + 1) % _slideshowFiles.Count;
        ShowImageFile(_slideshowFiles[_slideIndex]);
    }
}
