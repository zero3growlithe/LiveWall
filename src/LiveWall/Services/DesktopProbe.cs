using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// Reads back what is actually on screen. This exists because "the surface is attached, the status says
/// playing, and the desktop is still empty" cannot be told apart from "the shell refuses to compose our
/// window" without looking at real pixels — so the diagnostics panel samples the monitor.
/// </summary>
public static class DesktopProbe
{
    private const int PatchSize = 14;

    private static readonly double[] ColumnFractions = new double[] { 0.25, 0.5, 0.75 };

    private static readonly double[] RowFractions = new double[] { 0.2, 0.45, 0.7 };

    /// <summary>Average colour of nine patches spread over the monitor, as "#RRGGBB" each.</summary>
    public static string Describe(RECT monitorRectangle)
    {
        List<string> samples = new List<string>();
        foreach (SamplePoint point in BuildSamplePoints(monitorRectangle))
        {
            int red;
            int green;
            int blue;
            if (!TryReadAverage(point.X, point.Y, out red, out green, out blue))
            {
                samples.Add("(unreadable)");
                continue;
            }

            samples.Add("#" + red.ToString("X2", CultureInfo.InvariantCulture)
                + green.ToString("X2", CultureInfo.InvariantCulture)
                + blue.ToString("X2", CultureInfo.InvariantCulture));
        }

        return string.Join(" ", samples);
    }

    /// <summary>
    /// True when the monitor shows <paramref name="expected"/> in at least half of the sampled patches,
    /// which means the wallpaper we draw really reaches the screen.
    /// </summary>
    public static bool Matches(RECT monitorRectangle, int expectedRed, int expectedGreen, int expectedBlue, int tolerance, out string detail)
    {
        int matches = 0;
        int readable = 0;
        List<string> observed = new List<string>();

        foreach (SamplePoint point in BuildSamplePoints(monitorRectangle))
        {
            int red;
            int green;
            int blue;
            if (!TryReadAverage(point.X, point.Y, out red, out green, out blue))
            {
                continue;
            }

            readable++;
            observed.Add("#" + red.ToString("X2", CultureInfo.InvariantCulture)
                + green.ToString("X2", CultureInfo.InvariantCulture)
                + blue.ToString("X2", CultureInfo.InvariantCulture));

            if (Math.Abs(red - expectedRed) <= tolerance
                && Math.Abs(green - expectedGreen) <= tolerance
                && Math.Abs(blue - expectedBlue) <= tolerance)
            {
                matches++;
            }
        }

        detail = "sampled " + readable.ToString(CultureInfo.InvariantCulture)
            + " patches, " + matches.ToString(CultureInfo.InvariantCulture)
            + " match the expected colour: " + string.Join(" ", observed);

        return readable > 0 && matches * 2 >= readable;
    }

    private static List<SamplePoint> BuildSamplePoints(RECT monitorRectangle)
    {
        List<SamplePoint> points = new List<SamplePoint>();
        int width = monitorRectangle.Width;
        int height = monitorRectangle.Height;
        if (width <= PatchSize || height <= PatchSize)
        {
            return points;
        }

        for (int row = 0; row < RowFractions.Length; row++)
        {
            for (int column = 0; column < ColumnFractions.Length; column++)
            {
                int x = monitorRectangle.Left + (int)Math.Round(width * ColumnFractions[column]) - (PatchSize / 2);
                int y = monitorRectangle.Top + (int)Math.Round(height * RowFractions[row]) - (PatchSize / 2);
                points.Add(new SamplePoint(x, y));
            }
        }

        return points;
    }

    private static bool TryReadAverage(int screenX, int screenY, out int red, out int green, out int blue)
    {
        red = 0;
        green = 0;
        blue = 0;

        try
        {
            using (Bitmap bitmap = new Bitmap(PatchSize, PatchSize, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(screenX, screenY, 0, 0, new Size(PatchSize, PatchSize), CopyPixelOperation.SourceCopy);
                }

                long totalRed = 0;
                long totalGreen = 0;
                long totalBlue = 0;
                int count = 0;
                for (int y = 0; y < PatchSize; y++)
                {
                    for (int x = 0; x < PatchSize; x++)
                    {
                        Color pixel = bitmap.GetPixel(x, y);
                        totalRed += pixel.R;
                        totalGreen += pixel.G;
                        totalBlue += pixel.B;
                        count++;
                    }
                }

                if (count == 0)
                {
                    return false;
                }

                red = (int)(totalRed / count);
                green = (int)(totalGreen / count);
                blue = (int)(totalBlue / count);
                return true;
            }
        }
        catch (Exception exception)
        {
            AppLog.WriteException("screen sampling failed", exception);
            return false;
        }
    }

    private struct SamplePoint
    {
        public SamplePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; private set; }

        public int Y { get; private set; }
    }
}
