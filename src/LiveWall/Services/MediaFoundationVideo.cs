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

    private static readonly object StartupLock = new object();

    private static bool _mediaFoundationStarted;

    private IMFSourceReader? _reader;
    private IMFAttributes? _readerAttributes;
    private byte[] _frameBuffer = Array.Empty<byte>();

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

    public bool Open(string path)
    {
        Close();

        try
        {
            EnsureMediaFoundationStarted();

            _readerAttributes = MediaFactory.MFCreateAttributes(2);
            _readerAttributes.Set(EnableVideoProcessingKey, 1u);

            _reader = MediaFactory.MFCreateSourceReaderFromURL(path, _readerAttributes);
            _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            using IMFAttributes outputAttributes = outputType.QueryInterface<IMFAttributes>();
            outputAttributes.Set(MediaTypeMajorTypeKey, MediaTypeVideoGuid);
            outputAttributes.Set(MediaTypeSubTypeKey, VideoFormatRgb32Guid);
            _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);

            using IMFMediaType currentType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            using IMFAttributes currentAttributes = currentType.QueryInterface<IMFAttributes>();
            ulong packedFrameSize = currentAttributes.GetUInt64(MediaTypeFrameSizeKey);
            Width = (int)(packedFrameSize & 0xFFFFFFFFUL);
            Height = (int)(packedFrameSize >> 32);

            if (Width <= 0 || Height <= 0)
            {
                FailureReason = "the decoder did not report a frame size";
                Close();
                return false;
            }

            Stride = ReadDefaultStride(currentAttributes);
            if (Stride == 0)
            {
                Stride = Width * 4;
            }

            if (Math.Abs(Stride) < Width * 4)
            {
                FailureReason = "the decoder reported an unusable row pitch";
                Close();
                return false;
            }

            _frameBuffer = new byte[Width * Height * 4];
            FailureReason = string.Empty;
            AppLog.Write(string.Format(
                CultureInfo.InvariantCulture,
                "video media type: {0}x{1}, stride {2}{3}, interlace mode {4}",
                Width,
                Height,
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
        Width = 0;
        Height = 0;
        Stride = 0;
    }

    public void Dispose()
    {
        Close();
    }
}
