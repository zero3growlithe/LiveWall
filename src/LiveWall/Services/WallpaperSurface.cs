using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// One monitor's wallpaper window. It lives inside the desktop host as a child window (so it is behind the
/// icons, in front of the static wallpaper, and never shows up in Alt+Tab or steals focus) and draws one of:
/// a solid colour, a still image, a slideshow or a looping video.
///
/// Video is drawn through a <see cref="MediaPlayer"/> painted by a <see cref="VideoDrawing"/> brush instead
/// of a <see cref="MediaElement"/>: MediaElement expects to own the layout of a normal window, while a brush
/// renders wherever it is used — which is what a child window of the shell needs.
/// </summary>
public sealed class WallpaperSurface : IDisposable
{
    private const int PopupWindowStyle = unchecked((int)0x80000000);
    private const int VisibleWindowStyle = 0x10000000;
    private const int ChildWindowStyle = 0x40000000;
    private const int NoActivateExtendedStyle = 0x08000000;
    private const int ToolWindowExtendedStyle = 0x00000080;
    private const int OffscreenCoordinate = -32000;
    private const int FadeDurationMilliseconds = 350;

    private readonly string _deviceName;
    private readonly Grid _root;
    private readonly Border _backdrop;
    private readonly Image _baseImage;
    private readonly Image _fadeImage;
    private readonly Rectangle _videoRectangle;
    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _slideshowTimer;
    private readonly Random _random = new Random();

    private HwndSource? _interopSource;
    private WallpaperSource _config = new WallpaperSource();
    private List<string> _slideshowFiles = new List<string>();
    private int _slideIndex;
    private int _pixelWidth = 1;
    private int _pixelHeight = 1;
    private string _videoFileName = string.Empty;
    private bool _videoOpened;
    private bool _paused;
    private bool _disposed;
    private string _hostDescription = "not attached";

    public WallpaperSurface(string deviceName)
    {
        _deviceName = deviceName;

        _backdrop = new Border();
        _backdrop.Background = Brushes.Black;

        _baseImage = new Image();
        _baseImage.Stretch = Stretch.UniformToFill;
        _baseImage.Visibility = Visibility.Collapsed;

        _fadeImage = new Image();
        _fadeImage.Stretch = Stretch.UniformToFill;
        _fadeImage.Visibility = Visibility.Collapsed;

        _videoRectangle = new Rectangle();
        _videoRectangle.Visibility = Visibility.Collapsed;
        _videoRectangle.IsHitTestVisible = false;

        _root = new Grid();
        _root.Children.Add(_backdrop);
        _root.Children.Add(_baseImage);
        _root.Children.Add(_fadeImage);
        _root.Children.Add(_videoRectangle);

        _player = new MediaPlayer();
        _player.MediaOpened += Player_MediaOpened;
        _player.MediaFailed += Player_MediaFailed;
        _player.MediaEnded += Player_MediaEnded;

        _slideshowTimer = new DispatcherTimer();
        _slideshowTimer.Tick += SlideshowTimer_Tick;
    }

    /// <summary>Raised whenever <see cref="ActiveDescription"/> changes, so the UI can refresh its status text.</summary>
    public event EventHandler? DescriptionChanged;

    /// <summary>One-line description of what is being shown right now (or why nothing is).</summary>
    public string ActiveDescription { get; private set; } = "nothing";

    /// <summary>Why the last attach attempt failed, or an empty string.</summary>
    public string AttachFailureReason { get; private set; } = string.Empty;

    public string HostDescription
    {
        get { return _hostDescription; }
    }

    public bool IsAttached
    {
        get { return _interopSource != null; }
    }

    public bool IsPaused
    {
        get { return _paused; }
    }

    public bool IsVideoOpen
    {
        get { return _videoOpened; }
    }

    public string DeviceName
    {
        get { return _deviceName; }
    }

    // ── Attach ──

