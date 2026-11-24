using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// Owns one <see cref="WallpaperSurface"/> per monitor, re-attaches them when Explorer restarts,
/// and pauses/resumes playback according to <see cref="FullscreenWatcher"/>.
///
/// Everything that touches WPF runs on the dispatcher thread. The 500 ms scan (window enumeration,
/// DWM queries, monitor enumeration) runs on a worker thread and only posts its RESULT to the UI, so
/// the settings window never stalls and its drop-downs keep working.
/// </summary>
public sealed class WallpaperEngine : IDisposable
{
    private const int WatchIntervalMilliseconds = 500;
    private const string CompositionTestColour = "#FF00FF";
    private const int FailingRebuildThreshold = 3;

    private static readonly TimeSpan MinimumRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailingRetryInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, WallpaperSurface> _surfaces = new Dictionary<string, WallpaperSurface>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _attachFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pauseReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly FullscreenWatcher _watcher = new FullscreenWatcher();
    private readonly DesktopWindowService _desktopWindowService = new DesktopWindowService();
    private readonly Dispatcher _dispatcher;
    private readonly Timer _watchTimer;

    private List<MonitorDescriptor> _monitors = new List<MonitorDescriptor>();
    private WallpaperSettings _settings = new WallpaperSettings();
    private PauseDecision _lastDecision = new PauseDecision();
    private DateTime _lastRetryUtc = DateTime.MinValue;
    private int _consecutiveFailures;
    private int _tickRunning;
    private bool _isRunning;
    private bool _isManualPause;
    private bool _disposed;

