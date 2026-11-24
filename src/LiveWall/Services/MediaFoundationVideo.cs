using System;
using System.Globalization;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace LiveWall.Services;

/// <summary>
/// Decodes a video file into BGRA8 frames with a Media Foundation source reader.
/// <para>
/// The source reader is used instead of a player because the wallpaper needs raw pixels: they are uploaded to
/// a Direct3D texture and presented through DirectComposition. Video processing is enabled on the reader so it
/// converts whatever the codec produces into RGB32 for us.
/// </para>
/// <para>
/// Thread affinity: the reader and its frames belong to the thread that called <see cref="Open"/>.
/// </para>
/// </summary>
internal sealed class MediaFoundationVideo : IDisposable
{
    private static readonly Guid MediaTypeMajorTypeKey = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MediaTypeSubTypeKey = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MediaTypeFrameSizeKey = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid EnableVideoProcessingKey = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid MediaTypeDefaultStrideKey = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MediaTypeInterlaceModeKey = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");

    private static readonly Guid MediaTypeVideoGuid = new Guid("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatRgb32Guid = new Guid("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatArgb32Guid = new Guid("00000015-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatNv12Guid = new Guid("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatI420Guid = new Guid("30323449-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatYv12Guid = new Guid("32315659-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// What the decoder actually handed back. Asking for RGB32 does not guarantee RGB32: a decoder without a
    /// colour-conversion MFT keeps producing NV12, and those bytes read as BGRA look like a grey, smeared
    /// picture with black bands and a green fringe where the chroma plane starts.
    /// </summary>
    private enum VideoPixelLayout
    {
        Bgra = 0,
        Nv12 = 1,
        I420 = 2,
        Yv12 = 3,
        Unsupported = 4,
    }

    private static readonly object StartupLock = new object();

    private static bool _mediaFoundationStarted;

    private IMFSourceReader? _reader;
    private IMFAttributes? _readerAttributes;
    private byte[] _frameBuffer = Array.Empty<byte>();
    private byte[] _sourceBuffer = Array.Empty<byte>();
    private VideoPixelLayout _pixelLayout = VideoPixelLayout.Bgra;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>
    /// Row pitch of the decoded frames, as the decoder reports it. It is usually wider than width*4 (rows
    /// are padded to a DWORD/simd alignment) and it can be negative, which means the rows arrive bottom-up.
    /// Uploading a padded frame as if it were tightly packed is what makes a video look sheared and
    /// repeated, so this value is honoured on every frame.
    /// </summary>
    public int Stride { get; private set; }

    /// <summary>Size of the last decoded buffer and how many rows actually came out of it, for the log.</summary>
    public int LastBufferLength { get; private set; }

    public int LastRowsCopied { get; private set; }

    public int ExpectedBufferLength
    {
        get { return Width * Height * 4; }
    }

    public string FailureReason { get; private set; } = string.Empty;

    public bool IsOpen
    {
        get { return _reader != null; }
    }

    /// <summary>Media Foundation has to be started once per process; every surface shares that.</summary>
    public static void EnsureMediaFoundationStarted()
    {
        lock (StartupLock)
        {
            if (_mediaFoundationStarted)
            {
                return;
            }

            MediaFactory.MFStartup().CheckError();
            _mediaFoundationStarted = true;
        }
    }

    /// <summary>
    /// Opens the file and negotiates a pixel format. <see cref="VideoPixelMode.ConvertInApp"/> keeps the
    /// decoder's own YUV output and converts it here, which removes the video-processor stage Media Foundation
    /// would otherwise insert; when the decoder offers nothing LiveWall can convert, the call repeats itself
    /// with the safe RGB request instead of failing.
    /// </summary>
    public bool Open(string path, LiveWall.Models.VideoPixelMode pixelMode)
    {
        if (OpenCore(path, pixelMode))
        {
            return true;
        }

        if (pixelMode == LiveWall.Models.VideoPixelMode.DecoderConvertsToRgb)
        {
            return false;
        }

        AppLog.Write("colour conversion in LiveWall failed for " + path + " (" + FailureReason
            + ") — falling back to the decoder's own RGB conversion");
        return OpenCore(path, LiveWall.Models.VideoPixelMode.DecoderConvertsToRgb);
    }

    private bool OpenCore(string path, LiveWall.Models.VideoPixelMode pixelMode)
    {
        Close();

        try
        {
            EnsureMediaFoundationStarted();

            bool askForRgb = pixelMode == LiveWall.Models.VideoPixelMode.DecoderConvertsToRgb;
            _readerAttributes = MediaFactory.MFCreateAttributes(2);
            _readerAttributes.Set(EnableVideoProcessingKey, askForRgb ? 1u : 0u);

            _reader = MediaFactory.MFCreateSourceReaderFromURL(path, _readerAttributes);
            _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            // The processing flag is read from the reader's own attribute store, not from the store it was
            // created with, so it is set on both.
            try
            {
                using IMFAttributes readerStore = _reader.QueryInterface<IMFAttributes>();
                readerStore.Set(EnableVideoProcessingKey, askForRgb ? 1u : 0u);
            }
            catch (Exception exception)
            {
                AppLog.Write("could not set video processing on the reader: " + exception.Message);
            }

            if (askForRgb)
            {
                // RGB32 makes Media Foundation insert its video-processor stage: one extra full-frame pass
                // over every frame, but a pixel format that needs no work on our side.
                using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
                using IMFAttributes outputAttributes = outputType.QueryInterface<IMFAttributes>();
                outputAttributes.Set(MediaTypeMajorTypeKey, MediaTypeVideoGuid);
                outputAttributes.Set(MediaTypeSubTypeKey, VideoFormatRgb32Guid);
                _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);
            }

            using IMFMediaType currentType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            using IMFAttributes currentAttributes = currentType.QueryInterface<IMFAttributes>();
            // MF_MT_FRAME_SIZE packs the WIDTH in the high 32 bits and the HEIGHT in the low 32 bits — that is
            // exactly what MFGetAttributeSize unpacks. Reading it the other way round transposes every frame:
            // a 1920x1080 video becomes a 1080x1920 texture holding the left half of each row, with black where
            // the rows run out. That single inversion looked like "cropped, stretched 200%, black bar at the
            // bottom, only the left half visible" and sent this renderer through five rounds of false trails.
            ulong packedFrameSize = currentAttributes.GetUInt64(MediaTypeFrameSizeKey);
            Width = (int)(packedFrameSize >> 32);
            Height = (int)(packedFrameSize & 0xFFFFFFFFUL);

            if (Width <= 0 || Height <= 0)
            {
                FailureReason = "the decoder did not report a frame size";
                Close();
                return false;
            }

            _pixelLayout = ReadPixelLayout(currentAttributes);
            if (_pixelLayout == VideoPixelLayout.Unsupported)
            {
                FailureReason = "the decoder produced a pixel format LiveWall cannot show";
                Close();
                return false;
            }

            bool planarOrSemiPlanar = _pixelLayout != VideoPixelLayout.Bgra;
            int minimumStride = planarOrSemiPlanar ? Width : Width * 4;

            Stride = ReadDefaultStride(currentAttributes);
            if (Stride == 0)
            {
                Stride = minimumStride;
            }

            if (Math.Abs(Stride) < minimumStride)
            {
                FailureReason = "the decoder reported an unusable row pitch";
                Close();
                return false;
            }

            _frameBuffer = new byte[Width * Height * 4];
            FailureReason = string.Empty;
            AppLog.Write(string.Format(
                CultureInfo.InvariantCulture,
                "video media type: {0}x{1} (frame size 0x{2:X8}), {3}, stride {4}{5}, interlace mode {6}",
                Width,
                Height,
                packedFrameSize,
                _pixelLayout,
                Stride,
                Stride < 0 ? " (bottom-up)" : string.Empty,
                ReadInterlaceMode(currentAttributes)));
            return true;
        }
        catch (Exception exception)
        {
            FailureReason = exception.Message;
            AppLog.WriteException("opening the video failed: " + path, exception);
            Close();
            return false;
        }
    }

    /// <summary>0 = progressive, 1 = interleaved, 2 = mixed; logged only, deinterlacing is the MFT's job.</summary>
    private static uint ReadInterlaceMode(IMFAttributes attributes)
    {
        try
        {
            return attributes.GetUInt32(MediaTypeInterlaceModeKey);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Pixel format of the negotiation result, which is what the buffers really contain.</summary>
    private static VideoPixelLayout ReadPixelLayout(IMFAttributes attributes)
    {
        Guid subtype;
        try
        {
            subtype = attributes.GetGUID(MediaTypeSubTypeKey);
        }
        catch (Exception)
        {
            return VideoPixelLayout.Bgra;
        }

        if (subtype == VideoFormatRgb32Guid || subtype == VideoFormatArgb32Guid)
        {
            return VideoPixelLayout.Bgra;
        }

        if (subtype == VideoFormatNv12Guid)
        {
            return VideoPixelLayout.Nv12;
        }

        if (subtype == VideoFormatI420Guid)
        {
            return VideoPixelLayout.I420;
        }

        if (subtype == VideoFormatYv12Guid)
        {
            return VideoPixelLayout.Yv12;
        }

        AppLog.Write("unsupported video pixel format: " + subtype.ToString("D"));
        return VideoPixelLayout.Unsupported;
    }

    /// <summary>Row pitch from the negotiation result; 0 when the decoder does not report one.</summary>
    private static int ReadDefaultStride(IMFAttributes attributes)
    {
        try
        {
            return (int)attributes.GetUInt32(MediaTypeDefaultStrideKey);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Reads the next frame. Returns the shared frame buffer, or null when there is nothing to show;
    /// <paramref name="sampleTime"/> is then -1 for end of stream and 0 for a recoverable error.
    /// </summary>
    public byte[]? ReadFrame(out long sampleTime)
    {
        sampleTime = 0;
        IMFSourceReader? reader = _reader;
        if (reader == null)
        {
            return null;
        }

        try
        {
            using IMFSample? sample = reader.ReadSample(
                SourceReaderIndex.FirstVideoStream,
                (SourceReaderControlFlag)0,
                out int actualStreamIndex,
                out SourceReaderFlag flags,
                out long time);

            if ((flags & SourceReaderFlag.Error) != 0)
            {
                FailureReason = "the decoder reported an error";
                return null;
            }

            if ((flags & SourceReaderFlag.EndOfStream) != 0 || sample == null)
            {
                sampleTime = -1;
                return null;
            }

            using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out IntPtr pointer, out int maximumLength, out int currentLength);
            try
            {
                if (currentLength <= 0)
                {
                    return null;
                }

                CopyFrame(pointer, currentLength);
            }
            finally
            {
                buffer.Unlock();
            }

            sampleTime = time;
            return _frameBuffer;
        }
        catch (Exception exception)
        {
            FailureReason = exception.Message;
            AppLog.WriteException("reading a video frame failed", exception);
            return null;
        }
    }

    /// <summary>
    /// Copies one decoded frame into the packed BGRA buffer used for uploading, honouring the row pitch and
    /// the row order. A padded stride copied as if it were width*4 shears the picture diagonally (it looks
    /// "warped / repeated / interlaced"), and a negative stride delivers the image upside down.
    /// </summary>
    private void CopyFrame(IntPtr pointer, int currentLength)
    {
        if (_pixelLayout != VideoPixelLayout.Bgra)
        {
            ConvertYuvFrame(pointer, currentLength);
            return;
        }

        int packedStride = Width * 4;
        int absoluteStride = Math.Abs(Stride);

        if (Stride == packedStride && currentLength >= _frameBuffer.Length)
        {
            LastBufferLength = currentLength;
            LastRowsCopied = Height;
            Marshal.Copy(pointer, _frameBuffer, 0, _frameBuffer.Length);
            return;
        }

        // Padded, or bottom-up: one row at a time, so both problems disappear.
        int copiedBytes = Math.Min(absoluteStride, packedStride);
        int availableRows = Math.Max(0, currentLength / Math.Max(1, absoluteStride));
        int rowCount = Math.Min(Height, availableRows);
        LastBufferLength = currentLength;
        LastRowsCopied = rowCount;

        for (int row = 0; row < rowCount; row++)
        {
            int sourceRow = Stride < 0 ? (Height - 1 - row) : row;
            IntPtr source = IntPtr.Add(pointer, sourceRow * absoluteStride);
            Marshal.Copy(source, _frameBuffer, row * packedStride, copiedBytes);
        }
    }

    /// <summary>
    /// Converts a decoded YUV frame into the packed BGRA buffer. NV12 and I420/YV12 are the formats a decoder
    /// falls back to when no colour converter is in the graph, and they are 1.5 bytes per pixel, so reading
    /// them as BGRA produces a grey smear, a black band and a green fringe — exactly the symptoms that made
    /// the wallpaper look broken rather than the geometry.
    /// </summary>
    private void ConvertYuvFrame(IntPtr pointer, int currentLength)
    {
        int stride = Math.Abs(Stride);
        if (stride < Width)
        {
            stride = Width;
        }

        if (_sourceBuffer.Length < currentLength)
        {
            _sourceBuffer = new byte[currentLength];
        }

        Marshal.Copy(pointer, _sourceBuffer, 0, currentLength);
        LastBufferLength = currentLength;

        int rows = Math.Min(Height, currentLength / Math.Max(1, stride));
        LastRowsCopied = rows;

        // BT.709 for HD content, BT.601 for anything smaller; the difference is visible on skin tones.
        bool highDefinition = Height >= 720;
        int redFromV = highDefinition ? 1836 : 1634;
        int greenFromU = highDefinition ? -218 : -401;
        int greenFromV = highDefinition ? -546 : -833;
        int blueFromU = highDefinition ? 2165 : 2066;

        int lumaPlaneLength = stride * Height;
        int chromaRowStride = _pixelLayout == VideoPixelLayout.Nv12 ? stride : Math.Max(2, stride / 2);
        int chromaRows = (Height + 1) / 2;
        int chromaPlaneLength = chromaRowStride * chromaRows;
        int uPlaneOffset = lumaPlaneLength;
        int vPlaneOffset = lumaPlaneLength;
        if (_pixelLayout == VideoPixelLayout.I420)
        {
            vPlaneOffset = lumaPlaneLength + chromaPlaneLength;
        }
        else if (_pixelLayout == VideoPixelLayout.Yv12)
        {
            vPlaneOffset = lumaPlaneLength;
            uPlaneOffset = lumaPlaneLength + chromaPlaneLength;
        }

        bool halfPlanar = _pixelLayout == VideoPixelLayout.Nv12;
        int writeOffset = 0;

        for (int row = 0; row < rows; row++)
        {
            int lumaRowOffset = row * stride;
            int chromaRowOffset = (row / 2) * chromaRowStride;

            for (int column = 0; column < Width; column++)
            {
                int lumaIndex = lumaRowOffset + column;
                int luma = lumaIndex < currentLength ? _sourceBuffer[lumaIndex] : (byte)16;

                int chromaColumn = column / 2;
                int u;
                int v;
                if (halfPlanar)
                {
                    int pairIndex = lumaPlaneLength + chromaRowOffset + (chromaColumn * 2);
                    u = pairIndex < currentLength ? _sourceBuffer[pairIndex] : 128;
                    v = (pairIndex + 1) < currentLength ? _sourceBuffer[pairIndex + 1] : 128;
                }
                else
                {
                    int uIndex = uPlaneOffset + chromaRowOffset + chromaColumn;
                    int vIndex = vPlaneOffset + chromaRowOffset + chromaColumn;
                    u = uIndex < currentLength ? _sourceBuffer[uIndex] : 128;
                    v = vIndex < currentLength ? _sourceBuffer[vIndex] : 128;
                }

                WriteYuvPixel(
                    _frameBuffer,
                    writeOffset,
                    luma,
                    u,
                    v,
                    redFromV,
                    greenFromU,
                    greenFromV,
                    blueFromU);
                writeOffset += 4;
            }
        }

        if (writeOffset < _frameBuffer.Length)
        {
            Array.Clear(_frameBuffer, writeOffset, _frameBuffer.Length - writeOffset);
        }
    }

    /// <summary>One limited-range YUV pixel to BGRA, in fixed point: the coefficients are scaled by 1024.</summary>
    private static void WriteYuvPixel(
        byte[] target,
        int offset,
        int luma,
        int u,
        int v,
        int redFromV,
        int greenFromU,
        int greenFromV,
        int blueFromU)
    {
        int scaledLuma = luma > 16 ? (luma - 16) * 1192 : 0;
        int centredU = u - 128;
        int centredV = v - 128;

        int red = (scaledLuma + (redFromV * centredV)) >> 10;
        int green = (scaledLuma + (greenFromU * centredU) + (greenFromV * centredV)) >> 10;
        int blue = (scaledLuma + (blueFromU * centredU)) >> 10;

        target[offset] = ClampToByte(blue);
        target[offset + 1] = ClampToByte(green);
        target[offset + 2] = ClampToByte(red);
        target[offset + 3] = 255;
    }

    private static byte ClampToByte(int value)
    {
        if (value < 0)
        {
            return 0;
        }

        return value > 255 ? (byte)255 : (byte)value;
    }

    /// <summary>Jumps back to the start, for looping.</summary>
    public bool Rewind()
    {
        IMFSourceReader? reader = _reader;
        if (reader == null)
        {
            return false;
        }

        try
        {
            reader.SetCurrentPosition(0);
            return true;
        }
        catch (Exception exception)
        {
            FailureReason = exception.Message;
            AppLog.WriteException("rewinding the video failed", exception);
            return false;
        }
    }

    public void Close()
    {
        if (_reader != null)
        {
            _reader.Dispose();
            _reader = null;
        }

        if (_readerAttributes != null)
        {
            _readerAttributes.Dispose();
            _readerAttributes = null;
        }

        _frameBuffer = Array.Empty<byte>();
        _sourceBuffer = Array.Empty<byte>();
        Width = 0;
        Height = 0;
        Stride = 0;
    }

    public void Dispose()
    {
        Close();
    }
}