    public bool Attach(DesktopHost host, RECT monitorRectangle, RECT hostRectangle)
    {
        if (_disposed)
        {
            return false;
        }

        if (_interopSource != null)
        {
            return true;
        }

        AttachFailureReason = string.Empty;
        _pixelWidth = Math.Max(monitorRectangle.Width, 1);
        _pixelHeight = Math.Max(monitorRectangle.Height, 1);

        try
        {
            HwndSourceParameters parameters = new HwndSourceParameters("LiveWallSurface_" + SanitizeWindowName(_deviceName));
            parameters.WindowStyle = PopupWindowStyle | VisibleWindowStyle;
            parameters.ExtendedWindowStyle = NoActivateExtendedStyle | ToolWindowExtendedStyle;
            parameters.PositionX = OffscreenCoordinate;
            parameters.PositionY = OffscreenCoordinate;
            parameters.Width = 1;
            parameters.Height = 1;
            parameters.UsesPerPixelOpacity = false;

            _interopSource = new HwndSource(parameters);
            _interopSource.RootVisual = _root;

            if (!TryParentToDesktopHost(host, out string parentFailure))
            {
                AttachFailureReason = parentFailure;
                _interopSource.Dispose();
                _interopSource = null;
                return false;
            }

            PlaceWindow(host, monitorRectangle, hostRectangle);
            _hostDescription = host.Describe();
            AppLog.Write("surface attached for " + _deviceName + " at " + monitorRectangle + " under " + host.Describe());
            return true;
        }
        catch (Exception exception)
        {
            AttachFailureReason = "creating the wallpaper window failed: " + exception.Message;
            AppLog.WriteException("surface attach failed for " + _deviceName, exception);
            if (_interopSource != null)
            {
                _interopSource.Dispose();
                _interopSource = null;
            }

            return false;
        }
    }

    /// <summary>
    /// Turns the surface into a child of the desktop host. The styles must be in place BEFORE SetParent, and
    /// on the raised desktop (Windows 11 24H2+) the window has to be layered, otherwise the shell never
    /// composes it and the wallpaper stays invisible even though every call succeeds.
    /// </summary>
    private bool TryParentToDesktopHost(DesktopHost host, out string failure)
    {
        failure = string.Empty;
        if (_interopSource == null)
        {
            failure = "the wallpaper window does not exist";
            return false;
        }

        IntPtr surfaceHandle = _interopSource.Handle;
        int extendedStyle = NoActivateExtendedStyle | ToolWindowExtendedStyle;
        if (host.UseLayeredWindow)
        {
            extendedStyle |= NativeMethods.WS_EX_LAYERED;
        }

        NativeMethods.SetWindowLongPtr(surfaceHandle, NativeMethods.GWL_STYLE, new IntPtr(ChildWindowStyle | VisibleWindowStyle));
        NativeMethods.SetWindowLongPtr(surfaceHandle, NativeMethods.GWL_EXSTYLE, new IntPtr(extendedStyle));

        NativeMethods.SetParent(surfaceHandle, host.ParentWindow);
        int error = Marshal.GetLastWin32Error();

        if (NativeMethods.GetParent(surfaceHandle) != host.ParentWindow)
        {
            failure = "SetParent(0x" + host.ParentWindow.ToInt64().ToString("X", CultureInfo.InvariantCulture)
                + " [" + GetWindowClassName(host.ParentWindow) + "]) failed with Win32 error "
                + error.ToString(CultureInfo.InvariantCulture);
            AppLog.Write("surface attach failed for " + _deviceName + ": " + failure);
            return false;
        }

        if (host.UseLayeredWindow)
        {
            NativeMethods.SetLayeredWindowAttributes(surfaceHandle, 0, 255, NativeMethods.LWA_ALPHA);
        }

        return true;
    }

