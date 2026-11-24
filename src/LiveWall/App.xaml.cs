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
    /// <summary>Stage 0 = hardware + child at creation; 1..3 are the fallbacks; 4 = every combination failed.</summary>
    private const int MaximumCompositionStage = 3;

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

        if (_settings.RenderMode == RenderModePreference.Software)
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

        _mainWindow = new MainWindow(_engine, _settings);
        MainWindow = _mainWindow;

        bool startedFromAutostart = Array.IndexOf(e.Args, "--tray") >= 0;
        bool showWindow = firstRun || !startedFromAutostart || !_settings.StartMinimized;
        if (showWindow)
        {
            _mainWindow.Show();
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
    /// The combinations tried, in order: the hardware path first (cheapest), then software rendering, each with
    /// the window created in place and with the older "popup, then re-parent" order.
    /// </summary>
    private static void ApplyCompositionStage(WallpaperSettings settings)
    {
        switch (settings.CompositionRetryStage)
        {
            case 1:
                settings.RenderMode = RenderModePreference.Hardware;
                settings.SurfaceCreation = SurfaceCreationMode.ReparentedPopup;
                break;
            case 2:
                settings.RenderMode = RenderModePreference.Software;
                settings.SurfaceCreation = SurfaceCreationMode.ChildAtCreation;
                break;
            case 3:
                settings.RenderMode = RenderModePreference.Software;
                settings.SurfaceCreation = SurfaceCreationMode.ReparentedPopup;
                break;
            default:
                settings.RenderMode = RenderModePreference.Hardware;
                settings.SurfaceCreation = SurfaceCreationMode.ChildAtCreation;
                break;
        }
    }

    private string DescribeCurrentStrategy()
    {
        if (_settings == null)
        {
            return "unknown";
        }

        return (_settings.RenderMode == RenderModePreference.Software ? "software rendering" : "hardware rendering")
            + " + " + (_settings.SurfaceCreation == SurfaceCreationMode.ChildAtCreation ? "child at creation" : "reparented popup")
            + " (stage " + _settings.CompositionRetryStage.ToString(CultureInfo.InvariantCulture) + ")";
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

            ProcessStartInfo startInfo = new ProcessStartInfo("cmd.exe", "/c timeout /t 1 >nul & start "" "" + executablePath + """);
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

    private void TrayIcon_ShowSettingsRequested(object? sender, EventArgs e)
    {
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
