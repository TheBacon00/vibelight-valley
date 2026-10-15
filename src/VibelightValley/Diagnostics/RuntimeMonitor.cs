using System;
using System.Threading;
using VibelightValley.Core;
using VibelightValley.Gfx;
using VibelightValley.Xr;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VibelightValley.Diagnostics;

/// <summary>
/// Startup checks and lightweight ongoing logging that is independent of VR: startup report, API probe,
/// render-thread round trip, scene / Camera.main changes, a 10 s fps line, and the Diagnostics-mode OpenXR probe.
/// </summary>
public static class RuntimeMonitor
{
    private static string _lastScene = "";
    private static int _lastMainCamId;
    private static float _cameraDumpAt = -1;
    private static int _pingFrame = -1;
    private static bool _xrProbeStarted;
    private static int _startFrame;

    private static float _statsAt;
    private static int _statsFrames;
    private static float _statsTime, _worstDt;

    private static readonly Hotkey DumpKey = new("F9", Hotkey.VK.F9);

    public static void Startup()
    {
        _startFrame = Time.frameCount;
        Log.Info($"First Update at frame {Time.frameCount}, t={Time.realtimeSinceStartup:F1}s");
        Log.Info($"OpenXR struct layout: {XrLayoutCheck.Report()}");
        if (!XrLayoutCheck.AllMatch()) Log.Error("OpenXR struct layout mismatch! VR will not work correctly.");
        Log.Block("Startup report", Report.Startup());

        bool apisOk = Report.ProbeApis(out string apiText);
        Log.Block("API probe", apiText);
        Log.Info($"API probe: {(apisOk ? "all critical APIs OK" : "CRITICAL API MISSING (see API probe block)")}");

        GfxDevice.Acquire();
        PipelineMonitor.Install();

        try
        {
            RenderThread.Issue(RenderThread.Op.Ping);
            _pingFrame = Time.frameCount;
        }
        catch (Exception ex) { Log.Error("RenderThread ping failed to issue", ex); }
    }

    public static void Update()
    {
        PingCheck();
        TrackScene();
        InputMonitor.Update();
        CanvasMonitor.Update();
        FpsStats();

        if (DumpKey.Pressed()) FullDump();
        if (_cameraDumpAt > 0 && Time.realtimeSinceStartup >= _cameraDumpAt)
        {
            _cameraDumpAt = -1;
            Log.Block($"Cameras after scene change ('{_lastScene}')", Report.Cameras(false));
        }
        if (!_xrProbeStarted && Settings.Mode.Value == VrMode.Diagnostics && Settings.ProbeOpenXR.Value && Time.frameCount - _startFrame > 120)
        {
            _xrProbeStarted = true;
            new Thread(XrProbe) { IsBackground = true, Name = "VibelightValley-XrProbe" }.Start();
        }
    }

    /// <summary>Verifies GL.IssuePluginEvent reaches Unity's render thread (the whole VR submit path depends on it).</summary>
    private static void PingCheck()
    {
        if (_pingFrame < 0) return;
        if (RenderThread.PingsReceived > 0)
        {
            Log.Info($"RenderThread: ping round-trip OK after {Time.frameCount - _pingFrame} frames; fence {(RenderThread.WaitForFence(1000) ? "OK" : "FAILED")}");
            _pingFrame = -1;
        }
        else if (Time.frameCount - _pingFrame > 120)
        {
            Log.Error("RenderThread: ping NOT received after 120 frames - plugin events do not reach the render thread");
            _pingFrame = -1;
        }
    }

    private static void TrackScene()
    {
        if (Time.frameCount % 15 != 0) return; // scene name marshals a string; no need to check every frame
        string scene = SceneManager.GetActiveScene().name;
        if (scene != _lastScene)
        {
            Log.Info($"Active scene: '{_lastScene}' -> '{scene}' ({SceneManager.sceneCount} loaded)");
            _lastScene = scene;
            _cameraDumpAt = Time.realtimeSinceStartup + 3f;
        }
        var main = Camera.main;
        int id = main == null ? 0 : main.GetInstanceID();
        if (id == _lastMainCamId) return;
        _lastMainCamId = id;
        Log.Info($"Camera.main: {(main == null ? "null" : Report.PathOf(main.transform))}");
    }

    private static void FpsStats()
    {
        float dt = Time.unscaledDeltaTime;
        _statsFrames++;
        _statsTime += dt;
        _worstDt = Mathf.Max(_worstDt, dt);
        if (Time.realtimeSinceStartup < _statsAt) return;
        _statsAt = Time.realtimeSinceStartup + 10f;
        if (_statsTime > 0)
            Log.Info($"STATS fps={_statsFrames / _statsTime:F1} worstFrame={_worstDt * 1000:F1}ms scene='{_lastScene}'");
        GfxDevice.CheckRemoved("stats");
        _statsFrames = 0; _statsTime = 0; _worstDt = 0;
    }

    private static void FullDump()
    {
        Log.Info("F9: full dump");
        Log.Block("Pipeline", Report.Pipeline());
        Log.Block("Cameras (incl. inactive)", Report.Cameras(true));
        Log.Block("Canvases", Report.Canvases());
        Log.Block("Volumes", Report.Volumes());
    }

    /// <summary>Diagnostics mode only: create and destroy an OpenXR instance to log runtime/headset details.</summary>
    private static void XrProbe()
    {
        try
        {
            using var b = XrBootstrap.Create(Plugin.PluginDir, Settings.EnableValidationLayer.Value);
            if (GfxDevice.Device != IntPtr.Zero)
                Log.Info($"OpenXR probe: headset GPU {(b.GraphicsRequirements.AdapterLuid == GfxDevice.Adapter.Luid ? "matches" : "DIFFERS FROM")} the game GPU; " +
                         $"feature level {D3D11.FeatureLevelName(GfxDevice.FeatureLevel)} (required {D3D11.FeatureLevelName(b.GraphicsRequirements.MinFeatureLevel)})");
            Log.Info("OpenXR probe finished OK");
        }
        catch (Exception ex) { Log.Error("OpenXR probe failed", ex); }
    }
}