    private void PlaceWindow(DesktopHost host, RECT monitorRectangle, RECT hostRectangle)
    {
        if (_interopSource == null)
        {
            return;
        }

        int x = monitorRectangle.Left - hostRectangle.Left;
        int y = monitorRectangle.Top - hostRectangle.Top;

        NativeMethods.SetWindowPos(
            _interopSource.Handle,
            host.InsertAfterWindow,
            x,
            y,
            _pixelWidth,
            _pixelHeight,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);

        int left;
        int top;
        int width;
        int height;
        if (TryGetClientOrigin(host.ParentWindow, out left, out top, out width, out height))
        {
            AppLog.Write("surface placed for " + _deviceName + ": child offset " + x.ToString(CultureInfo.InvariantCulture)
                + "," + y.ToString(CultureInfo.InvariantCulture)
                + " size " + _pixelWidth.ToString(CultureInfo.InvariantCulture) + "x" + _pixelHeight.ToString(CultureInfo.InvariantCulture)
                + "; host client " + left.ToString(CultureInfo.InvariantCulture) + "," + top.ToString(CultureInfo.InvariantCulture)
                + " " + width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static bool TryGetClientOrigin(IntPtr parentWindow, out int left, out int top, out int width, out int height)
    {
        left = 0;
        top = 0;
        width = 0;
        height = 0;

        NativeMethods.NativePoint origin = new NativeMethods.NativePoint();
        if (!NativeMethods.ClientToScreen(parentWindow, ref origin))
        {
            return false;
        }

        RECT clientRectangle = new RECT();
        if (!NativeMethods.GetClientRect(parentWindow, out clientRectangle))
        {
            return false;
        }

        left = origin.X;
        top = origin.Y;
        width = clientRectangle.Width;
        height = clientRectangle.Height;
        return true;
    }

    private static string GetWindowClassName(IntPtr windowHandle)
    {
        StringBuilder builder = new StringBuilder(256);
        NativeMethods.GetClassName(windowHandle, builder, builder.Capacity);
        return builder.ToString();
    }

    // ── Source ──

    public void ApplySource(WallpaperSource config)
    {
        if (_disposed)
        {
            return;
        }

        _config = config;

        StopSlideshow();
        UnloadVideo();
        HideImages();
        _backdrop.Background = CreateBackgroundBrush(config.BackgroundColor);

        switch (config.Kind)
        {
            case WallpaperKind.Image:
                ApplyImageStretch();
                ShowStillImage(config.FilePath);
                break;

            case WallpaperKind.Slideshow:
                ApplyImageStretch();
                StartSlideshow(config);
                break;

            case WallpaperKind.Video:
                StartVideo(config);
                break;

            default:
                ActiveDescription = "solid colour " + config.BackgroundColor;
                break;
        }

        RaiseDescriptionChanged();
    }

    public void SetPaused(bool paused)
    {
        if (_disposed || _paused == paused)
        {
            return;
        }

        _paused = paused;

        switch (_config.Kind)
        {
            case WallpaperKind.Video:
                if (_videoOpened)
                {
                    try
                    {
                        if (paused)
                        {
                            _player.Pause();
                        }
                        else
                        {
                            _player.Play();
                        }
                    }
                    catch (Exception exception)
                    {
                        AppLog.WriteException("pausing a video failed", exception);
                    }
                }

                break;

            case WallpaperKind.Slideshow:
                if (paused)
                {
                    _slideshowTimer.Stop();
                }
                else if (_slideshowFiles.Count > 0)
                {
                    StartSlideshowTimer();
                }

                break;
        }
    }

    // ── Still images and slideshow ──

    private void ShowStillImage(string filePath)
    {
        BitmapSource? image = LoadBitmap(filePath, out string failure);
        if (image == null)
        {
            ActiveDescription = "image — " + failure;
            return;
        }

        ShowImage(image);
        ActiveDescription = "image " + DescribeFileName(filePath);
    }

    private void StartSlideshow(WallpaperSource config)
    {
        _slideshowFiles = SlideshowPlaylist.Collect(config);
        if (_slideshowFiles.Count == 0)
        {
            ActiveDescription = "slideshow — no images found in " + DescribeFileName(config.FolderPath);
            return;
        }

        if (config.Shuffle)
        {
            SlideshowPlaylist.Shuffle(_slideshowFiles, _random);
        }

        _slideIndex = 0;
        string firstFailure = ShowCurrentSlide();
        ActiveDescription = "slideshow " + DescribeFileName(config.FolderPath)
            + " (" + _slideshowFiles.Count.ToString(CultureInfo.InvariantCulture) + " images, "
            + config.SlideshowIntervalSeconds.ToString(CultureInfo.InvariantCulture) + "s)";

        if (firstFailure != null && firstFailure.Length > 0)
        {
            ActiveDescription += " — " + firstFailure;
        }

        if (!_paused)
        {
            StartSlideshowTimer();
        }
    }

    private string ShowCurrentSlide()
    {
        if (_slideshowFiles.Count == 0)
        {
            return "no files";
        }

        for (int attempt = 0; attempt < _slideshowFiles.Count; attempt++)
        {
            string path = _slideshowFiles[_slideIndex];
            _slideIndex = (_slideIndex + 1) % _slideshowFiles.Count;

            BitmapSource? image = LoadBitmap(path, out string failure);
            if (image != null)
            {
                ShowImage(image);
                return string.Empty;
            }

            AppLog.Write("slideshow skipped " + path + ": " + failure);
        }

        return "none of the images could be loaded";
    }

    private void StartSlideshowTimer()
    {
        int seconds = _config.SlideshowIntervalSeconds;
        if (seconds < 1)
        {
            seconds = 1;
        }

        _slideshowTimer.Interval = TimeSpan.FromSeconds(seconds);
        _slideshowTimer.Start();
    }

    private void StopSlideshow()
    {
        _slideshowTimer.Stop();
        _slideshowFiles = new List<string>();
        _slideIndex = 0;
    }

    private void SlideshowTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _paused)
        {
            return;
        }

        ShowCurrentSlide();
        RaiseDescriptionChanged();
    }

    private void ShowImage(BitmapSource image)
    {
        _videoRectangle.Visibility = Visibility.Collapsed;
        _videoRectangle.Fill = null;

        _fadeImage.Source = image;
        _fadeImage.Visibility = Visibility.Visible;
        _fadeImage.Opacity = 0.0;

        DoubleAnimation fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(FadeDurationMilliseconds));
        fade.Completed += Fade_Completed;
        _fadeImage.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void Fade_Completed(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _baseImage.Source = _fadeImage.Source;
        _baseImage.Visibility = Visibility.Visible;
        _fadeImage.BeginAnimation(UIElement.OpacityProperty, null);
        _fadeImage.Visibility = Visibility.Collapsed;
    }

    private void HideImages()
    {
        _baseImage.Source = null;
        _baseImage.Visibility = Visibility.Collapsed;
        _fadeImage.BeginAnimation(UIElement.OpacityProperty, null);
        _fadeImage.Source = null;
        _fadeImage.Visibility = Visibility.Collapsed;
    }

    private void ApplyImageStretch()
    {
        Stretch stretch = ToMediaStretch(_config.Stretch);
        _baseImage.Stretch = stretch;
        _fadeImage.Stretch = stretch;
    }

    private static BitmapSource? LoadBitmap(string filePath, out string failure)
    {
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            failure = "no file selected";
            return null;
        }

        if (!File.Exists(filePath))
        {
            failure = "file not found: " + filePath;
            return null;
        }

        try
        {
            BitmapImage image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(filePath, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception)
        {
            failure = "could not be loaded: " + exception.Message;
            AppLog.WriteException("image load failed: " + filePath, exception);
            return null;
        }
    }

    // ── Video ──

    private void StartVideo(WallpaperSource config)
    {
        string path = config.FilePath;
        _videoFileName = DescribeFileName(path);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ActiveDescription = "video — file not found: " + path;
            return;
        }

        _videoOpened = false;
        _videoRectangle.Visibility = Visibility.Visible;

        try
        {
            _player.Volume = Math.Max(0.0, Math.Min(1.0, config.VideoVolume / 100.0));
            _player.IsMuted = config.MuteVideo;
            _player.Open(new Uri(path, UriKind.Absolute));
            if (!_paused)
            {
                _player.Play();
            }

            ActiveDescription = "video " + _videoFileName + " — opening…";
        }
        catch (Exception exception)
        {
            ActiveDescription = "video " + _videoFileName + " — could not start: " + exception.Message;
            AppLog.WriteException("video start failed: " + path, exception);
        }
    }

