using System;
using System.Globalization;

namespace LiveWall.Models;

/// <summary>
/// One physical monitor, resolved from EnumDisplayMonitors.
/// </summary>
public sealed class MonitorDescriptor
{
    public string DeviceName { get; set; } = string.Empty;

    public IntPtr MonitorHandle { get; set; }

    public RECT MonitorRectangle { get; set; }

    public RECT WorkRectangle { get; set; }

    public bool IsPrimary { get; set; }

    public int Index { get; set; }

    public string DisplayName
    {
        get
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Display {0} — {1}×{2}{3}",
                Index + 1,
                MonitorRectangle.Width,
                MonitorRectangle.Height,
                IsPrimary ? " (primary)" : string.Empty);
        }
    }

    public override string ToString()
    {
        return DisplayName + " [" + DeviceName + "]";
    }
}
