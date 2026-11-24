using System;
using System.IO;
using System.Threading;
using System.Windows;
using LiveWall.Models;
using LiveWall.Services;

namespace LiveWall;

public partial class App : Application
{
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

        bool showWindow = firstRun || !_settings.StartMinimized;
        if (showWindow)
        {
            _mainWindow.Show();
            _mainWindow.Activate();
        }

        UpdateTrayStatus();
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