    private void Player_MediaOpened(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _videoOpened = true;

        double width = _player.NaturalVideoWidth > 0 ? _player.NaturalVideoWidth : _pixelWidth;
        double height = _player.NaturalVideoHeight > 0 ? _player.NaturalVideoHeight : _pixelHeight;

        VideoDrawing drawing = new VideoDrawing();
        drawing.Rect = new Rect(0.0, 0.0, width, height);
        drawing.Player = _player;

        DrawingBrush brush = new DrawingBrush();
        brush.Drawing = drawing;
        brush.Stretch = ToBrushStretch(_config.Stretch);
        brush.AlignmentX = AlignmentX.Center;
        brush.AlignmentY = AlignmentY.Center;
        brush.ViewboxUnits = BrushMappingMode.Absolute;
        brush.Viewbox = new Rect(0.0, 0.0, width, height);
        brush.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
        brush.Viewport = new Rect(0.0, 0.0, 1.0, 1.0);

        _videoRectangle.Fill = brush;
        _videoRectangle.Visibility = Visibility.Visible;

        if (_paused)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }

        ActiveDescription = "video " + _videoFileName
            + " (" + ((int)width).ToString(CultureInfo.InvariantCulture)
            + "x" + ((int)height).ToString(CultureInfo.InvariantCulture)
            + (_config.MuteVideo ? ", muted" : string.Empty)
            + ", looping)";

