using System;
using System.Collections.Generic;
using System.Windows.Threading;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// Owns one <see cref="WallpaperSurface"/> per monitor, re-attaches them when Explorer restarts,
/// and pauses/resumes playback according to <see cref="FullscreenWatcher"/>.
/// </summary>
public sealed class WallpaperEngine : IDisposable
{
    private const int WatchIntervalMilliseconds = 500;
    private const int MonitorRescanTickInterval = 10;

    private readonly Dictionary<string, WallpaperSurface> _surfaces = new Dictionary<string, WallpaperSurface>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _attachFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pauseReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly FullscreenWatcher _watcher = new FullscreenWatcher();
    private readonly DesktopWindowService _desktopWindowService = new DesktopWindowService();

    private DispatcherTimer? _watchTimer;
    private List<MonitorDescriptor> _monitors = new List<MonitorDescriptor>();
    private WallpaperSettings _settings = new WallpaperSettings();
    private PauseDecision _lastDecision = new PauseDecision();
    private bool _isRunning;
    private bool _isManualPause;
    private bool _disposed;
    private int _tickCounter;

    public event EventHandler? StatusChanged;

    public bool IsRunning
    {
        get { return _isRunning; }
    }

    public bool IsManualPause
    {
        get
        {
            return _isManualPause;
        }

        set
        {
            if (_isManualPause == value)
            {
                return;
            }

            _isManualPause = value;
            UpdatePlaybackState();
            RaiseStatusChanged();
        }
    }

    public IReadOnlyList<MonitorDescriptor> Monitors
    {
        get { return _monitors; }
    }

    public void RefreshMonitors()
    {
        _monitors = MonitorService.GetMonitors();
    }

    public void Start(WallpaperSettings settings)
    {
        _settings = settings;
        RefreshMonitors();
        RebuildSurfaces();

        if (_watchTimer == null)
        {
            _watchTimer = new DispatcherTimer();
            _watchTimer.Interval = TimeSpan.FromMilliseconds(WatchIntervalMilliseconds);
            _watchTimer.Tick += WatchTimer_Tick;
        }

        _isRunning = true;
        _watchTimer.Start();
        UpdatePlaybackState();
        RaiseStatusChanged();
    }

    public void Stop()
    {
        _isRunning = false;
        if (_watchTimer != null)
        {
            _watchTimer.Stop();
        }

        DisposeSurfaces();
        _pauseReasons.Clear();
        _lastDecision = new PauseDecision();
        RaiseStatusChanged();
    }

    public void ApplySettings(WallpaperSettings settings)
    {
        _settings = settings;
        if (!_isRunning)
        {
            // "Apply" on a stopped engine means "start drawing this wallpaper".
            Start(settings);
            return;
        }

        RefreshMonitors();
        RebuildSurfaces();
        UpdatePlaybackState();
        RaiseStatusChanged();
    }

    public string GetMonitorStatus(string deviceName)
    {
        if (!_isRunning)
        {
            return "Stopped";
        }

        if (_attachFailures.TryGetValue(deviceName, out string? failure) && failure != null)
        {
            return "Not attached — " + failure;
        }

        WallpaperSurface? surface = null;
        if (_surfaces.ContainsKey(deviceName))
        {
            surface = _surfaces[deviceName];
        }

        if (surface == null)
        {
            return "Stopped";
        }

        string reason = string.Empty;
        if (_pauseReasons.TryGetValue(deviceName, out reason))
        {
            return "Paused — " + reason + " (" + surface.ActiveDescription + ")";
        }

        return "Playing — " + surface.ActiveDescription;
    }

