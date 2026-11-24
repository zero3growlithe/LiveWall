namespace LiveWall.Models;

/// <summary>
/// Who converts the decoded YUV frame into the RGB the desktop needs.
/// </summary>
public enum VideoPixelMode
{
    /// <summary>
    /// Ask Media Foundation for RGB32, which makes it insert a video-processor stage. Predictable, but that
    /// stage is a whole extra full-frame pass over every frame.
    /// </summary>
    DecoderConvertsToRgb = 0,

    /// <summary>
    /// Leave the decoder's own NV12/I420 output alone and convert it in LiveWall. Skips the video-processor
    /// stage, so it costs less CPU per frame; falls back automatically when the decoder offers nothing usable.
    /// </summary>
    ConvertInApp = 1
}
