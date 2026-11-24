namespace LiveWall.Models;

/// <summary>
/// How a folder of videos is played.
/// </summary>
public enum VideoPlaybackOrder
{
    /// <summary>Play the files in name order, one after another, then start again from the first.</summary>
    Sequential = 0,

    /// <summary>Play every file once in a freshly shuffled order, then reshuffle and start again.</summary>
    ShuffledSequence = 1,

    /// <summary>Pick one file at random when the wallpaper starts and loop that single video.</summary>
    SingleRandomLoop = 2
}
