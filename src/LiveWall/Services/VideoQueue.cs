using System;
using System.Collections.Generic;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// The play order of a video folder. One instance per applied source, owned by the render thread that opens
/// the files, so it holds no UI state and needs no locking.
/// </summary>
internal sealed class VideoQueue
{
    private readonly List<string> _files;
    private readonly VideoPlaybackOrder _order;
    private readonly Random _random = new Random();
    private int _index;

    public VideoQueue(WallpaperSource source)
    {
        _files = SlideshowPlaylist.CollectVideos(source);
        _order = source.VideoOrder;

        if (_order != VideoPlaybackOrder.Sequential)
        {
            // Both shuffled modes pick their first file at random; SingleRandomLoop then stays on it.
            SlideshowPlaylist.Shuffle(_files, _random);
        }

        _index = 0;
    }

    public int Count
    {
        get { return _files.Count; }
    }

    /// <summary>1-based position of the current file, for the status line.</summary>
    public int Position
    {
        get { return _files.Count == 0 ? 0 : _index + 1; }
    }

    /// <summary>True when the mode keeps replaying one file, so the caller should rewind instead of reopening.</summary>
    public bool RepeatsSingleFile
    {
        get { return _order == VideoPlaybackOrder.SingleRandomLoop; }
    }

    public string? Current
    {
        get
        {
            if (_files.Count == 0)
            {
                return null;
            }

            return _files[Math.Min(_index, _files.Count - 1)];
        }
    }

    /// <summary>
    /// The next file when the user asks for it by hand. A single-random-loop folder has no natural "next", so
    /// pressing the button draws a new random clip instead.
    /// </summary>
    public string? AdvanceOnSkip()
    {
        if (_files.Count == 0)
        {
            return null;
        }

        if (_order != VideoPlaybackOrder.SingleRandomLoop)
        {
            return Advance();
        }

        string? previous = Current;
        SlideshowPlaylist.Shuffle(_files, _random);
        if (_files.Count > 1 && string.Equals(_files[0], previous, StringComparison.OrdinalIgnoreCase))
        {
            string temporary = _files[0];
            _files[0] = _files[1];
            _files[1] = temporary;
        }

        _index = 0;
        return _files[0];
    }

    /// <summary>
    /// The next file to play, or null when the caller should rewind the current one (single-file loop) or when
    /// the folder holds no videos at all.
    /// </summary>
    public string? Advance()
    {
        if (_files.Count == 0)
        {
            return null;
        }

        if (_order == VideoPlaybackOrder.SingleRandomLoop)
        {
            return null;
        }

        _index++;
        if (_index >= _files.Count)
        {
            _index = 0;
            if (_order == VideoPlaybackOrder.ShuffledSequence)
            {
                string previousFirst = _files[0];
                SlideshowPlaylist.Shuffle(_files, _random);

                // A fresh shuffle must not repeat the file that just finished first.
                if (_files.Count > 1 && string.Equals(_files[0], previousFirst, StringComparison.OrdinalIgnoreCase))
                {
                    string temporary = _files[0];
                    _files[0] = _files[1];
                    _files[1] = temporary;
                }
            }
        }

        return _files[_index];
    }
}
