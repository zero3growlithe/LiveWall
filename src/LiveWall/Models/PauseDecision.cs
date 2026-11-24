using System;
using System.Collections.Generic;

namespace LiveWall.Models;

/// <summary>
/// Result of one coverage check: which monitors must pause and why.
/// The reason string is shown in the UI and the tray tooltip, so a paused wallpaper always explains itself.
/// </summary>
public sealed class PauseDecision
{
    public bool PauseAll { get; set; }

    public string PauseAllReason { get; set; } = string.Empty;

    public Dictionary<string, string> MonitorReasons { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty
    {
        get { return !PauseAll && MonitorReasons.Count == 0; }
    }
}
