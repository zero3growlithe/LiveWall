using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// Saves, lists, loads and deletes named wallpaper profiles. One profile holds the wallpaper of every
/// monitor at once, so a whole setup can be recalled in a click instead of being retyped per screen.
/// </summary>
public static class ProfileService
{
    private const string Extension = ".json";

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    public static string ProfilesDirectory
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiveWall",
                "profiles");
        }
    }

    /// <summary>Profile names, sorted, without the file extension; empty when nothing was saved yet.</summary>
    public static IReadOnlyList<string> ListProfiles()
    {
        List<string> names = new List<string>();
        try
        {
            if (!Directory.Exists(ProfilesDirectory))
            {
                return names;
            }

            string[] files = Directory.GetFiles(ProfilesDirectory, "*" + Extension);
            for (int index = 0; index < files.Length; index++)
            {
                names.Add(Path.GetFileNameWithoutExtension(files[index]));
            }
        }
        catch (Exception exception)
        {
            AppLog.WriteException("listing the wallpaper profiles failed", exception);
        }

        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return names;
    }

    public static bool Save(string name, WallpaperSettings settings)
    {
        string fileName = MakeFileName(name);
        if (fileName.Length == 0 || settings == null)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(ProfilesDirectory);
            WallpaperProfile profile = WallpaperProfile.From(settings);
            profile.Name = name == null ? string.Empty : name.Trim();
            string json = JsonSerializer.Serialize(profile, SerializerOptions);
            File.WriteAllText(Path.Combine(ProfilesDirectory, fileName + Extension), json);
            return true;
        }
        catch (Exception exception)
        {
            AppLog.WriteException("saving the wallpaper profile failed: " + name, exception);
            return false;
        }
    }

    public static WallpaperProfile? Load(string name)
    {
        string fileName = MakeFileName(name);
        if (fileName.Length == 0)
        {
            return null;
        }

        try
        {
            string path = Path.Combine(ProfilesDirectory, fileName + Extension);
            if (!File.Exists(path))
            {
                return null;
            }

            WallpaperProfile? profile = JsonSerializer.Deserialize<WallpaperProfile>(
                File.ReadAllText(path),
                SerializerOptions);
            if (profile != null && string.IsNullOrEmpty(profile.Name))
            {
                profile.Name = name;
            }

            return profile;
        }
        catch (Exception exception)
        {
            AppLog.WriteException("loading the wallpaper profile failed: " + name, exception);
            return null;
        }
    }

    public static bool Delete(string name)
    {
        string fileName = MakeFileName(name);
        if (fileName.Length == 0)
        {
            return false;
        }

        try
        {
            string path = Path.Combine(ProfilesDirectory, fileName + Extension);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception exception)
        {
            AppLog.WriteException("deleting the wallpaper profile failed: " + name, exception);
            return false;
        }
    }

    /// <summary>Strips anything a file name cannot hold, so a profile called "desk / night" still saves.</summary>
    private static string MakeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string trimmed = name.Trim();
        StringBuilder builder = new StringBuilder(trimmed.Length);
        for (int index = 0; index < trimmed.Length; index++)
        {
            char character = trimmed[index];
            bool rejected = false;
            for (int invalidIndex = 0; invalidIndex < invalid.Length; invalidIndex++)
            {
                if (invalid[invalidIndex] == character)
                {
                    rejected = true;
                    break;
                }
            }

            if (!rejected)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }
}
