using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// A wallpaper surface that paints through DirectComposition instead of WPF.
/// <para>
/// The window is a plain child of the desktop host (a "Static" window, no WPF content at all) and the pixels
/// arrive through a D3D11 flip-model swapchain bound to a DirectComposition visual. On Windows 11 24H2+ this
/// is the only path the shell composes, so it is the default renderer.
/// </para>
/// <para>
/// Two threads are involved and the split is deliberate: the window, the slideshow timer and image decoding
/// live on the UI thread, while the D3D device, the swapchain and the video decoder live on a dedicated
/// render thread (a D3D11 immediate context is single threaded, and decoding must never block the UI).
/// </para>
/// </summary>
public sealed class CompositionSurface : IWallpaperSurface
{
    private const int PixelsPerDipBase = 96;
    private const int DefaultDpi = 96;
    private const int RenderThreadJoinMilliseconds = 2500;

    private sealed class FrameCommand
    {
        public FrameCommand(byte[] pixels, int width, int height, WallpaperStretch stretch, string description)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
            Stretch = stretch;
            Description = description;
        }

        public byte[] Pixels { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public WallpaperStretch Stretch { get; private set; }

        public string Description { get; private set; }
    }

    private sealed class VideoCommand
    {
        public VideoCommand(string filePath, WallpaperStretch stretch, string description)
        {
            FilePath = filePath;
            Stretch = stretch;
            Description = description;
        }

        public string FilePath { get; private set; }

        public WallpaperStretch Stretch { get; private set; }

        public string Description { get; private set; }
    }

    private readonly string _deviceName;
    /// <summary>How long a covered monitor may stay paused before its video decoder is closed (memory).</summary>
    private const double PausedDecoderReleaseSeconds = 15.0;

    private readonly WallpaperScaleMode _scaleMode;
    private readonly Dispatcher _dispatcher;
    private readonly object _sync = new object();
    private readonly Random _shuffleRandom = new Random();

    private List<string> _playlistFiles = new List<string>();
    private int _playlistIndex;
    private DispatcherTimer? _slideshowTimer;
    private WallpaperSource _source = new WallpaperSource();

    private IntPtr _windowHandle;
    private Thread? _renderThread;
    private bool _attached;
    private bool _disposed;
    private bool _paused;
    private bool _isVideoOpen;
    private bool _stopRenderThread;
    private FrameCommand? _pendingFrame;
    private VideoCommand? _pendingVideo;
    private WallpaperStretch _stretch = WallpaperStretch.Fill;
    private string _description = "nothing";
    private string _attachFailure = string.Empty;
    private string _hostDescription = "not attached";
    private string _rendererDescription = "not created";
    private int _windowWidth;
    private int _windowHeight;
    private double _devicePixelsPerDip = 1.0;
    private bool _firstFrameLogged;
    private bool _videoGeometryLogged;
    private bool _firstVideoFrameDumped;
    private bool _contentBoxValid;
    private int _pendingBackgroundColour = -1;
    private VideoQueue? _videoQueue;

    // Big pixel buffers are reused instead of re-allocated. A 4K frame is 33 MB, which lands on the large
    // object heap; allocating a fresh one every frame or every slide is what made the app grow in RAM.
    private byte[] _cropBuffer = Array.Empty<byte>();
    private byte[] _imageBufferA = Array.Empty<byte>();
    private byte[] _imageBufferB = Array.Empty<byte>();
    private bool _imageBufferFlip;

    // Video cost accounting, written by the render thread only and read by the diagnostics.
    private long _videoFrameCount;
    private double _videoDecodeSeconds;
    private double _videoPresentSeconds;
    private string _videoCostDescription = "not measured";
    private int _videoFrameRateLimit = 30;
    private VideoPixelMode _videoPixelMode = VideoPixelMode.DecoderConvertsToRgb;
    private bool _useHardwareDecoding = true;
    private bool _hardwareDecoderActive;
    private string? _resumeVideoPath;
    private int _appliedBackgroundColour = -1;
    private ContentBox _contentBox;
    private long _framesPresented;
    private volatile bool _renderThreadAlive;

