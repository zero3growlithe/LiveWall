using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Interop;
using LiveWall.Models;
using Microsoft.Win32;

namespace LiveWall.Services;

/// <summary>
/// Swaps the whole palette at runtime (<c>Themes/Light.xaml</c> ↔ <c>Themes/Dark.xaml</c>) and paints the
/// native title bars to match. Both palettes carry the same key set, so replacing one merged dictionary is
/// enough: every <c>DynamicResource</c> holder hears the change and repaints itself — open windows and open
/// dialogs included, with no restart and nothing re-shown.
/// </summary>
public static class ThemeManager
{
    private const string LightPalettePath = "Themes/Light.xaml";
    private const string DarkPalettePath = "Themes/Dark.xaml";

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE, Windows 10 20H1 and newer.</summary>
    private const int DarkModeAttribute = 20;

    /// <summary>The same switch on builds before 20H1, where the attribute number was different.</summary>
    private const int DarkModeAttributeLegacy = 19;

    private static bool _titleBarHookInstalled;

    /// <summary>The palette in use.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>
    /// Installs the <see cref="FrameworkElement.LoadedEvent"/> hook that gives every window — the settings
    /// window and both dialogs — a title bar in the current theme, without per-window code.
    /// </summary>
    public static void Initialize()
    {
        if (_titleBarHookInstalled)
        {
            return;
        }

        _titleBarHookInstalled = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Window_Loaded));
    }

    /// <summary>
    /// Replaces the palette dictionary in place. Called before the first window exists, and again every time
    /// the theme selector changes — this is what makes the switch immediate.
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        Application? application = Application.Current;
        if (application == null)
        {
            return;
        }

        ResourceDictionary palette = LoadPalette(theme == AppTheme.Dark ? DarkPalettePath : LightPalettePath);
        Collection<ResourceDictionary> merged = application.Resources.MergedDictionaries;

        // Replace the entry, do not merge a second palette: two palettes in the list would keep the first one
        // winning for every key it defines, and half the window would stay in the old theme.
        bool replaced = false;
        for (int index = 0; index < merged.Count; index++)
        {
            Uri? source = merged[index].Source;
            if (source == null)
            {
                continue;
            }

            string originalString = source.OriginalString;
            if (originalString.EndsWith(LightPalettePath, StringComparison.OrdinalIgnoreCase)
                || originalString.EndsWith(DarkPalettePath, StringComparison.OrdinalIgnoreCase))
            {
                merged[index] = palette;
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            merged.Insert(0, palette);
        }

        Current = theme;
        ApplyTitleBars(theme == AppTheme.Dark);
    }

    /// <summary>
    /// The Windows app mode (Settings → Personalisation → Colours), used on the first run so the window does
    /// not open bright white in a dark desktop. Any failure falls back to <see cref="AppTheme.Light"/>.
    /// </summary>
    public static AppTheme DetectSystemTheme()
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object? value = key == null ? null : key.GetValue("AppsUseLightTheme");
                if (value is int appsUseLightTheme)
                {
                    return appsUseLightTheme == 0 ? AppTheme.Dark : AppTheme.Light;
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.WriteException("reading the Windows app theme failed", exception);
        }

        return AppTheme.Light;
    }

    private static ResourceDictionary LoadPalette(string relativePath)
    {
        // A fully qualified pack URI built from the running assembly: a relative Uri resolves against whatever
        // base URI happens to be ambient and then fails only at swap time with "cannot locate resource".
        string assemblyName = typeof(ThemeManager).Assembly.GetName().Name ?? "LiveWall";
        Uri uri = new Uri("pack://application:,,,/" + assemblyName + ";component/" + relativePath, UriKind.Absolute);
        return new ResourceDictionary { Source = uri };
    }

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Window? window = sender as Window;
        if (window != null)
        {
            ApplyTitleBar(window, Current == AppTheme.Dark);
        }
    }

    private static void ApplyTitleBars(bool dark)
    {
        Application? application = Application.Current;
        if (application == null)
        {
            return;
        }

        foreach (Window window in application.Windows)
        {
            ApplyTitleBar(window, dark);
        }
    }

    /// <summary>
    /// Switches the native caption and frame to dark (or back). Older Windows simply ignores the call, so
    /// every failure here is swallowed on purpose.
    /// </summary>
    private static void ApplyTitleBar(Window window, bool dark)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int value = dark ? 1 : 0;
            int result = NativeMethods.DwmSetWindowAttributeInt32(handle, DarkModeAttribute, ref value, sizeof(int));
            if (result != 0)
            {
                NativeMethods.DwmSetWindowAttributeInt32(handle, DarkModeAttributeLegacy, ref value, sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // No dwmapi (very old Windows): the window keeps its light caption.
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-Vista DWM entry points: nothing to do.
        }
        catch (InvalidOperationException)
        {
            // The window has no handle yet; the Loaded hook runs again when it gets one.
        }
    }
}
