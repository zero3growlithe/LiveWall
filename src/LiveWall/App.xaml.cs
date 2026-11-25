using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LiveWall.Models;
using LiveWall.Services;

namespace LiveWall;

public partial class App : Application
{
    /// <summary>
    /// Stage 0 = our DirectComposition renderer, 1 = WPF hardware, 2 = WPF software, 3 = every strategy failed.
    /// The order is not arbitrary: on Windows 11 24H2+ only DirectComposition content is composed by the shell,
    /// so that is tried first and the WPF paths are left as fallbacks for older builds.
    /// </summary>
    private const int MaximumCompositionStage = 2;

    private Mutex? _singleInstanceMutex;
    private WallpaperEngine? _engine;
    private WallpaperSettings? _settings;
    private TrayIconService? _trayIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        bool createdNew;
        _singleInstanceMutex = new Mutex(true, "Local\\LiveWall.Instance", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "LiveWall is already running. Use its icon in the notification area to open the settings.",
                "LiveWall",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        bool firstRun = !File.Exists(AppLog.SettingsPath);
        _settings = SettingsService.Load();

        if (firstRun)
        {
            // Nothing saved yet: open in whatever mode Windows itself is set to. From then on the choice is the
            // user's and comes back from the settings file.
            _settings.Theme = ThemeManager.DetectSystemTheme();
        }

        // Before any window exists, or the user sees a light window flash and then a dark one.
        ThemeManager.Initialize();
        ThemeManager.Apply(_settings.Theme);

        if (_settings.Renderer == RendererPreference.WpfSoftware || _settings.RenderMode == RenderModePreference.Software)
        {
            // Must happen before any window exists: WPF decides the render path per window at creation.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            AppLog.Write("software rendering requested by the settings");
        }

        _engine = new WallpaperEngine();
        _engine.StatusChanged += Engine_StatusChanged;

        _trayIcon = new TrayIconService();
        _trayIcon.ShowSettingsRequested += TrayIcon_ShowSettingsRequested;
        _trayIcon.TogglePauseRequested += TrayIcon_TogglePauseRequested;
        _trayIcon.ExitRequested += TrayIcon_ExitRequested;

        if (_settings.StartAutomatically)
        {
            _engine.Start(_settings);
        }

        if (firstRun)
        {
            // Persist the default configuration so the next launch knows it is not the first run.
            SettingsService.Save(_settings);
        }

        bool startedFromAutostart = Array.IndexOf(e.Args, "--tray") >= 0;
        bool showWindow = firstRun || !startedFromAutostart || !_settings.StartMinimized;
        if (showWindow)
        {
            EnsureMainWindow();
            _mainWindow!.Show();
            _mainWindow.Activate();
        }

        UpdateTrayStatus();

        bool shouldCheckComposition = _settings.StartAutomatically
            && !_settings.CompositionVerified
            && _settings.CompositionRetryStage <= MaximumCompositionStage;
        if (shouldCheckComposition)
        {
            RunStartupCompositionCheck();
        }
    }

    /// <summary>
    /// Runs once, the first time the wallpaper is drawn: it proves whether pixels really reach the desktop and
    /// records the answer in the log, so an "I see nothing" report comes with evidence attached.
    /// </summary>
    private async void RunStartupCompositionCheck()
    {
        try
        {
            await Task.Delay(1600);
            if (_engine == null || _settings == null)
            {
                return;
            }

            CompositionCheckReport report = await _engine.RunCompositionCheckAsync();
            _settings.CompositionCheckResult = report.Text;
            SettingsService.Save(_settings);
            AppLog.Write("composition check (" + DescribeCurrentStrategy() + "): " + report.Text.Replace(Environment.NewLine, " | "));
            if (_mainWindow != null)
            {
                _mainWindow.RefreshFromEngine();
            }

            if (report.AnyVisible)
            {
                _settings.CompositionVerified = true;
                SettingsService.Save(_settings);
                AppLog.Write("wallpaper is reaching the screen with " + DescribeCurrentStrategy());
                return;
            }

            if (!report.AnyNotVisible)
            {
                // Every monitor was covered while probing: nothing was learned, do not start a restart loop.
                return;
            }

            if (_settings.CompositionRetryStage >= MaximumCompositionStage)
            {
                _settings.CompositionRetryStage = MaximumCompositionStage + 1;
                SettingsService.Save(_settings);
                AppLog.Write("every rendering strategy failed — the shell is not composing our window on this build");
                return;
            }

            _settings.CompositionRetryStage++;
            ApplyCompositionStage(_settings);
            SettingsService.Save(_settings);
            AppLog.Write("wallpaper is not visible; retrying with " + DescribeCurrentStrategy());
            RestartDetached();
        }
        catch (Exception exception)
        {
            AppLog.WriteException("the startup composition check failed", exception);
        }
    }