    public CompositionSurface(string deviceName, WallpaperScaleMode scaleMode)
    {
        _deviceName = deviceName;
        _scaleMode = scaleMode;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public event EventHandler? DescriptionChanged;

    public string DeviceName
    {
        get { return _deviceName; }
    }

    public string ActiveDescription
    {
        get { return _description; }
    }

    public string AttachFailureReason
    {
        get { return _attachFailure; }
    }

    public string HostDescription
    {
        get { return _hostDescription; }
    }

    public string CreationMode
    {
        get { return "direct composition"; }
    }

    public bool IsAttached
    {
        get { return _attached; }
    }

    public bool IsPaused
    {
        get { return _paused; }
    }

    public bool IsVideoOpen
    {
        get { return _isVideoOpen; }
    }

    /// <summary>
    /// Creates the wallpaper window straight into the desktop host and starts the render thread. The creation
    /// order is irrelevant here (there is no WPF content to reparent), so the "child at creation" and
    /// "reparented popup" fallbacks are the same thing for this renderer.
    /// </summary>
    public bool Attach(DesktopHost host, RECT monitorRectangle, RECT hostRectangle, SurfaceCreationMode creationMode)
    {
        if (_disposed)
        {
            return false;
        }

        if (_attached && _windowHandle != IntPtr.Zero)
        {
            return true;
        }

        try
        {
            if (host.IsEmpty || host.ParentWindow == IntPtr.Zero)
            {
                _attachFailure = "the desktop host window was not resolved";
                return false;
            }

            _windowWidth = monitorRectangle.Width;
            _windowHeight = monitorRectangle.Height;
            int childX = monitorRectangle.Left - hostRectangle.Left;
            int childY = monitorRectangle.Top - hostRectangle.Top;

            _windowHandle = CompositionWindowFactory.Create(host.ParentWindow, childX, childY, _windowWidth, _windowHeight);
            if (_windowHandle == IntPtr.Zero)
            {
                _attachFailure = "CreateWindowEx failed with Win32 error "
                    + System.Runtime.InteropServices.Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                return false;
            }

            // Sit directly below the icon host so the desktop icons stay on top of the wallpaper.
            NativeMethods.SetWindowPos(
                _windowHandle,
                host.InsertAfterWindow,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_SHOWNOACTIVATE);

            _hostDescription = host.Describe();
            int dpi = NativeMethods.GetDpiForWindow(_windowHandle);
            if (dpi <= 0)
            {
                dpi = DefaultDpi;
            }

            // DirectComposition may or may not turn the visual's DIP coordinates into device pixels with the
            // display scaling, and the answer differs per window and build. Take the largest DPI the three
            // sources report (our window, its monitor, the desktop host it is parented into) and divide by it,
            // then log all of them: a 150 % display showing the wallpaper 1.5x too large is exactly the case
            // this compensates for, and the log says which source produced the factor.
            int hostDpi = host.ParentWindow == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(host.ParentWindow);
            int monitorDpi = ReadMonitorDpi(_windowHandle);
            int effectiveDpi = Math.Max(dpi, Math.Max(hostDpi, monitorDpi));
            if (effectiveDpi <= 0)
            {
                effectiveDpi = DefaultDpi;
            }

            double devicePixelsPerDip;
            switch (_scaleMode)
            {
                case WallpaperScaleMode.Pixels:
                    devicePixelsPerDip = 1.0;
                    break;
                case WallpaperScaleMode.AssumeOneHundredFiftyPercent:
                    devicePixelsPerDip = (double)PixelsPerDipBase / 144.0;
                    break;
                default:
                    devicePixelsPerDip = (double)PixelsPerDipBase / effectiveDpi;
                    break;
            }

            _devicePixelsPerDip = devicePixelsPerDip;

            RECT clientRectangle;
            string clientSize = NativeMethods.GetClientRect(_windowHandle, out clientRectangle)
                ? string.Format(CultureInfo.InvariantCulture, "{0}x{1}", clientRectangle.Width, clientRectangle.Height)
                : "unknown";

            AppLog.Write(string.Format(
                CultureInfo.InvariantCulture,
                "composition surface attached for {0} at {1},{2},{3}x{4} under host 0x{5:X} (window 0x{6:X}, client {7}, dpi window {8} monitor {9} host {10} -> {11}, scale mode {12}, dip scale {13:F3})",
                _deviceName,
                monitorRectangle.Left,
                monitorRectangle.Top,
                _windowWidth,
                _windowHeight,
                host.ParentWindow.ToInt64(),
                _windowHandle.ToInt64(),
                clientSize,
                dpi,
                monitorDpi,
                hostDpi,
                effectiveDpi,
                _scaleMode,
                devicePixelsPerDip));

            int width = _windowWidth;
            int height = _windowHeight;
            _renderThread = new Thread(() => RenderLoop(width, height, devicePixelsPerDip));
            _renderThread.IsBackground = true;
            _renderThread.Name = "LiveWall composition " + _deviceName;
            _renderThread.SetApartmentState(ApartmentState.MTA);
            _renderThread.Start();
            _renderThreadAlive = true;
            _attached = true;
            _attachFailure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            _attachFailure = exception.GetType().Name + ": " + exception.Message;
            AppLog.WriteException("attaching the composition surface failed for " + _deviceName, exception);
            DisposeWindow();
            return false;
        }
    }

    public void ApplySource(WallpaperSource config)
    {
        if (_disposed)
        {
            return;
        }

        _source = config == null ? new WallpaperSource() : config;
        _firstFrameLogged = false;
        StopSlideshow();

        WallpaperStretch stretch = _source.Stretch;
        lock (_sync)
        {
            _stretch = stretch;
        }

        Color background = ColorParser.ParseHexColor(_source.BackgroundColor, Colors.Black);
        _pendingBackgroundColour = (background.R << 16) | (background.G << 8) | background.B;

        _videoQueue = null;
        _cropBuffer = Array.Empty<byte>();
        _videoFrameRateLimit = _source.VideoFrameRateLimit;
        _videoPixelMode = _source.VideoPixelMode;
        _useHardwareDecoding = _source.UseHardwareDecoding;
        lock (_sync)
        {
            _resumeVideoPath = null;
        }

        RequestLargeObjectHeapCompaction();

        switch (_source.Kind)
        {
            case WallpaperKind.Video:
                PushVideoSource();
                break;
            case WallpaperKind.VideoFolder:
                PushVideoFolderSource();
                break;
            case WallpaperKind.Image:
                ShowImageFile(_source.FilePath, stretch);
                break;
            case WallpaperKind.Slideshow:
                StartSlideshow();
                break;
            default:
                ShowSolidColour(_source.BackgroundColor, stretch);
                break;
        }
    }

    /// <summary>
    /// Moves to the next item of a folder source: the next slide for a slideshow, the next clip for a video
    /// folder. A single image or a single video has no "next", so the call does nothing.
    /// </summary>
    public void SkipToNext()
    {
        if (_disposed)
        {
            return;
        }

        if (_slideshowTimer != null && _playlistFiles.Count > 0)
        {
            _playlistIndex = (_playlistIndex + 1) % _playlistFiles.Count;
            ShowCurrentSlide();
            _slideshowTimer.Stop();
            _slideshowTimer.Start();
            return;
        }

        if (_videoQueue == null)
        {
            return;
        }

        string? nextFile = _videoQueue.AdvanceOnSkip();
        if (string.IsNullOrEmpty(nextFile))
        {
            return;
        }

        string description = DescribeQueuedVideo(_videoQueue, nextFile);
        lock (_sync)
        {
            _pendingVideo = new VideoCommand(nextFile, _source.Stretch, description);
            Monitor.PulseAll(_sync);
        }

        SetDescription(description + " (opening…)");
    }

    public void SetPaused(bool paused)
    {
        if (_disposed)
        {
            return;
        }

        if (_paused == paused)
        {
            return;
        }

        _paused = paused;
        lock (_sync)
        {
            Monitor.PulseAll(_sync);
        }
    }

    public string DiagnosticLine()
    {
        string windowHandle = _windowHandle == IntPtr.Zero ? "-" : "0x" + _windowHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} | attached={1} | window={2} | size={3}x{4} | paused={5} | videoOpen={6} | renderer={7} | framesPresented={8} | host={9} | what={10}",
            _deviceName,
            _attached ? "yes" : "no",
            windowHandle,
            _windowWidth,
            _windowHeight,
            _paused ? "yes" : "no",
            _isVideoOpen ? "yes" : "no",
            _rendererDescription,
            _framesPresented,
            _hostDescription,
            _description)
            + " | decoder=" + (_hardwareDecoderActive ? "hardware" : "software")
            + " | video cost=" + _videoCostDescription;
    }

    // ── Source handling (UI thread) ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes the configured background colour to the presenter, which paints it behind the wallpaper. Whatever
    /// a stretch mode leaves uncovered then shows that colour instead of the old Windows wallpaper.
    /// </summary>
    private void ApplyPendingBackgroundColour(CompositionPresenter presenter)
    {
        int colour = _pendingBackgroundColour;
        if (colour < 0 || colour == _appliedBackgroundColour)
        {
            return;
        }

        _appliedBackgroundColour = colour;
        presenter.SetBackgroundColor(
            (byte)((colour >> 16) & 0xFF),
            (byte)((colour >> 8) & 0xFF),
            (byte)(colour & 0xFF));
    }

    private void PushVideoSource()
    {
        string filePath = _source.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            ShowSolidColour(_source.BackgroundColor, _source.Stretch);
            SetDescription("video (no file selected)");
            return;
        }

        lock (_sync)
        {
            _pendingFrame = null;
            _resumeVideoPath = null;
            _pendingVideo = new VideoCommand(filePath, _source.Stretch, "video " + Path.GetFileName(filePath));
            Monitor.PulseAll(_sync);
        }

        SetDescription("video " + Path.GetFileName(filePath) + " (opening…)");
    }

    /// <summary>
    /// Starts a folder of videos. The queue decides the order, and the same queue keeps handing out the next
    /// file when one ends, so a folder behaves like a playlist instead of a single looping clip.
    /// </summary>
    private void PushVideoFolderSource()
    {
        VideoQueue queue = new VideoQueue(_source);
        _videoQueue = queue;

        string? firstFile = queue.Current;
        if (string.IsNullOrEmpty(firstFile))
        {
            ShowSolidColour(_source.BackgroundColor, _source.Stretch);
            SetDescription("video folder (no videos found in " + _source.FolderPath + ")");
            return;
        }

        lock (_sync)
        {
            _pendingFrame = null;
            _resumeVideoPath = null;
            _pendingVideo = new VideoCommand(firstFile, _source.Stretch, DescribeQueuedVideo(queue, firstFile));
            Monitor.PulseAll(_sync);
        }

        SetDescription(DescribeQueuedVideo(queue, firstFile) + " (opening…)");
    }

    private static string DescribeQueuedVideo(VideoQueue queue, string filePath)
    {
        string name = Path.GetFileName(filePath);
        if (queue.RepeatsSingleFile)
        {
            return "video " + name + " (random pick, looped)";
        }

        if (queue.Count <= 1)
        {
            return "video " + name;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "video {0} ({1} of {2})",
            name,
            queue.Position,
            queue.Count);
    }

    private void ShowImageFile(string filePath, WallpaperStretch stretch)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            ShowSolidColour(_source.BackgroundColor, stretch);
            SetDescription("image (no file selected)");
            return;
        }

        byte[]? pixels;
        int width;
        int height;
        string failure;
        if (TryLoadImagePixels(filePath, out pixels, out width, out height, out failure) && pixels != null)
        {
            PushFrame(pixels, width, height, stretch, "image " + Path.GetFileName(filePath));
            return;
        }

        AppLog.Write("could not decode the wallpaper image " + filePath + ": " + failure);
        ShowSolidColour(_source.BackgroundColor, stretch);
        SetDescription("image " + Path.GetFileName(filePath) + " could not be read: " + failure);
    }

    private void ShowSolidColour(string colorHex, WallpaperStretch stretch)
    {
        Color color = ColorParser.ParseHexColor(colorHex, Colors.Black);
        const int width = 8;
        const int height = 8;
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = 255;
        }

        PushFrame(pixels, width, height, stretch, "solid colour " + colorHex);
    }

    private void PushFrame(byte[] pixels, int width, int height, WallpaperStretch stretch, string description)
    {
        lock (_sync)
        {
            _pendingVideo = null;
            _pendingFrame = new FrameCommand(pixels, width, height, stretch, description);
            Monitor.PulseAll(_sync);
        }

        SetDescription(description);
    }

    private void StartSlideshow()
    {
        _playlistFiles = SlideshowPlaylist.Collect(_source);
        if (_playlistFiles.Count == 0)
        {
            ShowSolidColour(_source.BackgroundColor, _source.Stretch);
            SetDescription("slideshow (no images found in " + _source.FolderPath + ")");
            return;
        }

        if (_source.Shuffle)
        {
            SlideshowPlaylist.Shuffle(_playlistFiles, _shuffleRandom);
        }

        _playlistIndex = 0;
        int intervalSeconds = _source.SlideshowIntervalSeconds;
        if (intervalSeconds < 1)
        {
            intervalSeconds = 1;
        }

        _slideshowTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _slideshowTimer.Interval = TimeSpan.FromSeconds(intervalSeconds);
        _slideshowTimer.Tick += SlideshowTimer_Tick;
        _slideshowTimer.Start();
        ShowCurrentSlide();
    }

    private void StopSlideshow()
    {
        if (_slideshowTimer != null)
        {
            _slideshowTimer.Stop();
            _slideshowTimer.Tick -= SlideshowTimer_Tick;
            _slideshowTimer = null;
        }
    }

    private void SlideshowTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || _playlistFiles.Count == 0)
        {
            return;
        }

        _playlistIndex = (_playlistIndex + 1) % _playlistFiles.Count;
        ShowCurrentSlide();

        // The slide that just left the screen left a picture-sized hole on the large object heap.
        RequestLargeObjectHeapCompaction();
    }

    private void ShowCurrentSlide()
    {
        if (_playlistFiles.Count == 0)
        {
            return;
        }

        string filePath = _playlistFiles[_playlistIndex];
        byte[]? pixels;
        int width;
        int height;
        string failure;
        if (TryLoadImagePixels(filePath, out pixels, out width, out height, out failure) && pixels != null)
        {
            string description = string.Format(
                CultureInfo.InvariantCulture,
                "slideshow {0} ({1} of {2}, every {3}s)",
                Path.GetFileName(filePath),
                _playlistIndex + 1,
                _playlistFiles.Count,
                _source.SlideshowIntervalSeconds);
            PushFrame(pixels, width, height, _source.Stretch, description);
            return;
        }

        AppLog.Write("could not decode a slideshow image " + filePath + ": " + failure);
    }

    private bool TryLoadImagePixels(string filePath, out byte[]? pixels, out int width, out int height, out string failure)
    {
        pixels = null;
        width = 0;
        height = 0;
        failure = string.Empty;

        try
        {
            if (!File.Exists(filePath))
            {
                failure = "the file does not exist";
                return false;
            }

            BitmapImage image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.UriSource = new Uri(filePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            // A format conversion holds a second full copy of the picture, so it is only created when the
            // decoder really did hand back something other than BGRA.
            BitmapSource source = image;
            if (image.Format != PixelFormats.Bgra32)
            {
                FormatConvertedBitmap converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                source = converted;
            }

            width = source.PixelWidth;
            height = source.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                failure = "the image has no pixels";
                return false;
            }

            int stride = width * 4;
            byte[] buffer = RentImageBuffer(stride * height);
            source.CopyPixels(buffer, stride, 0);
            pixels = buffer;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Hands out one of two reusable image buffers, grown only when a picture needs more room. Two buffers are
    /// kept so the array the render thread is still presenting is never overwritten while the next slide is
    /// decoded.
    /// </summary>
    private byte[] RentImageBuffer(int length)
    {
        _imageBufferFlip = !_imageBufferFlip;
        byte[] buffer = _imageBufferFlip ? _imageBufferA : _imageBufferB;
        if (buffer.Length < length)
        {
            buffer = new byte[length];
        }

        if (_imageBufferFlip)
        {
            _imageBufferA = buffer;
        }
        else
        {
            _imageBufferB = buffer;
        }

        return buffer;
    }

    /// <summary>
    /// Asks the next garbage collection to compact the large object heap, which is what gives a 33 MB picture
    /// back to the operating system after the wallpaper moved on. The collection itself is left to the runtime.
    /// </summary>
    private static void RequestLargeObjectHeapCompaction()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        }
        catch (Exception)
        {
            // A platform that does not support it is not a reason to fail.
        }
    }

    private void SetDescription(string description)
    {
        if (string.Equals(_description, description, StringComparison.Ordinal))
        {
            return;
        }

        _description = description;
        RaiseDescriptionChanged();
    }

    private void RaiseDescriptionChanged()
    {
        if (_dispatcher.CheckAccess())
        {
            DescriptionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _dispatcher.BeginInvoke(new Action(() => DescriptionChanged?.Invoke(this, EventArgs.Empty)));
    }

    // ── Render thread ─────────────────────────────────────────────────────────────────────────────────────

    private void RenderLoop(int windowWidth, int windowHeight, double devicePixelsPerDip)
    {
        CompositionPresenter? presenter = null;
        MediaFoundationVideo? video = null;
        long firstSampleTime = -1;
        long playbackStartTimestamp = 0;
        long lastPresentTimestamp = 0;
        long pausedSinceTimestamp = 0;
        string? currentVideoPath = null;

        try
        {
            presenter = new CompositionPresenter();
            presenter.Initialize(_windowHandle, windowWidth, windowHeight, devicePixelsPerDip);
            _rendererDescription = presenter.DeviceDescription;
            AppLog.Write("composition renderer ready for " + _deviceName + ": " + presenter.DeviceDescription);

            while (true)
            {
                FrameCommand? frame;
                VideoCommand? videoCommand;
                WallpaperStretch stretch;
                bool paused;
                bool stop;
                lock (_sync)
                {
                    stop = _stopRenderThread;
                    frame = _pendingFrame;
                    videoCommand = _pendingVideo;
                    _pendingFrame = null;
                    _pendingVideo = null;
                    stretch = _stretch;
                    paused = _paused;
                }

                if (stop)
                {
                    break;
                }

                ApplyPendingBackgroundColour(presenter);

                if (videoCommand != null)
                {
                    ReleaseVideo(ref video);
                    if (string.IsNullOrWhiteSpace(videoCommand.FilePath))
                    {
                        SetDescription("video (no file selected)");
                    }
                    else
                    {
                        MediaFoundationVideo candidate = new MediaFoundationVideo();
                        if (candidate.Open(
                            videoCommand.FilePath,
                            _videoPixelMode,
                            presenter.DecoderDevice,
                            _useHardwareDecoding))
                        {
                            video = candidate;
                            _hardwareDecoderActive = candidate.UsesHardwareDecoder;
                            currentVideoPath = videoCommand.FilePath;
                            pausedSinceTimestamp = 0;
                            lastPresentTimestamp = 0;
                            _isVideoOpen = true;
                            _videoGeometryLogged = false;
                            _firstVideoFrameDumped = false;
                            _contentBoxValid = false;
                            _contentBox = default;
                            firstSampleTime = -1;
                            playbackStartTimestamp = 0;
                            presenter.InvalidateTransform();
                            SetDescription(string.Format(
                                CultureInfo.InvariantCulture,
                                "{0} — {1}x{2}, muted",
                                string.IsNullOrEmpty(videoCommand.Description)
                                    ? "video " + Path.GetFileName(videoCommand.FilePath)
                                    : videoCommand.Description,
                                candidate.Width,
                                candidate.Height));
                        }
                        else
                        {
                            candidate.Dispose();
                            SetDescription("video " + Path.GetFileName(videoCommand.FilePath) + " failed to open: " + candidate.FailureReason);
                        }
                    }
                }

                if (!paused && video == null)
                {
                    string? resumePath = null;
                    lock (_sync)
                    {
                        resumePath = _resumeVideoPath;
                        _resumeVideoPath = null;
                    }

                    if (!string.IsNullOrEmpty(resumePath))
                    {
                        lock (_sync)
                        {
                            _pendingVideo = new VideoCommand(resumePath, stretch, "video " + Path.GetFileName(resumePath));
                            Monitor.PulseAll(_sync);
                        }

                        continue;
                    }
                }

                if (frame != null)
                {
                    ReleaseVideo(ref video);
                    try
                    {
                        presenter.ShowPixels(frame.Pixels, frame.Width, frame.Height, frame.Stretch);
                        LogFirstFrame(presenter, frame.Width, frame.Height, frame.Stretch, frame.Description);
                        Interlocked.Increment(ref _framesPresented);
                        SetDescription(frame.Description);
                    }
                    catch (Exception exception)
                    {
                        AppLog.WriteException("presenting a still frame failed for " + _deviceName, exception);
                        SetDescription("presenting failed: " + exception.Message);
                    }
                }

                if (video != null && video.IsOpen)
                {
                    if (paused)
                    {
                        // Paused means paused: the decoder is not pulled, so the last frame stays on screen
                        // and no CPU or GPU time is spent on the video. A long pause also gives the decoder
                        // itself back, because a covered monitor does not need tens of megabytes of reference
                        // frames sitting in RAM; the clip restarts from its beginning when the monitor returns.
                        if (pausedSinceTimestamp == 0)
                        {
                            pausedSinceTimestamp = Stopwatch.GetTimestamp();
                        }
                        else if ((Stopwatch.GetTimestamp() - pausedSinceTimestamp) / (double)Stopwatch.Frequency
                            >= PausedDecoderReleaseSeconds)
                        {
                            lock (_sync)
                            {
                                _resumeVideoPath = currentVideoPath;
                            }

                            ReleaseVideo(ref video);
                            currentVideoPath = null;
                            pausedSinceTimestamp = 0;
                            SetDescription(_description + " (decoder released while paused)");
                        }

                        Thread.Sleep(30);
                        continue;
                    }

                    pausedSinceTimestamp = 0;

                    long decodeStart = Stopwatch.GetTimestamp();
                    byte[]? pixels = video.ReadFrame(out long sampleTime);
                    double decodeSeconds = (Stopwatch.GetTimestamp() - decodeStart) / (double)Stopwatch.Frequency;
                    if (pixels == null)
                    {
                        if (sampleTime < 0)
                        {
                            // End of stream. A folder queue decides what plays next; a single file (and the
                            // single-random-loop mode, which deliberately stays on one file) rewinds instead.
                            firstSampleTime = -1;
                            playbackStartTimestamp = 0;
                            string? nextFile = _videoQueue == null ? null : _videoQueue.Advance();
                            if (!string.IsNullOrEmpty(nextFile))
                            {
                                lock (_sync)
                                {
                                    _pendingVideo = new VideoCommand(
                                        nextFile,
                                        stretch,
                                        DescribeQueuedVideo(_videoQueue, nextFile));
                                    Monitor.PulseAll(_sync);
                                }

                                continue;
                            }

                            if (!video.Rewind())
                            {
                                ReleaseVideo(ref video);
                            }
                        }
                        else
                        {
                            Thread.Sleep(20);
                        }

                        continue;
                    }

                    if (firstSampleTime < 0)
                    {
                        firstSampleTime = sampleTime;
                        playbackStartTimestamp = Stopwatch.GetTimestamp();
                    }
                    else
                    {
                        WaitForPlaybackTiming(sampleTime, firstSampleTime, playbackStartTimestamp);
                    }

                    try
                    {
                        byte[] presented = pixels;
                        int presentedWidth = video.Width;
                        int presentedHeight = video.Height;

                        if (!_contentBoxValid && TryMeasureContentBox(pixels, video.Width, video.Height, out ContentBox measured))
                        {
                            _contentBox = measured;
                            _contentBoxValid = true;
                            ReportContentBox(video, measured);
                        }

                        if (_contentBoxValid && _contentBox.IsCrop)
                        {
                            presentedWidth = _contentBox.Width;
                            presentedHeight = _contentBox.Height;
                            presented = CropToBox(pixels, video.Width, _contentBox);
                        }

                        // Every frame has to be decoded to keep the stream in order, but pushing all of them to
                        // the screen is optional: a wallpaper at 30 fps looks the same as one at 60 and costs
                        // half the uploads.
                        bool presentThisFrame = true;
                        if (_videoFrameRateLimit > 0 && lastPresentTimestamp != 0)
                        {
                            double sinceLastPresent = (Stopwatch.GetTimestamp() - lastPresentTimestamp)
                                / (double)Stopwatch.Frequency;
                            presentThisFrame = sinceLastPresent >= (1.0 / _videoFrameRateLimit);
                        }

                        if (presentThisFrame)
                        {
                            long presentStart = Stopwatch.GetTimestamp();
                            presenter.ShowPixels(presented, presentedWidth, presentedHeight, stretch);
                            lastPresentTimestamp = Stopwatch.GetTimestamp();
                            _videoPresentSeconds += (lastPresentTimestamp - presentStart) / (double)Stopwatch.Frequency;
                            LogFirstFrame(presenter, presentedWidth, presentedHeight, stretch, _description);
                            LogVideoGeometry(video);
                            DumpFirstVideoFrame(pixels, video.Width, video.Height);
                            Interlocked.Increment(ref _framesPresented);
                        }

                        _videoDecodeSeconds += decodeSeconds;
                        _videoFrameCount++;
                        if ((_videoFrameCount % 60) == 0)
                        {
                            SummarizeVideoCost();
                        }
                    }
                    catch (Exception exception)
                    {
                        AppLog.WriteException("presenting a video frame failed for " + _deviceName, exception);
                        ReleaseVideo(ref video);
                        SetDescription("presenting the video failed: " + exception.Message);
                    }

                    continue;
                }

                lock (_sync)
                {
                    if (_pendingFrame == null && _pendingVideo == null && !_stopRenderThread)
                    {
                        Monitor.Wait(_sync, 250);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.WriteException("the composition render thread failed for " + _deviceName, exception);
            SetDescription("renderer failed: " + exception.Message);
        }
        finally
        {
            _renderThreadAlive = false;
            if (video != null)
            {
                video.Dispose();
            }

            if (presenter != null)
            {
                presenter.Dispose();
            }
        }
    }

    /// <summary>Effective DPI of the monitor a window sits on, 0 when the shell cannot tell us.</summary>
    private static int ReadMonitorDpi(IntPtr windowHandle)
    {
        try
        {
            IntPtr monitorHandle = NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitorHandle == IntPtr.Zero)
            {
                return 0;
            }

            if (NativeMethods.GetDpiForMonitor(monitorHandle, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) != 0)
            {
                return 0;
            }

            return (int)dpiX;
        }
        catch (Exception)
        {
            // shcore.dll is present on Windows 8.1 and newer; a missing entry point is not worth a crash.
            return 0;
        }
    }

    /// <summary>
    /// One line per source that records the geometry of the first presented frame: it is the only way to tell a
    /// wrong visual transform from a decoder that reports the wrong frame size, without a Windows machine.
    /// </summary>
    private void LogFirstFrame(CompositionPresenter presenter, int sourceWidth, int sourceHeight, WallpaperStretch stretch, string description)
    {
        if (_firstFrameLogged)
        {
            return;
        }

        _firstFrameLogged = true;
        AppLog.Write(string.Format(
            CultureInfo.InvariantCulture,
            "first frame presented for {0}: {1} — source {2}x{3} -> window {4}x{5} px, dip scale {6:F3}, visual scale {7:F3}x{8:F3} ({9}), {10}",
            _deviceName,
            description,
            sourceWidth,
            sourceHeight,
            _windowWidth,
            _windowHeight,
            _devicePixelsPerDip,
            presenter.LastScaleX,
            presenter.LastScaleY,
            stretch,
            _hostDescription));
    }

    /// <summary>
    /// Logs how the decoder actually lays a frame out. A buffer with fewer rows than the reported frame size is
    /// the one case that leaves a black band at the bottom of the wallpaper, so it must be visible in the log.
    /// </summary>
    private void LogVideoGeometry(MediaFoundationVideo video)
    {
        if (_videoGeometryLogged)
        {
            return;
        }

        _videoGeometryLogged = true;
        AppLog.Write(string.Format(
            CultureInfo.InvariantCulture,
            "video frame geometry for {0}: {1}x{2}, stride {3}, buffer {4} bytes (packed would be {5}), rows copied {6}",
            _deviceName,
            video.Width,
            video.Height,
            video.Stride,
            video.LastBufferLength,
            video.ExpectedBufferLength,
            video.LastRowsCopied));
    }

    /// <summary>
    /// Finds the rectangle that actually holds picture data. A decoder can hand back a buffer whose frame size
    /// says one thing while the image sits in part of it, and that is fatal for the layout: every stretch mode
    /// divides the window by the source size, so a wrong source size zooms, crops or offsets the picture no
    /// matter which mode is chosen. Measured once per video, then reused, because the geometry of a stream does
    /// not change from frame to frame.
    /// </summary>
    private bool TryMeasureContentBox(byte[] pixels, int width, int height, out ContentBox box)
    {
        box = default;

        int rowBytes = width * 4;
        if (rowBytes <= 0 || height < 16 || pixels.Length < rowBytes * height)
        {
            return false;
        }

        int maximumTrimRows = Math.Max(1, (height * 45) / 100);
        int top = 0;
        while (top < maximumTrimRows && IsBlackRow(pixels, rowBytes, width, top))
        {
            top++;
        }

        int bottom = height;
        while (((height - bottom) < maximumTrimRows) && ((bottom - top) > 16) && IsBlackRow(pixels, rowBytes, width, bottom - 1))
        {
            bottom--;
        }

        int maximumTrimColumns = Math.Max(1, (width * 45) / 100);
        int left = 0;
        while (left < maximumTrimColumns && IsBlackColumn(pixels, rowBytes, width, left, top, bottom))
        {
            left++;
        }

        int right = width;
        while (((width - right) < maximumTrimColumns) && ((right - left) > 16) && IsBlackColumn(pixels, rowBytes, width, right - 1, top, bottom))
        {
            right--;
        }

        int boxWidth = right - left;
        int boxHeight = bottom - top;
        if (boxWidth < 16 || boxHeight < 16)
        {
            return false;
        }

        // A nearly-empty measurement is a black frame, not a layout: ignore it and try again next frame.
        if ((long)boxWidth * boxHeight * 4L < (long)width * height * 4L / 4L)
        {
            return false;
        }

        box = new ContentBox(left, top, boxWidth, boxHeight, width, height);
        return true;
    }

    /// <summary>True when the sampled pixels of a row are all black, which is what a letterbox band looks like.</summary>
    private static bool IsBlackRow(byte[] pixels, int rowBytes, int width, int rowIndex)
    {
        int offset = rowIndex * rowBytes;
        int last = offset + (width * 4) - 4;
        for (int index = offset; index <= last; index += 16)
        {
            if (pixels[index] > 8 || pixels[index + 1] > 8 || pixels[index + 2] > 8)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the sampled pixels of a column are all black between two rows.</summary>
    private static bool IsBlackColumn(byte[] pixels, int rowBytes, int width, int column, int top, int bottom)
    {
        if (column < 0 || column >= width)
        {
            return true;
        }

        int rowStep = Math.Max(1, (bottom - top) / 64);
        for (int row = top; row < bottom; row += rowStep)
        {
            int index = (row * rowBytes) + (column * 4);
            if (pixels[index] > 8 || pixels[index + 1] > 8 || pixels[index + 2] > 8)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Copies the given rectangle of a frame into the reusable crop buffer. Reusing it matters: this runs for
    /// every single frame of a letterboxed video, and a fresh 33 MB array per frame is exactly the kind of
    /// garbage that makes a wallpaper app grow to hundreds of megabytes.
    /// </summary>
    private byte[] CropToBox(byte[] pixels, int width, ContentBox box)
    {
        int sourceRowBytes = width * 4;
        int targetRowBytes = box.Width * 4;
        int length = box.Height * targetRowBytes;
        if (_cropBuffer.Length < length)
        {
            _cropBuffer = new byte[length];
        }

        for (int row = 0; row < box.Height; row++)
        {
            int sourceOffset = ((box.Top + row) * sourceRowBytes) + (box.Left * 4);
            Buffer.BlockCopy(pixels, sourceOffset, _cropBuffer, row * targetRowBytes, targetRowBytes);
        }

        return _cropBuffer;
    }

    private void ReportContentBox(MediaFoundationVideo video, ContentBox box)
    {
        if (!box.IsCrop)
        {
            AppLog.Write(string.Format(
                CultureInfo.InvariantCulture,
                "video content box for {0}: the picture fills the whole {1}x{2} frame",
                _deviceName,
                box.FrameWidth,
                box.FrameHeight));
            return;
        }

        AppLog.Write(string.Format(
            CultureInfo.InvariantCulture,
            "video content box for {0}: picture is {1}x{2} at {3},{4} inside a {5}x{6} frame — drawing the picture only",
            _deviceName,
            box.Width,
            box.Height,
            box.Left,
            box.Top,
            box.FrameWidth,
            box.FrameHeight));
    }

    /// <summary>
    /// Writes the first decoded frame of a video to %LocalAppData%\LiveWall\first-video-frame.png. When a
    /// video looks wrong on screen this file shows what the decoder actually produced, which separates a
    /// decoding problem from a presentation problem instead of guessing at it.
    /// </summary>
    private void DumpFirstVideoFrame(byte[] pixels, int width, int height)
    {
        if (_firstVideoFrameDumped)
        {
            return;
        }

        _firstVideoFrameDumped = true;

        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveWall");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "first-video-frame.png");
            // Bgr32, not Bgra32: a decoder's RGB32 frame leaves the fourth byte undefined, so an alpha-aware
            // encoder wrote a fully transparent PNG that showed up as a black rectangle in every viewer.
            BitmapSource frame = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgr32,
                null,
                pixels,
                width * 4);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using (FileStream stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            AppLog.Write("first video frame written to " + path);
        }
        catch (Exception exception)
        {
            AppLog.WriteException("writing the first video frame failed", exception);
        }
    }

    /// <summary>Sleeps until the next sample is due, so a video plays at its own speed instead of decoding flat out.</summary>
    /// <summary>
    /// Turns the per-frame stopwatch sums into one readable line: how much of each frame's time went into
    /// decoding and how much into getting it onto the screen. Without it "it uses a lot of CPU" has no answer.
    /// </summary>
    private void SummarizeVideoCost()
    {
        long frames = _videoFrameCount;
        if (frames <= 0)
        {
            _videoCostDescription = "not measured";
            return;
        }

        double decodeMilliseconds = _videoDecodeSeconds * 1000.0 / frames;
        double presentMilliseconds = _videoPresentSeconds * 1000.0 / frames;
        double frameMilliseconds = decodeMilliseconds + presentMilliseconds;
        double framesPerSecond = frameMilliseconds > 0 ? 1000.0 / frameMilliseconds : 0.0;
        _videoCostDescription = string.Format(
            CultureInfo.InvariantCulture,
            "{0:F1} fps, decode {1:F1} ms + present {2:F1} ms per frame = {3:F1}% of one core of wall time",
            framesPerSecond,
            decodeMilliseconds,
            presentMilliseconds,
            frameMilliseconds * framesPerSecond / 10.0);
    }

    private static void WaitForPlaybackTiming(long sampleTime, long firstSampleTime, long playbackStartTimestamp)
    {
        double dueSeconds = (sampleTime - firstSampleTime) / 10000000.0;
        double elapsedSeconds = (Stopwatch.GetTimestamp() - playbackStartTimestamp) / (double)Stopwatch.Frequency;
        double remainingSeconds = dueSeconds - elapsedSeconds;
        if (remainingSeconds > 0.002)
        {
            int sleepMilliseconds = (int)Math.Min(1000.0, remainingSeconds * 1000.0);
            if (sleepMilliseconds > 0)
            {
                Thread.Sleep(sleepMilliseconds);
            }
        }
    }

    private void ReleaseVideo(ref MediaFoundationVideo? video)
    {
        if (video == null)
        {
            return;
        }

        video.Dispose();
        video = null;
        _isVideoOpen = false;
        _hardwareDecoderActive = false;
    }

    // ── Teardown ──────────────────────────────────────────────────────────────────────────────────────────

    private void DisposeWindow()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
        }

        _attached = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopSlideshow();

        lock (_sync)
        {
            _stopRenderThread = true;
            _pendingFrame = null;
            _pendingVideo = null;
            Monitor.PulseAll(_sync);
        }

        Thread? renderThread = _renderThread;
        _renderThread = null;
        if (renderThread != null && renderThread.IsAlive)
        {
            if (!renderThread.Join(RenderThreadJoinMilliseconds))
            {
                AppLog.Write("the composition render thread for " + _deviceName + " did not stop in time");
            }
        }

        _renderThreadAlive = false;
        _cropBuffer = Array.Empty<byte>();
        _imageBufferA = Array.Empty<byte>();
        _imageBufferB = Array.Empty<byte>();
        DisposeWindow();
    }

    // ── Nested types ──

    /// <summary>
    /// The rectangle of a decoded frame that holds picture instead of black padding, and the frame size it was
    /// measured in.
    /// </summary>
    private readonly struct ContentBox
    {
        public ContentBox(int left, int top, int width, int height, int frameWidth, int frameHeight)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            FrameWidth = frameWidth;
            FrameHeight = frameHeight;
        }

        public int Left { get; }

        public int Top { get; }

        public int Width { get; }

        public int Height { get; }

        public int FrameWidth { get; }

        public int FrameHeight { get; }

        /// <summary>True when the picture is smaller than the frame, so the frame has to be cropped.</summary>
        public bool IsCrop
        {
            get
            {
                return Left > 0 || Top > 0 || Width < FrameWidth || Height < FrameHeight;
            }
        }
    }
}
