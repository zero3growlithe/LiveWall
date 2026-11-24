using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// Loads and saves %LocalAppData%\LiveWall\settings.json.
/// </summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };

    public static WallpaperSettings Load()
    {
        try
        {
            if (!File.Exists(AppLog.SettingsPath))
            {
                return new WallpaperSettings();
            }

            string json = File.ReadAllText(AppLog.SettingsPath);
            WallpaperSettings? settings = JsonSerializer.Deserialize<WallpaperSettings>(json);
            if (settings == null)
            {
                return new WallpaperSettings();
            }

            return Normalize(settings);
        }
        catch (Exception exception)
        {
            AppLog.WriteException("settings load failed, defaults used", exception);
            return new WallpaperSettings();
        }
    }

    public static void Save(WallpaperSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppLog.DataDirectory);
            string json = JsonSerializer.Serialize(Normalize(settings), SerializerOptions);
            File.WriteAllText(AppLog.SettingsPath, json);
        }
        catch (Exception exception)
        {
            AppLog.WriteException("settings save failed", exception);
        }
    }

    private static WallpaperSettings Normalize(WallpaperSettings settings)
    {
        Dictionary<string, WallpaperSource>? existing = settings.PerMonitor;
        Dictionary<string, WallpaperSource> normalized = new Dictionary<string, WallpaperSource>(StringComparer.OrdinalIgnoreCase);
        if (existing != null)
        {
            foreach (KeyValuePair<string, WallpaperSource> pair in existing)
            {
                if (!string.IsNullOrEmpty(pair.Key) && pair.Value != null)
                {
                    normalized[pair.Key] = pair.Value;
                }
            }
        }

        settings.PerMonitor = normalized;
        if (settings.Shared == null)
        {
            settings.Shared = new WallpaperSource();
        }

        settings.Shared.SlideshowIntervalSeconds = Math.Clamp(settings.Shared.SlideshowIntervalSeconds, 5, 3600);
        settings.Shared.VideoVolume = Math.Clamp(settings.Shared.VideoVolume, 0, 100);
        foreach (KeyValuePair<string, WallpaperSource> pair in normalized)
        {
            pair.Value.SlideshowIntervalSeconds = Math.Clamp(pair.Value.SlideshowIntervalSeconds, 5, 3600);
            pair.Value.VideoVolume = Math.Clamp(pair.Value.VideoVolume, 0, 100);
        }

        return settings;
    }
}
