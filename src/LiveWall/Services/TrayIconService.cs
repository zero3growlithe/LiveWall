using System;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace LiveWall.Services;

/// <summary>
/// Notification-area icon with a small context menu. Kept in its own file so the WinForms
/// and WPF namespaces never meet (they both define Application, MessageBox, ...).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _pauseMenuItem;

    public TrayIconService()
    {
        _notifyIcon = new Forms.NotifyIcon();
        _notifyIcon.Icon = LoadIcon();
        _notifyIcon.Text = "LiveWall";
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

        Forms.ToolStripMenuItem settingsMenuItem = new Forms.ToolStripMenuItem("Settings…", null, SettingsMenuItem_Click);
        _pauseMenuItem = new Forms.ToolStripMenuItem("Pause all", null, PauseMenuItem_Click);
        Forms.ToolStripMenuItem exitMenuItem = new Forms.ToolStripMenuItem("Exit", null, ExitMenuItem_Click);

        Forms.ContextMenuStrip contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add(settingsMenuItem);
        contextMenu.Items.Add(_pauseMenuItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(exitMenuItem);
        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    public event EventHandler? ShowSettingsRequested;

    public event EventHandler? TogglePauseRequested;

    public event EventHandler? ExitRequested;

    public void UpdateStatus(string statusText)
    {
        string text = string.IsNullOrWhiteSpace(statusText) ? "LiveWall" : "LiveWall — " + statusText;
        if (text.Length > 63)
        {
            text = text.Substring(0, 63);
        }

        _notifyIcon.Text = text;
    }

    public void SetPauseLabel(bool paused)
    {
        _pauseMenuItem.Text = paused ? "Resume all" : "Pause all";
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private static Icon LoadIcon()
    {
        try
        {
            string? executablePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(executablePath))
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(executablePath);
                if (extracted != null)
                {
                    return extracted;
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.WriteException("tray icon extraction failed", exception);
        }

        return SystemIcons.WinLogo;
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
    {
        EventHandler? handler = ShowSettingsRequested;
        if (handler != null)
        {
            handler(this, EventArgs.Empty);
        }
    }

    private void SettingsMenuItem_Click(object? sender, EventArgs e)
    {
        EventHandler? handler = ShowSettingsRequested;
        if (handler != null)
        {
            handler(this, EventArgs.Empty);
        }
    }

    private void PauseMenuItem_Click(object? sender, EventArgs e)
    {
        EventHandler? handler = TogglePauseRequested;
        if (handler != null)
        {
            handler(this, EventArgs.Empty);
        }
    }

    private void ExitMenuItem_Click(object? sender, EventArgs e)
    {
        EventHandler? handler = ExitRequested;
        if (handler != null)
        {
            handler(this, EventArgs.Empty);
        }
    }
}