        AppLog.Write("video opened for " + _deviceName + ": " + _videoFileName + " " + width + "x" + height);
        RaiseDescriptionChanged();
    }

    private void Player_MediaFailed(object? sender, ExceptionEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        string message = e.ErrorException == null ? "unknown media error" : e.ErrorException.Message;
        ActiveDescription = "video " + _videoFileName + " — playback failed: " + message;
        AppLog.Write("video failed for " + _deviceName + ": " + message);
        UnloadVideo();
        RaiseDescriptionChanged();
    }

    private void Player_MediaEnded(object? sender, EventArgs e)
    {
        if (_disposed || !_videoOpened)
        {
            return;
        }

        try
        {
            _player.Position = TimeSpan.Zero;
            if (!_paused)
            {
                _player.Play();
            }
        }
        catch (Exception exception)
        {
            AppLog.WriteException("looping a video failed", exception);
        }
    }

    /// <summary>Stops and releases the media session so the file is no longer held and nothing keeps decoding.</summary>
    private void UnloadVideo()
    {
        _videoOpened = false;
        _videoFileName = string.Empty;
        _videoRectangle.Visibility = Visibility.Collapsed;
        _videoRectangle.Fill = null;

        try
        {
            _player.Stop();
        }
        catch (Exception exception)
        {
            AppLog.WriteException("stopping a video failed", exception);
        }

        try
        {
            _player.Close();
        }
        catch (Exception exception)
        {
            AppLog.WriteException("closing a video failed", exception);
        }
    }

    private Stretch ToBrushStretch(WallpaperStretch stretch)
    {
        switch (stretch)
        {
            case WallpaperStretch.Fit:
                return Stretch.Uniform;
            case WallpaperStretch.Stretch:
                return Stretch.Fill;
            case WallpaperStretch.Center:
                return Stretch.None;
            default:
                return Stretch.UniformToFill;
        }
    }

    // ── Helpers ──

    private static Stretch ToMediaStretch(WallpaperStretch stretch)
    {
        switch (stretch)
        {
            case WallpaperStretch.Fit:
                return Stretch.Uniform;
            case WallpaperStretch.Stretch:
                return Stretch.Fill;
            case WallpaperStretch.Center:
                return Stretch.None;
            default:
                return Stretch.UniformToFill;
        }
    }

    private static Brush CreateBackgroundBrush(string colorText)
    {
        Color color = ColorParser.ParseHexColor(colorText, Colors.Black);
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string DescribeFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "(none)";
        }

        try
        {
            string name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private void RaiseDescriptionChanged()
    {
        EventHandler? handler = DescriptionChanged;
        if (handler != null)
        {
            handler(this, EventArgs.Empty);
        }
    }

    /// <summary>One line for the diagnostics panel.</summary>
    public string DiagnosticLine()
    {
        return _deviceName
            + " | attached=" + (IsAttached ? "yes" : "no")
            + " | handle=0x" + (_interopSource == null ? "0" : _interopSource.Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture))
            + " | size=" + _pixelWidth.ToString(CultureInfo.InvariantCulture) + "x" + _pixelHeight.ToString(CultureInfo.InvariantCulture)
            + " | paused=" + (_paused ? "yes" : "no")
            + " | videoOpen=" + (_videoOpened ? "yes" : "no")
            + " | host=" + _hostDescription
            + " | what=" + ActiveDescription;
    }

    private static string SanitizeWindowName(string deviceName)
    {
        StringBuilder builder = new StringBuilder(deviceName.Length);
        for (int index = 0; index < deviceName.Length; index++)
        {
            char character = deviceName[index];
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
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
            _player.Close();
        }
        catch (Exception)
        {
            // Closing an already closed session is not worth reporting.
        }

        try
        {
            _root.Children.Clear();
            if (_interopSource != null)
            {
                _interopSource.Dispose();
                _interopSource = null;
            }
        }
        catch (Exception exception)
        {
            AppLog.WriteException("disposing a wallpaper surface failed", exception);
        }
    }
}
