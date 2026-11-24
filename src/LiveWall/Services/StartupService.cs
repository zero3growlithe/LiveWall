using System;
using Microsoft.Win32;

namespace LiveWall.Services;

/// <summary>
/// HKCU "Run" registration so the wallpaper starts with Windows.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LiveWall";

    public static bool IsRegistered()
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (key == null)
                {
                    return false;
                }

                object? value = key.GetValue(ValueName);
                return value != null;
            }
        }
        catch (Exception exception)
        {
            AppLog.WriteException("startup registration read failed", exception);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null)
                {
                    return;
                }

                if (enabled)
                {
                    string? executablePath = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(executablePath))
                    {
                        AppLog.Write("startup registration skipped: executable path unknown");
                        return;
                    }

                    // The --tray switch tells a launched-by-Windows instance to stay in the notification area.
                    key.SetValue(ValueName, "\"" + executablePath + "\" --tray");
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.WriteException("startup registration write failed", exception);
        }
    }
}