    /// <summary>
    /// The drawing stacks tried, in order: DirectComposition first (the only one the shell composes on
    /// Windows 11 24H2+), then the two WPF paths for older builds.
    /// </summary>
    private static void ApplyCompositionStage(WallpaperSettings settings)
    {
        settings.SurfaceCreation = SurfaceCreationMode.ChildAtCreation;
        switch (settings.CompositionRetryStage)
        {
            case 1:
                settings.Renderer = RendererPreference.WpfHardware;
                settings.RenderMode = RenderModePreference.Hardware;
                break;
            case 2:
                settings.Renderer = RendererPreference.WpfSoftware;
                settings.RenderMode = RenderModePreference.Software;
                break;
            default:
                settings.Renderer = RendererPreference.Composition;
                settings.RenderMode = RenderModePreference.Hardware;
                break;
        }
    }

    private string DescribeCurrentStrategy()
    {
        if (_settings == null)
        {
            return "unknown";
        }

        string renderer;
        switch (_settings.Renderer)
        {
            case RendererPreference.WpfHardware:
                renderer = "WPF hardware rendering";
                break;
            case RendererPreference.WpfSoftware:
                renderer = "WPF software rendering";
                break;
            default:
                renderer = "DirectComposition";
                break;
        }

        return renderer + " (stage " + _settings.CompositionRetryStage.ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>Starts a fresh instance after this one is gone, so a render-mode change can take effect.</summary>
    private void RestartDetached()
    {
        try
        {
            Process current = Process.GetCurrentProcess();
            string executablePath = current.MainModule == null ? string.Empty : current.MainModule.FileName;
            if (string.IsNullOrEmpty(executablePath))
            {
                return;
            }

            // Built from a char literal on purpose: the command line needs real double quotes around the
            // path and the empty title, and escaped quotes here are easy to get wrong.
            const char quote = '\u0022';
            string arguments = "/c timeout /t 1 >nul & start " + quote + quote + " " + quote + executablePath + quote;
            ProcessStartInfo startInfo = new ProcessStartInfo("cmd.exe", arguments);
            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = false;
            Process.Start(startInfo);
            ExitApplication();
        }
        catch (Exception exception)
        {
            AppLog.WriteException("restarting for a renderer retry failed", exception);
        }
    }

    private void Engine_StatusChanged(object? sender, EventArgs e)
    {
        UpdateTrayStatus();
        if (_mainWindow != null && !ReferenceEquals(sender, _mainWindow))
        {
            _mainWindow.RefreshFromEngine();
        }
    }

    /// <summary>
    /// Builds the settings window the first time it is actually wanted. A tray-only start has no window at all:
    /// a WPF window and its visual tree cost tens of megabytes, and the wallpaper itself never needs it.
    /// </summary>
    private void EnsureMainWindow()
    {
        if (_mainWindow != null || _engine == null || _settings == null)
        {
            return;
        }

        _mainWindow = new MainWindow(_engine, _settings);
        MainWindow = _mainWindow;
    }

    private void TrayIcon_ShowSettingsRequested(object? sender, EventArgs e)
    {
        EnsureMainWindow();
        if (_mainWindow == null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.RefreshFromEngine();
    }

    private void TrayIcon_TogglePauseRequested(object? sender, EventArgs e)
    {
        if (_engine == null)
        {
            return;
        }

        _engine.IsManualPause = !_engine.IsManualPause;
        UpdateTrayStatus();
        if (_mainWindow != null)
        {
            _mainWindow.RefreshFromEngine();
        }
    }

    private void TrayIcon_ExitRequested(object? sender, EventArgs e)
    {
        ExitApplication();
    }

    private void UpdateTrayStatus()
    {
        if (_trayIcon == null || _engine == null)
        {
            return;
        }

        _trayIcon.UpdateStatus(_engine.GetGlobalStatusText());
        _trayIcon.SetPauseLabel(_engine.IsManualPause);
    }

    private void ExitApplication()
    {
        if (_mainWindow != null)
        {
            _mainWindow.SaveWindowPlacement();
            _mainWindow.ExitRequested = true;
        }

        if (_engine != null)
        {
            _engine.Stop();
            _engine.Dispose();
            _engine = null;
        }

        if (_trayIcon != null)
        {
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_singleInstanceMutex != null)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        Shutdown();
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        AppLog.WriteException("unhandled UI exception", eventArgs.Exception);
        MessageBox.Show(
            "Unexpected error: " + eventArgs.Exception.Message + Environment.NewLine + Environment.NewLine
                + "A log was written to " + AppLog.CrashLogPath,
            "LiveWall (build " + (string.IsNullOrEmpty(BuildInfo.Sha) ? "local" : BuildInfo.Sha) + ")",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        eventArgs.Handled = true;
    }
}
