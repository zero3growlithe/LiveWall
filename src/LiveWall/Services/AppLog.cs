using System;
using System.Globalization;
using System.IO;

namespace LiveWall.Services;

/// <summary>
/// Append-only log in %LocalAppData%\LiveWall\crash.log. Diagnostics must never throw.
/// </summary>
public static class AppLog
{
    private static readonly object SyncRoot = new object();

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveWall");

    public static string CrashLogPath
    {
        get { return Path.Combine(DataDirectory, "crash.log"); }
    }

    public static string SettingsPath
    {
        get { return Path.Combine(DataDirectory, "settings.json"); }
    }

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "[{0:yyyy-MM-dd HH:mm:ss}] build {1}: {2}{3}",
                DateTime.Now,
                string.IsNullOrEmpty(BuildInfo.Sha) ? "local" : BuildInfo.Sha,
                message,
                Environment.NewLine);

            lock (SyncRoot)
            {
                File.AppendAllText(CrashLogPath, line);
            }
        }
        catch (Exception)
        {
            // Logging must never take the application down.
        }
    }

    public static void WriteException(string context, Exception exception)
    {
        Write(context + " -> " + exception);
    }
}
