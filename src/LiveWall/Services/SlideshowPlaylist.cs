using System;
using System.Collections.Generic;
using System.IO;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// Builds the slideshow file list for one monitor.
/// </summary>
public static class SlideshowPlaylist
{
    private static readonly string[] SupportedExtensions = new string[]
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    private static readonly string[] SupportedVideoExtensions = new string[]
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"
    };

    /// <summary>Image files in the source's folder, honouring its subfolder option, in name order.</summary>
    public static List<string> Collect(WallpaperSource source)
    {
        return Collect(source, SupportedExtensions);
    }

    /// <summary>Video files in the source's folder, honouring its subfolder option, in name order.</summary>
    public static List<string> CollectVideos(WallpaperSource source)
    {
        return Collect(source, SupportedVideoExtensions);
    }

    private static List<string> Collect(WallpaperSource source, string[] extensions)
    {
        List<string> files = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(source.FolderPath) || !Directory.Exists(source.FolderPath))
            {
                return files;
            }

            SearchOption searchOption = source.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string path in Directory.EnumerateFiles(source.FolderPath, "*", searchOption))
            {
                string extension = Path.GetExtension(path);
                for (int index = 0; index < extensions.Length; index++)
                {
                    if (string.Equals(extension, extensions[index], StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(path);
                        break;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.WriteException("folder scan failed: " + source.FolderPath, exception);
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    public static void Shuffle(List<string> files, Random random)
    {
        for (int index = files.Count - 1; index > 0; index--)
        {
            int swapIndex = random.Next(index + 1);
            string temporary = files[index];
            files[index] = files[swapIndex];
            files[swapIndex] = temporary;
        }
    }
}
