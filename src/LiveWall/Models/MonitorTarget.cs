using System;
using System.ComponentModel;

namespace LiveWall.Models;

/// <summary>
/// One row in the monitor list of the settings window: the monitor identity plus its live playback status.
/// Public because the XAML item template binds to it.
/// </summary>
public sealed class MonitorTarget : INotifyPropertyChanged
{
    private string _statusLine = "Stopped";

    public MonitorTarget(string deviceName, string displayName)
    {
        DeviceName = deviceName;
        DisplayName = displayName;
    }

    public string DeviceName { get; }

    public string DisplayName { get; }

    public string StatusLine
    {
        get
        {
            return _statusLine;
        }

        set
        {
            string safeValue = value ?? string.Empty;
            if (string.Equals(_statusLine, safeValue, StringComparison.Ordinal))
            {
                return;
            }

            _statusLine = safeValue;
            RaisePropertyChanged(nameof(StatusLine));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged(string propertyName)
    {
        PropertyChangedEventHandler? handler = PropertyChanged;
        if (handler != null)
        {
            handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
