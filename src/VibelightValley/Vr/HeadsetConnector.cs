using System;
using System.Diagnostics;
using System.Threading;
using VibelightValley.Core;
using VibelightValley.Xr;

namespace VibelightValley.Vr;

/// <summary>
/// Decides when to try connecting to the headset, and runs each attempt on a background thread (xrCreateInstance can
/// block for seconds).
///
/// An attempt is not free. With SteamVR as the OpenXR runtime, xrCreateInstance starts vrserver.exe when it is not
/// running; without a headset that process fails and SteamVR kills it five seconds later. Retrying on a fixed timer
/// did that about 240 times an hour for as long as the game was played flat. So:
///  - failed attempts back off: ConnectRetrySeconds, then twice as long each time, up to <see cref="MaxRetrySeconds"/>
///  - while SteamVR is already running an attempt launches nothing, so it is retried every ConnectRetrySeconds; a
///    headset that connects later brings SteamVR up, and VR follows within seconds
///  - <see cref="RetryNow"/> (F5) skips the wait
/// </summary>
public sealed class HeadsetConnector
{
    private const float MaxRetrySeconds = 300f;
    private const float SteamVrPollSeconds = 3f;
    private const int FailuresLogged = 5;   // later ones only in the verbose log

    private readonly string _pluginDir;
    private int _attempts;
    private int _failures;                  // in a row, since a session last ran
    private float _retryAt;                 // next attempt that may have to launch SteamVR
    private float _lastAttemptEnd = float.NegativeInfinity;

    // The attempt in flight. The thread writes _result/_error and sets _done last.
    private bool _connecting;
    private bool _attemptWithSteamVrUp;
    private volatile bool _done;
    private XrBootstrap _result;
    private Exception _error;

    // SteamVR watch: a process list query takes milliseconds, so it runs on the thread pool.
    private float _nextProbe;
    private int _probeGeneration;           // a probe started before the latest attempt must not report
    private volatile bool _steamVrRunning;

    public HeadsetConnector(string pluginDir) { _pluginDir = pluginDir; }

    private static float BaseDelay => Math.Max(2f, Settings.ConnectRetrySeconds.Value);

    /// <summary>Main thread, every frame while disconnected. Returns the OpenXR instance once an attempt succeeds.</summary>
    public XrBootstrap Update(float now)
    {
        if (_connecting) return _done ? Finish(now) : null;

        ProbeSteamVr(now);
        if (now >= _retryAt) Start(steamVrUp: false);
        else if (_steamVrRunning && now >= _lastAttemptEnd + BaseDelay) Start(steamVrUp: true);
        return null;
    }

    /// <summary>Try on the next Update instead of waiting out the back-off.</summary>
    public void RetryNow() => _retryAt = 0f;

    /// <summary>A session is up: the next disconnect starts with short waits again.</summary>
    public void SessionEstablished() => _failures = 0;

    /// <summary>
    /// The session could not be set up, or a running one ended (SteamVR closed, headset disconnected): schedule the
    /// next attempt. Never the shortest wait, so quitting SteamVR does not bring it straight back.
    /// </summary>
    public void Disconnected(float now)
    {
        _failures = Math.Max(_failures + 1, 2);
        _retryAt = now + Backoff();
        AttemptEnded(now);
    }

    private float Backoff() => Math.Min(MaxRetrySeconds, BaseDelay * (1 << Math.Clamp(_failures - 1, 0, 10)));

    /// <param name="steamVrUp">Started because SteamVR was seen running (nothing to launch), not because the wait was over.</param>
    private void Start(bool steamVrUp)
    {
        _attempts++;
        _connecting = true;
        _attemptWithSteamVrUp = steamVrUp;
        _done = false;
        InvalidateProbe();
        string msg = $"Connecting to OpenXR (attempt {_attempts}{(steamVrUp ? ", SteamVR is running" : "")})...";
        if (_failures < FailuresLogged) Log.Info(msg);
        else Log.Verbose(msg);
        bool validation = Settings.EnableValidationLayer.Value;
        new Thread(() =>
        {
            try { _result = XrBootstrap.Create(_pluginDir, validation); }
            catch (Exception ex) { _error = ex; }
            _done = true;
        }) { IsBackground = true, Name = "VibelightValley-XrConnect" }.Start();
    }

    private XrBootstrap Finish(float now)
    {
        _connecting = false;
        AttemptEnded(now);
        var boot = _result;
        var error = _error;
        _result = null;
        _error = null;
        if (error == null) return boot;

        _failures++;
        float delay = Backoff();
        _retryAt = now + delay;
        LogFailure(error, delay);
        return null;
    }

    private void AttemptEnded(float now)
    {
        _lastAttemptEnd = now;
        // A vrserver our own attempt started is gone again by the time SteamVR gives up, but give it a moment.
        _nextProbe = now + SteamVrPollSeconds;
        InvalidateProbe();
    }

    private void LogFailure(Exception ex, float delay)
    {
        bool noHeadset = ex is XrException { Result: XrResult.ErrorRuntimeFailure or XrResult.ErrorFormFactorUnavailable or XrResult.ErrorRuntimeUnavailable };
        string next = _attemptWithSteamVrUp
            ? $"SteamVR is running, so the next try is in {BaseDelay:F0} s."
            : $"Next try in {delay:F0} s, sooner if SteamVR starts; F5 tries right away.";
        string msg = noHeadset
            ? $"VR not available: headset not connected or SteamVR not ready ({ex.Message}; see Steam\\logs\\vrserver.txt). The game runs flat. {next}"
            : $"OpenXR connect failed: {ex.Message}. {next}";
        // The stack trace only helps for failures that are not simply "no headset".
        if (_failures == 1 && !noHeadset) Log.Error("OpenXR connect failure detail", ex);
        if (_failures < FailuresLogged) Log.Warn(msg);
        else if (_failures == FailuresLogged) Log.Warn(msg + " Further failed attempts are only in the verbose log.");
        else Log.Verbose(msg);
    }

    private void ProbeSteamVr(float now)
    {
        if (now < _nextProbe) return;
        _nextProbe = now + SteamVrPollSeconds;
        int generation = _probeGeneration;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool running = SteamVrIsRunning();
            if (generation == Volatile.Read(ref _probeGeneration)) _steamVrRunning = running;
        });
    }

    private void InvalidateProbe()
    {
        Interlocked.Increment(ref _probeGeneration);
        _steamVrRunning = false;
    }

    private static bool SteamVrIsRunning()
    {
        try
        {
            var found = Process.GetProcessesByName("vrserver");
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }
        catch { return false; }
    }
}
