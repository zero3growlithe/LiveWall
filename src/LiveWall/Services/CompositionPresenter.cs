using System;
using System.Numerics;
using LiveWall.Models;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace LiveWall.Services;

/// <summary>
/// Presents BGRA8 frames on one child window through DirectComposition.
/// <para>
/// This is the whole point of the composition renderer: the visual tree of a window is composed by the
/// desktop compositor, and on Windows 11 24H2+ (build 26100 and newer) that is the <em>only</em> path that
/// lands inside the shell's desktop composition. WPF (hardware or software) and plain GDI blits to a child of
/// the desktop host do not, which is why the wallpaper stayed invisible there.
/// </para>
/// <para>
/// Thread affinity: a D3D11 immediate context is single threaded, so every method here must be called from
/// the thread that called <see cref="Initialize"/>.
/// </para>
/// </summary>
internal sealed class CompositionPresenter : IDisposable
{
    private const int SwapChainBufferCount = 2;
    private const int BytesPerPixel = 4;
    private const int BackgroundSwatchSize = 8;

    /// <summary>
    /// How far the solid-colour swatch is stretched. It has to cover the window whatever mapping the compositor
    /// uses between DIPs and pixels, so it is deliberately absurd: 8 px * 2000 = 16000 DIPs per side.
    /// </summary>
    private const float BackgroundSwatchScale = 2000f;

