using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LiveWall.Models;
using LiveWall.Services;

namespace LiveWall.Controls;

/// <summary>
/// Shows what the engine actually sees (desktop topology, per-monitor surface state) and can prove whether
/// the wallpaper reaches the screen by painting a known colour and reading the pixels back.
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private const string TestColour = "#FF00FF";

    private readonly WallpaperEngine _engine;

    public DiagnosticsWindow(WallpaperEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        RefreshReport();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshReport();
    }

    private void RefreshReport()
    {
        ReportTextBox.Text = _engine.BuildDiagnosticsReport();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ReportTextBox.Text ?? string.Empty);
            ResultTextBlock.Text = "Copied to the clipboard.";
        }
        catch (Exception exception)
        {
            AppLog.WriteException("copying the diagnostics report failed", exception);
            ResultTextBlock.Text = "Could not copy: " + exception.Message;
        }
    }

    /// <summary>
    /// Paints magenta on every monitor, waits, then samples the screen. "Not visible" here means the shell is
    /// not composing our windows at all — no amount of re-attaching fixes that, the drawing path has to change.
    /// </summary>
    private async void TestVisibility_Click(object sender, RoutedEventArgs e)
    {
        TestVisibilityButton.IsEnabled = false;
        ResultTextBlock.Text = "Painting the test colour and reading the screen back…";

        try
        {
            _engine.ApplyTestColour(TestColour);
            await Task.Delay(900);

            List<string> lines = new List<string>();
            bool anyVisible = false;
            IReadOnlyList<MonitorDescriptor> monitors = _engine.Monitors;

            for (int index = 0; index < monitors.Count; index++)
            {
                MonitorDescriptor monitor = monitors[index];
                string detail;
                bool visible = DesktopProbe.Matches(monitor.MonitorRectangle, 255, 0, 255, 30, out detail);
                anyVisible = anyVisible || visible;
                lines.Add("Display " + (monitor.Index + 1).ToString() + ": " + (visible ? "VISIBLE" : "NOT VISIBLE") + " — " + detail);
            }

            _engine.RestoreConfiguredSources();
            await Task.Delay(200);
            RefreshReport();

            if (!anyVisible)
            {
                lines.Add("Nothing reached the screen: the shell is not composing our child windows.");
            }

            ResultTextBlock.Text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception exception)
        {
            AppLog.WriteException("the visibility test failed", exception);
            ResultTextBlock.Text = "The test failed: " + exception.Message;
        }
        finally
        {
            TestVisibilityButton.IsEnabled = true;
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppLog.DataDirectory);
            ProcessStartInfo startInfo = new ProcessStartInfo("explorer.exe", AppLog.DataDirectory);
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            AppLog.WriteException("opening the settings folder failed", exception);
            ResultTextBlock.Text = "Could not open the folder: " + exception.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
