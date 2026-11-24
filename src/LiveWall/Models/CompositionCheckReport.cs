namespace LiveWall.Models;

/// <summary>
/// Outcome of the "is the wallpaper really on screen?" check: the per-monitor text plus whether anything was
/// seen at all, so the app can decide to try another rendering strategy instead of parsing its own log.
/// </summary>
public sealed class CompositionCheckReport
{
    public CompositionCheckReport(bool anyVisible, bool anyNotVisible, string text)
    {
        AnyVisible = anyVisible;
        AnyNotVisible = anyNotVisible;
        Text = text;
    }

    /// <summary>At least one uncovered monitor showed the test colour.</summary>
    public bool AnyVisible { get; private set; }

    /// <summary>At least one uncovered monitor did NOT show the test colour.</summary>
    public bool AnyNotVisible { get; private set; }

    /// <summary>Per-monitor description, the text stored in the settings and shown in the UI.</summary>
    public string Text { get; private set; }
}