    public string GetGlobalStatusText()
    {
        if (!_isRunning)
        {
            return "Engine stopped — no wallpaper is being drawn.";
        }

        if (_attachFailures.Count > 0)
        {
            List<string> failures = new List<string>();
            foreach (KeyValuePair<string, string> pair in _attachFailures)
            {
                failures.Add(MonitorLabel(pair.Key) + ": " + pair.Value);
            }

            return "Could not attach to the desktop for " + string.Join(", ", failures) + " — see crash.log.";
        }

        if (_monitors.Count == 0)
        {
            return "No monitors detected.";
        }

        if (_isManualPause)
        {
            return "Paused on every monitor (manual pause).";
        }

        if (_lastDecision.PauseAll)
        {
            return "Paused on every monitor — " + _lastDecision.PauseAllReason + ".";
        }

        if (_lastDecision.MonitorReasons.Count > 0)
        {
            List<string> reasons = new List<string>();
            foreach (KeyValuePair<string, string> pair in _lastDecision.MonitorReasons)
            {
                reasons.Add(MonitorLabel(pair.Key) + " (" + pair.Value + ")");
            }

            return "Paused on " + string.Join(", ", reasons) + "; the other monitors keep playing.";
        }

        return "Playing on " + _monitors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (_monitors.Count == 1 ? " monitor." : " monitors.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _surfaces.Clear();
    }

    // ── Surfaces ──

    private void RebuildSurfaces()
    {
        DisposeSurfaces();
        _attachFailures.Clear();

        if (_monitors.Count == 0)
        {
            AppLog.Write("no monitors detected — nothing to draw");
            return;
        }

        if (!_desktopWindowService.EnsureWorkerWindow())
        {
            AppLog.Write("desktop host window unavailable — wallpaper cannot be shown");
            return;
        }

        IntPtr workerWindow = _desktopWindowService.WorkerWindow;
        RECT workerRectangle = _desktopWindowService.GetWorkerRectangle();

        foreach (MonitorDescriptor monitor in _monitors)
        {
            WallpaperSurface surface = new WallpaperSurface(monitor.DeviceName);
            if (!surface.Attach(workerWindow, monitor.MonitorRectangle, workerRectangle))
            {
                _attachFailures[monitor.DeviceName] = surface.AttachFailureReason;
                surface.Dispose();
                continue;
            }

            surface.ApplySource(_settings.SourceForMonitor(monitor.DeviceName));
            _surfaces[monitor.DeviceName] = surface;
        }
    }

    private void DisposeSurfaces()
    {
        foreach (KeyValuePair<string, WallpaperSurface> pair in _surfaces)
        {
            pair.Value.Dispose();
        }

        _surfaces.Clear();
        _pauseReasons.Clear();
    }

    // ── Watch loop ──

    private void WatchTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isRunning)
        {
            return;
        }

        _tickCounter++;
        if (_tickCounter >= MonitorRescanTickInterval)
        {
            _tickCounter = 0;
            if (MonitorSetChanged())
            {
                AppLog.Write("monitor layout changed — rebuilding surfaces");
                RefreshMonitors();
                RebuildSurfaces();
                UpdatePlaybackState();
                RaiseStatusChanged();
                return;
            }
        }

        if (!_desktopWindowService.IsAlive || _surfaces.Count == 0)
        {
            AppLog.Write("desktop host window lost (Explorer restart?) — re-attaching surfaces");
            RebuildSurfaces();
        }

        UpdatePlaybackState();
    }

    private bool MonitorSetChanged()
    {
        List<MonitorDescriptor> current = MonitorService.GetMonitors();
        if (current.Count != _monitors.Count)
        {
            return true;
        }

        for (int index = 0; index < current.Count; index++)
        {
            if (!string.Equals(current[index].DeviceName, _monitors[index].DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            RECT currentRectangle = current[index].MonitorRectangle;
            RECT knownRectangle = _monitors[index].MonitorRectangle;
            if (currentRectangle.Left != knownRectangle.Left
                || currentRectangle.Top != knownRectangle.Top
                || currentRectangle.Width != knownRectangle.Width
                || currentRectangle.Height != knownRectangle.Height)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdatePlaybackState()
    {
        if (!_isRunning)
        {
            return;
        }

        _lastDecision = _watcher.Evaluate(_monitors, _settings.PauseWhenMaximized, _settings.PauseWhenFullscreen);
        bool stateChanged = false;

        foreach (KeyValuePair<string, WallpaperSurface> pair in _surfaces)
        {
            string reason = string.Empty;
            bool shouldPause = false;

            if (_isManualPause)
            {
                shouldPause = true;
                reason = "paused manually";
            }
            else if (_lastDecision.PauseAll)
            {
                shouldPause = true;
                reason = _lastDecision.PauseAllReason;
            }
            else if (_lastDecision.MonitorReasons.TryGetValue(pair.Key, out string? monitorReason) && monitorReason != null)
            {
                shouldPause = true;
                reason = monitorReason;
            }

            string previousReason = string.Empty;
            bool wasPaused = _pauseReasons.TryGetValue(pair.Key, out previousReason);

            if (shouldPause)
            {
                _pauseReasons[pair.Key] = reason;
                if (!wasPaused || !string.Equals(previousReason, reason, StringComparison.Ordinal))
                {
                    stateChanged = true;
                }
            }
            else if (wasPaused)
            {
                _pauseReasons.Remove(pair.Key);
                stateChanged = true;
            }

            pair.Value.SetPaused(shouldPause);
        }

        if (stateChanged)
        {
            RaiseStatusChanged();
        }
    }

    private string MonitorLabel(string deviceName)
    {
        for (int index = 0; index < _monitors.Count; index++)
        {
            if (string.Equals(_monitors[index].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return "Display " + (_monitors[index].Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return deviceName;
    }

    private void RaiseStatusChanged()
    {
        EventHandler? handler = StatusChanged;
        if (handler != null)
        {
            handler(this, EventArgs.Empty);
        }
    }
}