    private static readonly FeatureLevel[] RequestedFeatureLevels = new FeatureLevel[]
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    };

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIDevice? _dxgiDevice;
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _rootVisual;
    private IDCompositionVisual? _visual;
    private IDCompositionVisual? _backgroundVisual;
    private IDXGISwapChain1? _swapChain;
    private IDXGISwapChain1? _backgroundSwapChain;
    private ID3D11Texture2D? _uploadTexture;
    private byte _backgroundColorRed;
    private byte _backgroundColorGreen;
    private byte _backgroundColorBlue;
    private bool _backgroundColorSet;

    private int _windowWidth;
    private int _windowHeight;
    private double _devicePixelsPerDip = 1.0;
    private int _bufferWidth;
    private int _bufferHeight;
    private WallpaperStretch _stretch = WallpaperStretch.Fill;
    private bool _transformValid;
    private string _deviceDescription = "not created";
    private double _lastScaleX = 1.0;
    private double _lastScaleY = 1.0;

    /// <summary>Which GPU/D3D feature level the presenter ended up with, for the diagnostics report.</summary>
    public string DeviceDescription
    {
        get { return _deviceDescription; }
    }

    public bool IsInitialized
    {
        get { return _compositionDevice != null && _visual != null; }
    }

    /// <summary>True when the colour behind the wallpaper is painted, so unpainted areas are not transparent.</summary>
    public bool HasBackgroundSwatch
    {
        get { return _backgroundSwapChain != null; }
    }

    public int BufferWidth
    {
        get { return _bufferWidth; }
    }

    public int BufferHeight
    {
        get { return _bufferHeight; }
    }

    /// <summary>The scale that was last handed to the visual, for the diagnostics log.</summary>
    public double LastScaleX
    {
        get { return _lastScaleX; }
    }

    public double LastScaleY
    {
        get { return _lastScaleY; }
    }

    /// <summary>
    /// Creates the D3D11 device, the DirectComposition target bound to <paramref name="windowHandle"/> and the
    /// root visual. <paramref name="devicePixelsPerDip"/> is 96/DPI: DirectComposition positions visuals in
    /// DIPs and then scales by the window's DPI, so pixel-exact output needs the reciprocal here.
    /// </summary>
    public void Initialize(IntPtr windowHandle, int windowWidth, int windowHeight, double devicePixelsPerDip)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("the wallpaper window does not exist");
        }

        _windowWidth = windowWidth;
        _windowHeight = windowHeight;
        _devicePixelsPerDip = devicePixelsPerDip <= 0 ? 1.0 : devicePixelsPerDip;

        try
        {
            _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, RequestedFeatureLevels);
            _deviceDescription = "Direct3D 11 hardware";
        }
        catch (Exception hardwareFailure)
        {
            // A wallpaper is not worth a black screen: the WARP software rasteriser can still present through
            // DirectComposition, just with more CPU work.
            AppLog.Write("hardware Direct3D device failed, falling back to WARP: " + hardwareFailure.Message);
            _device = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport, RequestedFeatureLevels);
            _deviceDescription = "Direct3D 11 WARP (software)";
        }

        _context = _device.ImmediateContext;
        _dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
        _compositionDevice.CreateTargetForHwnd(windowHandle, new RawBool(false), out IDCompositionTarget compositionTarget).CheckError();
        _compositionTarget = compositionTarget;
        // Visual tree: root (identity) -> solid colour swatch (below) + the wallpaper content (above).
        // The swatch exists because a wallpaper that does not cover the whole window would otherwise leave the
        // old Windows wallpaper visible around it — which reads as "several different pictures at once".
        _compositionDevice.CreateVisual(out IDCompositionVisual rootVisual).CheckError();
        _rootVisual = rootVisual;
        _compositionTarget.SetRoot(rootVisual);

        _compositionDevice.CreateVisual(out IDCompositionVisual backgroundVisual).CheckError();
        _backgroundVisual = backgroundVisual;
        _backgroundVisual.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear);
        _rootVisual.AddVisual(_backgroundVisual, new RawBool(true), null!);

        _compositionDevice.CreateVisual(out IDCompositionVisual visual).CheckError();
        _visual = visual;
        _visual.SetBitmapInterpolationMode(BitmapInterpolationMode.Linear);
        _rootVisual.AddVisual(_visual, new RawBool(true), _backgroundVisual);

        CreateBackgroundSwatch();
        _compositionDevice.Commit();
    }

    /// <summary>
    /// Paints the colour that shows through wherever the wallpaper does not cover the window. Every stretch mode
    /// except Fill and Stretch leaves area uncovered, and that area used to show whatever wallpaper Windows had
    /// before, which is impossible to tell apart from a broken renderer.
    /// </summary>
    public void SetBackgroundColor(byte red, byte green, byte blue)
    {
        if (_backgroundSwapChain == null || _context == null)
        {
            return;
        }

        if (_backgroundColorSet && _backgroundColorRed == red && _backgroundColorGreen == green && _backgroundColorBlue == blue)
        {
            return;
        }

        try
        {
            int rowBytes = BackgroundSwatchSize * BytesPerPixel;
            byte[] pixels = new byte[BackgroundSwatchSize * rowBytes];
            for (int index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = blue;
                pixels[index + 1] = green;
                pixels[index + 2] = red;
                pixels[index + 3] = 255;
            }

            using ID3D11Texture2D buffer = _backgroundSwapChain.GetBuffer<ID3D11Texture2D>(0);
            _context.UpdateSubresource<byte>(pixels, buffer, 0, (uint)rowBytes, 0, null);
            _backgroundSwapChain.Present(1, PresentFlags.None);

            _backgroundColorRed = red;
            _backgroundColorGreen = green;
            _backgroundColorBlue = blue;
            _backgroundColorSet = true;
        }
        catch (Exception exception)
        {
            AppLog.Write("painting the wallpaper background colour failed: " + exception.Message);
        }
    }

    private void CreateBackgroundSwatch()
    {
        if (_device == null || _dxgiDevice == null || _backgroundVisual == null)
        {
            return;
        }

        SwapChainDescription1 description = new SwapChainDescription1(
            (uint)BackgroundSwatchSize,
            (uint)BackgroundSwatchSize,
            Format.B8G8R8A8_UNorm,
            false,
            Usage.RenderTargetOutput,
            1,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            AlphaMode.Ignore,
            SwapChainFlags.None);

        using IDXGIAdapter adapter = _dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();
        _backgroundSwapChain = factory.CreateSwapChainForComposition(_device, description, null!);
        _backgroundVisual.SetContent(_backgroundSwapChain);
        _backgroundVisual.SetTransform(new Matrix3x2(BackgroundSwatchScale, 0f, 0f, BackgroundSwatchScale, 0f, 0f));
    }

    /// <summary>Resizes the window side of the equation (monitor resolution changed).</summary>
    public void SetWindowSize(int windowWidth, int windowHeight)
    {
        if (_windowWidth == windowWidth && _windowHeight == windowHeight)
        {
            return;
        }

        _windowWidth = windowWidth;
        _windowHeight = windowHeight;
        _transformValid = false;
    }

    /// <summary>
    /// Uploads one BGRA8 frame and presents it. The swapchain is created at the source size and scaled to the
    /// monitor by the visual's transform, so the GPU does the scaling and no shader is needed.
    /// </summary>
    public void ShowPixels(byte[] pixels, int width, int height, WallpaperStretch stretch)
    {
        if (_device == null || _context == null || _visual == null || _compositionDevice == null)
        {
            throw new InvalidOperationException("the composition presenter is not initialized");
        }

        if (pixels == null || width <= 0 || height <= 0)
        {
            return;
        }

        int requiredBytes = width * height * BytesPerPixel;
        if (pixels.Length < requiredBytes)
        {
            throw new ArgumentException("the frame buffer is smaller than the frame size", nameof(pixels));
        }

        EnsureSwapChain(width, height);
        ID3D11Texture2D swapChainTexture = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        try
        {
            _context.UpdateSubresource<byte>(pixels, _uploadTexture!, 0, (uint)(width * BytesPerPixel), 0, null);
            _context.CopyResource(swapChainTexture, _uploadTexture!);
        }
        finally
        {
            swapChainTexture.Dispose();
        }

        if (_stretch != stretch || !_transformValid)
        {
            ApplyTransform(width, height, stretch);
        }

        _swapChain.Present(1, PresentFlags.None);
    }

    /// <summary>Forces the next <see cref="ShowPixels"/> call to recompute the visual transform.</summary>
    public void InvalidateTransform()
    {
        _transformValid = false;
    }

    private void EnsureSwapChain(int width, int height)
    {
        if (_swapChain != null && _uploadTexture != null && _bufferWidth == width && _bufferHeight == height)
        {
            return;
        }

        ReleaseSwapChain();

        SwapChainDescription1 description = new SwapChainDescription1(
            (uint)width,
            (uint)height,
            Format.B8G8R8A8_UNorm,
            false,
            Usage.RenderTargetOutput,
            SwapChainBufferCount,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            AlphaMode.Ignore,
            SwapChainFlags.None);

        using IDXGIAdapter adapter = _dxgiDevice!.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();
        _swapChain = factory.CreateSwapChainForComposition(_device!, description, null!);

        Texture2DDescription textureDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)width,
            (uint)height,
            1,
            1,
            BindFlags.None,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);
        _uploadTexture = _device!.CreateTexture2D(textureDescription);

        _bufferWidth = width;
        _bufferHeight = height;
        _transformValid = false;

        _visual!.SetContent(_swapChain);
        _compositionDevice!.Commit();
    }

    private void ApplyTransform(int sourceWidth, int sourceHeight, WallpaperStretch stretch)
    {
        double scaleX = 1.0;
        double scaleY = 1.0;

        if (_windowWidth > 0 && _windowHeight > 0 && sourceWidth > 0 && sourceHeight > 0)
        {
            double widthRatio = (double)_windowWidth / sourceWidth;
            double heightRatio = (double)_windowHeight / sourceHeight;

            switch (stretch)
            {
                case WallpaperStretch.Fill:
                    double coverScale = Math.Max(widthRatio, heightRatio);
                    scaleX = coverScale;
                    scaleY = coverScale;
                    break;
                case WallpaperStretch.Fit:
                    double containScale = Math.Min(widthRatio, heightRatio);
                    scaleX = containScale;
                    scaleY = containScale;
                    break;
                case WallpaperStretch.Stretch:
                    scaleX = widthRatio;
                    scaleY = heightRatio;
                    break;
                default:
                    scaleX = 1.0;
                    scaleY = 1.0;
                    break;
            }
        }

        double offsetX = (_windowWidth - (sourceWidth * scaleX)) / 2.0;
        double offsetY = (_windowHeight - (sourceHeight * scaleY)) / 2.0;

        // DIPs in, device pixels out: DirectComposition scales the visual by the window DPI, so undo that here.
        Matrix3x2 transform = new Matrix3x2(
            (float)(scaleX * _devicePixelsPerDip),
            0f,
            0f,
            (float)(scaleY * _devicePixelsPerDip),
            (float)(offsetX * _devicePixelsPerDip),
            (float)(offsetY * _devicePixelsPerDip));

        _visual!.SetTransform(transform);
        _compositionDevice!.Commit();
        _stretch = stretch;
        _transformValid = true;
        _lastScaleX = scaleX * _devicePixelsPerDip;
        _lastScaleY = scaleY * _devicePixelsPerDip;
    }

    private void ReleaseSwapChain()
    {
        if (_uploadTexture != null)
        {
            _uploadTexture.Dispose();
            _uploadTexture = null;
        }

        if (_swapChain != null)
        {
            _swapChain.Dispose();
            _swapChain = null;
        }

        _bufferWidth = 0;
        _bufferHeight = 0;
    }

    public void Dispose()
    {
        ReleaseSwapChain();

        if (_compositionDevice != null && (_visual != null || _backgroundVisual != null))
        {
            try
            {
                if (_visual != null)
                {
                    _visual.SetContent(null);
                }

                if (_backgroundVisual != null)
                {
                    _backgroundVisual.SetContent(null);
                }

                _compositionDevice.Commit();
            }
            catch (Exception exception)
            {
                AppLog.Write("clearing the composition visual failed: " + exception.Message);
            }
        }

        if (_visual != null)
        {
            _visual.Dispose();
            _visual = null;
        }

        if (_backgroundSwapChain != null)
        {
            _backgroundSwapChain.Dispose();
            _backgroundSwapChain = null;
        }

        if (_backgroundVisual != null)
        {
            _backgroundVisual.Dispose();
            _backgroundVisual = null;
        }

        if (_rootVisual != null)
        {
            _rootVisual.Dispose();
            _rootVisual = null;
        }

        if (_compositionTarget != null)
        {
            _compositionTarget.Dispose();
            _compositionTarget = null;
        }

        if (_compositionDevice != null)
        {
            _compositionDevice.Dispose();
            _compositionDevice = null;
        }

        if (_dxgiDevice != null)
        {
            _dxgiDevice.Dispose();
            _dxgiDevice = null;
        }

        if (_context != null)
        {
            _context.Dispose();
            _context = null;
        }

        if (_device != null)
        {
            _device.Dispose();
            _device = null;
        }
    }
}
