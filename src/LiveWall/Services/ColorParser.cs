using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LiveWall.Services;

/// <summary>
/// Hex colour parsing/formatting, same rules as the SubtitleForge picker: "#RRGGBB" or "#AARRGGBB",
/// with or without the leading '#'.
/// </summary>
public static class ColorParser
{
    public static Color ParseHexColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        string value = text.Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
        {
            value = value.Substring(1);
        }

        if (value.Length == 6)
        {
            value = "FF" + value;
        }

        if (value.Length != 8)
        {
            return fallback;
        }

        uint packed;
        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out packed))
        {
            return fallback;
        }

        return Color.FromArgb(
            (byte)((packed >> 24) & 0xFF),
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF));
    }

    public static string FormatHex(Color color)
    {
        if (color.A == 255)
        {
            return "#" + color.R.ToString("X2", CultureInfo.InvariantCulture)
                + color.G.ToString("X2", CultureInfo.InvariantCulture)
                + color.B.ToString("X2", CultureInfo.InvariantCulture);
        }

        return "#" + color.A.ToString("X2", CultureInfo.InvariantCulture)
            + color.R.ToString("X2", CultureInfo.InvariantCulture)
            + color.G.ToString("X2", CultureInfo.InvariantCulture)
            + color.B.ToString("X2", CultureInfo.InvariantCulture);
    }
}