    public WallpaperEngine()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _watchTimer = new Timer(WatchTimer_Tick, null, Timeout.Infinite, Timeout.Infinite);
    }

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
            ApplyPlaybackState(ReevaluateNow());
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
        _monitors = MonitorService.GetMonitors();
        RebuildSurfaces();

        _isRunning = true;
        ApplyPlaybackState(ReevaluateNow());
        _watchTimer.Change(WatchIntervalMilliseconds, WatchIntervalMilliseconds);
        RaiseStatusChanged();
    }

    public void Stop()
    {
        _isRunning = false;
        _watchTimer.Change(Timeout.Infinite, Timeout.Infinite);
        DisposeSurfaces();
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

        List<MonitorDescriptor> monitors = MonitorService.GetMonitors();
        if (MonitorSetChanged(monitors))
        {
            _monitors = monitors;
            RebuildSurfaces();
        }
        else
        {
            ApplyConfiguredSources();
        }

        ApplyPlaybackState(ReevaluateNow());
        RaiseStatusChanged();
    }

    /// <summary>
    /// Re-applies the configured source to the surfaces that already exist. Applying a source is a full
    /// switch — the current video is stopped and closed, the slideshow timer is halted, the image layers are
    /// cleared — so changing from video to image really unloads the video instead of leaving it running.
    /// </summary>
    private void ApplyConfiguredSources()
    {
        if (!_desktopWindowService.EnsureWorkerWindow())
        {
            return;
        }

        AttachMissingSurfaces();

        List<string> staleDevices = new List<string>();
        foreach (KeyValuePair<string, WallpaperSurface> pair in _surfaces)
        {
            if (MonitorExists(pair.Key))
            {
                pair.Value.ApplySource(_settings.SourceForMonitor(pair.Key));
            }
            else
            {
                staleDevices.Add(pair.Key);
            }
        }

        for (int index = 0; index < staleDevices.Count; index++)
        {
            WallpaperSurface surface = _surfaces[staleDevices[index]];
            surface.DescriptionChanged -= Surface_DescriptionChanged;
            surface.Dispose();
            _surfaces.Remove(staleDevices[index]);
        }
    }

    private bool MonitorExists(string deviceName)
    {
        for (int index = 0; index < _monitors.Count; index++)
        {
            if (string.Equals(_monitors[index].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
            return "Waiting for the desktop host…";
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
        _watchTimer.Dispose();
        _surfaces.Clear();
    }

    // ── Worker thread: detection only ──

    private void WatchTimer_Tick(object? state)
    {
        if (!_isRunning || _disposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            List<MonitorDescriptor> monitors = MonitorService.GetMonitors();
            PauseDecision decision = _watcher.Evaluate(monitors, _settings.PauseWhenMaximized, _settings.PauseWhenFullscreen);
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    ApplyTick(monitors, decision);
                }
                catch (Exception exception)
                {
                    AppLog.WriteException("applying a watch tick failed", exception);
                }
                finally
                {
                    Interlocked.Exchange(ref _tickRunning, 0);
                }
            }));
        }
        catch (Exception exception)
        {
            AppLog.WriteException("watch tick scan failed", exception);
            Interlocked.Exchange(ref _tickRunning, 0);
        }
    }

    private PauseDecision ReevaluateNow()
    {
        return _watcher.Evaluate(_monitors, _settings.PauseWhenMaximized, _settings.PauseWhenFullscreen);
    }

    // ── Dispatcher thread: apply ──

    private void ApplyTick(List<MonitorDescriptor> monitors, PauseDecision decision)
    {
        if (!_isRunning)
        {
            return;
        }

        if (MonitorSetChanged(monitors))
        {
            AppLog.Write("monitor layout changed — rebuilding surfaces");
            _monitors = monitors;
            RebuildSurfaces();
        }
        else if (!_desktopWindowService.IsAlive)
        {
            if (CanRetry())
            {
                AppLog.Write("desktop host window lost (Explorer restart?) — re-attaching surfaces");
                RebuildSurfaces();
            }
        }
        else if (_surfaces.Count < _monitors.Count && CanRetry())
        {
            AttachMissingSurfaces();
        }

        ApplyPlaybackState(decision);
    }

    private bool CanRetry()
    {
        TimeSpan interval = _consecutiveFailures >= FailingRebuildThreshold ? FailingRetryInterval : MinimumRetryInterval;
        return DateTime.UtcNow - _lastRetryUtc >= interval;
    }

    private bool MonitorSetChanged(List<MonitorDescriptor> monitors)
    {
        if (monitors.Count != _monitors.Count)
        {
            return true;
        }

        for (int index = 0; index < monitors.Count; index++)
        {
            if (!string.Equals(monitors[index].DeviceName, _monitors[index].DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            RECT currentRectangle = monitors[index].MonitorRectangle;
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

    private void ApplyPlaybackState(PauseDecision decision)
    {
        if (!_isRunning)
        {
            return;
        }

        _lastDecision = decision;
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
            else if (decision.PauseAll)
            {
                shouldPause = true;
                reason = decision.PauseAllReason;
            }
            else if (decision.MonitorReasons.TryGetValue(pair.Key, out string? monitorReason) && monitorReason != null)
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

    // ── Surfaces ──

    private void RebuildSurfaces()
    {
        _lastRetryUtc = DateTime.UtcNow;
        DisposeSurfaces();

        if (_monitors.Count == 0)
        {
            _consecutiveFailures++;
            AppLog.Write("no monitors detected — nothing to draw");
            return;
        }

        if (!_desktopWindowService.EnsureWorkerWindow())
        {
            _consecutiveFailures++;
            AppLog.Write("desktop host window unavailable — wallpaper cannot be shown");
            return;
        }

        DesktopHost host = _desktopWindowService.Host;
        RECT workerRectangle = _desktopWindowService.GetWorkerRectangle();
        int attachedCount = 0;

        foreach (MonitorDescriptor monitor in _monitors)
        {
            WallpaperSurface surface = new WallpaperSurface(monitor.DeviceName);
            if (!surface.Attach(host, monitor.MonitorRectangle, workerRectangle))
            {
                _attachFailures[monitor.DeviceName] = surface.AttachFailureReason;
                surface.Dispose();
                continue;
            }

            surface.DescriptionChanged += Surface_DescriptionChanged;
            surface.ApplySource(_settings.SourceForMonitor(monitor.DeviceName));
            _surfaces[monitor.DeviceName] = surface;
            attachedCount++;
        }

        _consecutiveFailures = attachedCount == _monitors.Count ? 0 : _consecutiveFailures + 1;
    }

    /// <summary>
    /// Creates only the surfaces that are missing, so a monitor that appears later does not restart
    /// the wallpaper already playing on the others.
    /// </summary>
    private void AttachMissingSurfaces()
    {
        _lastRetryUtc = DateTime.UtcNow;

        if (!_desktopWindowService.EnsureWorkerWindow())
        {
            _consecutiveFailures++;
            return;
        }

        DesktopHost host = _desktopWindowService.Host;
        RECT workerRectangle = _desktopWindowService.GetWorkerRectangle();
        bool allAttached = true;

        foreach (MonitorDescriptor monitor in _monitors)
        {
            if (_surfaces.ContainsKey(monitor.DeviceName))
            {
                continue;
            }

            _attachFailures.Remove(monitor.DeviceName);
            WallpaperSurface surface = new WallpaperSurface(monitor.DeviceName);
            if (!surface.Attach(host, monitor.MonitorRectangle, workerRectangle))
            {
                _attachFailures[monitor.DeviceName] = surface.AttachFailureReason;
                surface.Dispose();
                allAttached = false;
                continue;
            }

            surface.DescriptionChanged += Surface_DescriptionChanged;
            surface.ApplySource(_settings.SourceForMonitor(monitor.DeviceName));
            _surfaces[monitor.DeviceName] = surface;
        }

        _consecutiveFailures = allAttached ? 0 : _consecutiveFailures + 1;
    }

    private void DisposeSurfaces()
    {
        foreach (KeyValuePair<string, WallpaperSurface> pair in _surfaces)
        {
            pair.Value.DescriptionChanged -= Surface_DescriptionChanged;
            pair.Value.Dispose();
        }

        _surfaces.Clear();
        _pauseReasons.Clear();
        _attachFailures.Clear();
    }

    private void Surface_DescriptionChanged(object? sender, EventArgs e)
    {
        // A surface learned something new (media opened, media failed, slide advanced): show it immediately
        // instead of waiting for the next pause-state change.
        RaiseStatusChanged();
    }

    /// <summary>The resolved desktop host (topology, parent window, z-order anchor).</summary>
    public DesktopHost Host
    {
        get { return _desktopWindowService.Host; }
    }

    /// <summary>Switches every surface to a solid colour without touching the saved settings (visibility test).</summary>
    public void ApplyTestColour(string colorHex)
    {
        WallpaperSource testSource = new WallpaperSource();
        testSource.Kind = WallpaperKind.SolidColor;
        testSource.BackgroundColor = colorHex;

        foreach (KeyValuePair<string, WallpaperSurface> pair in _surfaces)
        {
            pair.Value.ApplySource(testSource);
        }

        ApplyPlaybackState(_lastDecision);
    }

    /// <summary>Puts the configured wallpapers back after a visibility test.</summary>
    public void RestoreConfiguredSources()
    {
        ApplyConfiguredSources();
        ApplyPlaybackState(ReevaluateNow());
        RaiseStatusChanged();
    }

    /// <summary>
    /// Paints magenta for a moment, reads the screen back and restores the wallpaper. This is the only way to
    /// tell "the shell is not composing our window" apart from "wrong z-order or position": a monitor covered
    /// by a maximized window is skipped, because the sample would read that window instead of the desktop.
    /// </summary>
    public async Task<string> RunCompositionCheckAsync()
    {
        if (!_isRunning || _surfaces.Count == 0)
        {
            return "not run — the engine is not drawing anything yet";
        }

        try
        {
            ApplyTestColour(CompositionTestColour);
            await Task.Delay(700);

            List<string> lines = new List<string>();
            for (int index = 0; index < _monitors.Count; index++)
            {
                MonitorDescriptor monitor = _monitors[index];
                string label = "Display " + (monitor.Index + 1).ToString(CultureInfo.InvariantCulture);

                if (_pauseReasons.ContainsKey(monitor.DeviceName))
                {
                    lines.Add(label + ": skipped (a window covers this monitor right now)");
                    continue;
                }

                string detail;
                bool visible = DesktopProbe.Matches(monitor.MonitorRectangle, 255, 0, 255, 30, out detail);
                lines.Add(label + ": " + (visible ? "wallpaper VISIBLE" : "wallpaper NOT VISIBLE") + " — " + detail);
            }

            return string.Join(Environment.NewLine, lines);
        }
        finally
        {
            RestoreConfiguredSources();
        }
    }

    /// <summary>Everything the diagnostics window shows, in one string.</summary>
    public string BuildDiagnosticsReport()
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("LiveWall diagnostics");
        builder.AppendLine("engine running: " + (_isRunning ? "yes" : "no") + (IsManualPause ? " (manually paused)" : string.Empty));
        builder.AppendLine("render mode: " + (_settings.RenderMode == RenderModePreference.Software ? "software (GDI)" : "hardware"));
        builder.AppendLine("composition check: " + (string.IsNullOrEmpty(_settings.CompositionCheckResult) ? "not run yet" : _settings.CompositionCheckResult.Replace(Environment.NewLine, " | ")));
        builder.AppendLine("desktop host: " + _desktopWindowService.Host.Describe());
        builder.AppendLine("desktop host alive: " + (_desktopWindowService.IsAlive ? "yes" : "no"));
        builder.AppendLine("desktop host client rect: " + _desktopWindowService.GetWorkerRectangle());
        builder.AppendLine("monitors: " + _monitors.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("attach failures: " + _attachFailures.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(string.Empty);

        for (int index = 0; index < _monitors.Count; index++)
        {
            MonitorDescriptor monitor = _monitors[index];
            builder.AppendLine("monitor " + (monitor.Index + 1).ToString(CultureInfo.InvariantCulture) + " " + monitor.DeviceName + " rect " + monitor.MonitorRectangle);

            WallpaperSurface? surface = null;
            if (_surfaces.ContainsKey(monitor.DeviceName))
            {
                surface = _surfaces[monitor.DeviceName];
            }

            if (surface == null)
            {
                string? failure;
                if (_attachFailures.TryGetValue(monitor.DeviceName, out failure) && failure != null)
                {
                    builder.AppendLine("  not attached: " + failure);
                }
                else
                {
                    builder.AppendLine("  not attached: no surface yet");
                }
            }
            else
            {
                builder.AppendLine("  " + surface.DiagnosticLine());
            }

            builder.AppendLine("  status: " + GetMonitorStatus(monitor.DeviceName));
            builder.AppendLine("  screen shows: " + DesktopProbe.Describe(monitor.MonitorRectangle));
            builder.AppendLine(string.Empty);
        }

        return builder.ToString();
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
