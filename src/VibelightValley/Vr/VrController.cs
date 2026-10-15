using System;
using System.Collections;
using System.IO;
using System.Threading;
using BepInEx.Configuration;
using VibelightValley.Core;
using VibelightValley.Diagnostics;
using VibelightValley.Gfx;
using VibelightValley.Xr;
using UnityEngine;
using UnityEngine.Rendering;

namespace VibelightValley.Vr;

/// <summary>
/// Owns the VR session lifecycle and drives each frame.
///
/// Frame flow (one Unity frame = one OpenXR frame):
///   Update               poll OpenXR events, hotkeys, choose the camera to follow, toggle the HUD layer
///   beginContextRendering (first render context that contains our eye cameras, i.e. after the game moved its
///                        camera): xrWaitFrame + locate views, place the eyes, queue xrBeginFrame
///   WaitForEndOfFrame    if nothing waited yet (no world view this frame) wait now; compose layers; queue xrEndFrame
/// The render thread then runs BeginFrame/EndFrame in order (see XrSession).
/// </summary>
public sealed class VrController
{
    private enum Phase { Disconnected, Connecting, Running, Failed }

    private Phase _phase = Phase.Disconnected;
    private float _nextConnectAttempt;
    private int _connectAttempts;
    private volatile bool _connectDone;   // set by the connect thread
    private XrBootstrap _connectResult;
    private Exception _connectError;

    private XrSession _xr;
    private VrRig _rig;
    private HudLayer _hud;
    private FrameComposer _composer;

    private int _slot = -1;               // ring slot waited this Unity frame (-1 = none open)
    private int _slotFrame = -1;          // Time.frameCount of that wait
    private int _tracedFrames;
    private int _consecutiveErrors;
    private float _statsAt;
    private long _lastWaits;
    private int _savedVSync = -1;

    // User-toggled state
    private bool _vrEnabled = true;
    private bool _testPattern;
    private bool _screenToggle;
    private bool _hudEnabled = Settings.Hud.Value;
    private bool _wantRecenter = true;

    private readonly (Hotkey key, Action action)[] _hotkeys;
    private readonly PadCombo _padHud = new(() => Settings.HudToggleButtons.Value);

    public VrController(bool testPattern)
    {
        _testPattern = testPattern;
        _hotkeys = new (Hotkey, Action)[]
        {
            (new Hotkey("F3", 0x72), () => Log.Info($"Hotkey F3: eye render effects: {_rig.CycleDiagnosticFeature()}")),
            (new Hotkey("F4", 0x73), ToggleHud),
            (new Hotkey("F5", Hotkey.VK.F5), Recenter),
            (new Hotkey("Num5", 0x65), Recenter),
            (new Hotkey("F6", Hotkey.VK.F6), () => Toggle(Settings.FlipEyesY)),
            (new Hotkey("F7", Hotkey.VK.F7), () => Toggle(Settings.FlipScreenY)),
            (new Hotkey("F8", Hotkey.VK.F8), () => Log.Info($"Hotkey: virtual screen {((_screenToggle = !_screenToggle) ? "ON" : "auto")}")),
            (new Hotkey("F10", Hotkey.VK.F10), DumpPngs),
            (new Hotkey("F11", Hotkey.VK.F11), () => Log.Info($"Hotkey: VR rendering {((_vrEnabled = !_vrEnabled) ? "ON" : "OFF")}")),
            (new Hotkey("F12", Hotkey.VK.F12), () => Log.Info($"Hotkey: test pattern {((_testPattern = !_testPattern) ? "ON" : "OFF")}")),
            // Viewpoint position: in character mode these change follow distance/height, otherwise the camera offset.
            (new Hotkey("Num8", 0x68), () => { if (CharacterMode) Nudge(Settings.FollowDistance, -0.25f); else Nudge(Settings.AnchorForward, +0.25f); }),
            (new Hotkey("Num2", 0x62), () => { if (CharacterMode) Nudge(Settings.FollowDistance, +0.25f); else Nudge(Settings.AnchorForward, -0.25f); }),
            (new Hotkey("Num9", 0x69), () => Nudge(CharacterMode ? Settings.FollowHeight : Settings.AnchorUp, +0.25f)),
            (new Hotkey("Num3", 0x63), () => Nudge(CharacterMode ? Settings.FollowHeight : Settings.AnchorUp, -0.25f)),
            (new Hotkey("Num7", 0x67), () => Nudge(Settings.HudPitchOffset, +2.5f)),
            (new Hotkey("Num1", 0x61), () => Nudge(Settings.HudPitchOffset, -2.5f)),
            (new Hotkey("PageUp", Hotkey.VK.PageUp), () => ChangeRenderScale(+0.1f)),
            (new Hotkey("PageDown", Hotkey.VK.PageDown), () => ChangeRenderScale(-0.1f)),
            (new Hotkey("Num+", Hotkey.VK.Add), () => Scale(1.25f)),
            (new Hotkey("Num-", Hotkey.VK.Subtract), () => Scale(1f / 1.25f)),
        };
        PipelineMonitor.BeginContext += OnBeginContext;
        RenderThread.Handler = OnRenderThread;
        Log.Info($"VrController created (testPattern={testPattern}). Keys: F4 HUD, F5/Num5 recenter, F6/F7 flip eyes/screen, F8 screen, " +
                 "F10 PNG dump, F11 VR on/off, F12 test pattern, Num8/2 fwd/back, Num9/3 up/down, Num+/- scale; both sticks = HUD");
    }

    // ================================================================== lifecycle

    public void Update()
    {
        switch (_phase)
        {
            case Phase.Disconnected when Time.realtimeSinceStartup >= _nextConnectAttempt:
                StartConnect();
                break;
            case Phase.Connecting when _connectDone:
                FinishConnect();
                break;
            case Phase.Running:
                RunningUpdate();
                break;
        }
    }

    /// <summary>xrCreateInstance can take seconds (it may launch SteamVR), so it runs on a background thread.</summary>
    private void StartConnect()
    {
        if (!GfxDevice.Acquire()) { _phase = Phase.Failed; Log.Error("VR disabled: no usable D3D11 device"); return; }
        _connectAttempts++;
        _phase = Phase.Connecting;
        _connectDone = false;
        Log.Info($"Connecting to OpenXR (attempt {_connectAttempts})...");
        new Thread(() =>
        {
            try { _connectResult = XrBootstrap.Create(Plugin.PluginDir, Settings.EnableValidationLayer.Value); }
            catch (Exception ex) { _connectError = ex; }
            _connectDone = true;
        }) { IsBackground = true, Name = "VibelightValley-XrConnect" }.Start();
    }

    private void FinishConnect()
    {
        if (_connectError != null)
        {
            var ex = _connectError;
            _connectError = null;
            bool noHeadset = ex is XrException { Result: XrResult.ErrorRuntimeFailure or XrResult.ErrorFormFactorUnavailable or XrResult.ErrorRuntimeUnavailable };
            Log.Limited("connectfail", 5, $"OpenXR connect failed: {ex.Message}" +
                (noHeadset ? " -> headset not connected or SteamVR not ready (see Steam\\logs\\vrserver.txt)." : "") +
                $" Retrying every {Settings.ConnectRetrySeconds.Value}s.", 'W');
            if (_connectAttempts == 1) Log.Error("OpenXR connect failure detail", ex);
            RetryLater(2f);
            return;
        }

        var boot = _connectResult;
        _connectResult = null;
        try
        {
            if (boot.GraphicsRequirements.AdapterLuid != GfxDevice.Adapter.Luid)
                Log.Warn("The headset runs on a different GPU than the game. Expect poor performance or a failed session.");

            var eye = EyeSizeFor(boot.Views);
            var hud = (w: Math.Clamp(UnityEngine.Screen.width, 256, 4096) & ~1, h: Math.Clamp(UnityEngine.Screen.height, 256, 4096) & ~1);
            var screen = (w: 1920, h: 1080);
            _eye = eye;
            Log.Info($"Eye resolution {eye.w}x{eye.h} (SteamVR recommends {boot.Views[0].RecommendedImageRectWidth}x{boot.Views[0].RecommendedImageRectHeight}, " +
                     $"RenderScale {Settings.RenderScale.Value}), HUD {hud.w}x{hud.h}");

            _xr = XrSession.Create(boot, GfxDevice.Device, eye, hud, screen, Settings.PreferSrgbSwapchain.Value);
            _rig = new VrRig(eye.w, eye.h, _xr.ColorFormat);
            _hud = new HudLayer();
            _composer = new FrameComposer(eye, hud, screen, _xr.ColorFormat);
            _phase = Phase.Running;
            _tracedFrames = 0;
            _wantRecenter = true;
            ApplyGameSettings(vrRunning: true);
            Log.Info("VR RUNNING - waiting for the OpenXR session to become READY");
        }
        catch (Exception ex)
        {
            Log.Error("VR session setup failed", ex);
            TearDown();
            boot?.Dispose();
            RetryLater(5f);
        }
    }

    /// <summary>Per-eye render size: SteamVR's recommendation (largest of both eyes) x RenderScale, clamped, even.</summary>
    private static (int w, int h) EyeSizeFor(XrViewConfigurationView[] v)
    {
        float s = Settings.RenderScale.Value;
        int w = (int)(Math.Max(v[0].RecommendedImageRectWidth, v[1].RecommendedImageRectWidth) * s);
        int h = (int)(Math.Max(v[0].RecommendedImageRectHeight, v[1].RecommendedImageRectHeight) * s);
        return (Math.Clamp(w, 256, (int)v[0].MaxImageRectWidth) & ~1, Math.Clamp(h, 256, (int)v[0].MaxImageRectHeight) & ~1);
    }

    // ------------------------------------------------------------------ live resolution
    // SteamVR's resolution slider (with XR_EXT_view_configuration_views_change) and RenderScale (PageUp/PageDown)
    // can change the eye size during play. Resizing swaps swapchains and render textures, so it only happens in
    // Update, with no OpenXR frame open, after the render thread has finished everything queued so far.
    private (int w, int h) _eye;
    private float _nextViewPoll;
    private bool _resizeWanted;

    private void CheckEyeSize()
    {
        if (!_resizeWanted && Time.realtimeSinceStartup < _nextViewPoll) return;
        _nextViewPoll = Time.realtimeSinceStartup + 2f;
        _resizeWanted = false;
        var want = EyeSizeFor(_xr.Boot.QueryViews());
        if (want == _eye) return;
        if (_slot >= 0 || !RenderThread.WaitForFence(1000))
        {
            _resizeWanted = true; // try again next frame
            return;
        }
        Log.Info($"Eye resolution {_eye.w}x{_eye.h} -> {want.w}x{want.h} (RenderScale {Settings.RenderScale.Value})");
        _xr.ResizeEyes(want.w, want.h);
        _rig.Resize(want.w, want.h);
        _composer.ResizeEyes(want.w, want.h);
        _eye = want;
    }

    private void ChangeRenderScale(float delta)
    {
        Settings.RenderScale.Value = Mathf.Clamp(Mathf.Round((Settings.RenderScale.Value + delta) * 10f) / 10f, 0.4f, 2f);
        _resizeWanted = true;
        Log.Info($"Hotkey: RenderScale={Settings.RenderScale.Value}");
    }

    private void RetryLater(float minSeconds)
    {
        _phase = Phase.Disconnected;
        _nextConnectAttempt = Time.realtimeSinceStartup + Mathf.Max(minSeconds, Settings.ConnectRetrySeconds.Value);
    }

    /// <summary>VSync off (the headset paces the game via xrWaitFrame) and no exclusive fullscreen while VR runs.</summary>
    private void ApplyGameSettings(bool vrRunning)
    {
        try
        {
            if (vrRunning && Settings.DisableVSync.Value)
            {
                if (_savedVSync < 0) _savedVSync = QualitySettings.vSyncCount;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            else if (!vrRunning && _savedVSync >= 0)
            {
                QualitySettings.vSyncCount = _savedVSync;
                _savedVSync = -1;
            }
            if (vrRunning && UnityEngine.Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen)
            {
                Log.Warn("Exclusive fullscreen detected - switching to borderless window for VR");
                UnityEngine.Screen.fullScreenMode = FullScreenMode.FullScreenWindow;
            }
            Application.runInBackground = true;
        }
        catch (Exception ex) { Log.Warn($"ApplyGameSettings: {ex.Message}"); }
    }

    private void RunningUpdate()
    {
        if (!_xr.PollEvents())
        {
            Log.Warn("OpenXR session ended (SteamVR closed or headset lost) - tearing down; will reconnect");
            TearDown();
            RetryLater(5f);
            return;
        }
        if (_xr.RecenterRequested) { _xr.RecenterRequested = false; _wantRecenter = true; }

        foreach (var (key, action) in _hotkeys)
            if (key.Pressed()) action();
        if (_padHud.Pressed()) ToggleHud();
        CheckEyeSize();

        bool eyesWanted = _vrEnabled && !_testPattern && _xr.Running
                          && _xr.State is XrSessionState.Synchronized or XrSessionState.Visible or XrSessionState.Focused;
        _rig.Prepare(eyesWanted);
        // Overlay UI always goes through the UI camera, except in the world with the HUD switched off: the desktop
        // capture used for menus/title does not contain overlay UI (e.g. the menu tab bar).
        _hud.Update(_xr.Running && (_hudEnabled || !_rig.Active));
        Stats();
    }

    // ================================================================== frame

    private bool InFrameLoop => _phase == Phase.Running && _xr != null && _xr.Running;
    private bool Tracing => _tracedFrames < Settings.DetailedFrameTraceCount.Value;

    private void OnBeginContext(ScriptableRenderContext ctx, Il2CppSystem.Collections.Generic.List<Camera> cams)
    {
        if (!InFrameLoop || !_rig.Active || _slotFrame == Time.frameCount) return;
        // Unity renders off-screen cameras (ours) and on-screen cameras in separate contexts; wait in ours.
        int eyeId = _rig.Eyes[0].GetInstanceID();
        for (int i = 0; i < cams.Count; i++)
        {
            var c = cams[i];
            if (c == null || c.GetInstanceID() != eyeId) continue;
            try { WaitAndBegin(placeEyes: true); }
            catch (Exception ex) { Error("WaitAndBegin", ex); }
            return;
        }
    }

    private void WaitAndBegin(bool placeEyes)
    {
        if (_slot >= 0)
        {
            // A waited frame was never ended (exception, skipped coroutine). End it empty or the runtime stalls.
            ref FrameRecord stale = ref _xr.Frame(_slot);
            stale.SubmitProjection = false;
            stale.Hud = default;
            stale.Screen = default;
            _xr.IssueEnd(_slot);
            Log.Limited("flush", 10, $"Flushed unfinished OpenXR frame (slot {_slot}, Unity frame {stale.UnityFrame})", 'W');
        }

        _slotFrame = Time.frameCount;
        _slot = _xr.WaitFrame(out var rec);
        if (_slot < 0) return;

        if (rec.PosesValid && _wantRecenter && _xr.LocateHead(rec.DisplayTime, out var head))
        {
            _rig.Recenter(head);
            _wantRecenter = false;
        }
        rec.SubmitProjection = placeEyes && rec.PosesValid && _rig.HasOrigin;
        if (rec.SubmitProjection) _rig.ApplyViews(ref rec.View0, ref rec.View1);
        rec.UnityFrame = Time.frameCount;
        _xr.StoreAndBegin(_slot, rec);

        if (Tracing)
            Log.Verbose($"FRAME wait slot={_slot} render={rec.ShouldRender} poses={rec.PosesValid} eyes={rec.SubmitProjection} " +
                        $"L={rec.View0.Pose} R={rec.View1.Pose} fovL={rec.View0.Fov}");
    }

    public IEnumerator EndOfFrameLoop()
    {
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            FrameProfiler.ComposeBegin();
            try { OnEndOfFrame(); }
            catch (Exception ex) { Error("OnEndOfFrame", ex); }
            FrameProfiler.FrameEnd();
        }
    }

    private void OnEndOfFrame()
    {
        if (!InFrameLoop) return;
        // No world render this frame (title screen, loading, menu, VR off): still run one OpenXR frame.
        if (_slotFrame != Time.frameCount) WaitAndBegin(placeEyes: false);
        if (_slot < 0) return;

        ref FrameRecord f = ref _xr.Frame(_slot);
        _composer.Compose(ref f, _rig, _hud, _testPattern, _screenToggle);
        _xr.IssueEnd(_slot);
        if (Tracing)
        {
            Log.Verbose($"FRAME end slot={_slot} projection={f.SubmitProjection} hud={f.Hud.Submit} screen={f.Screen.Submit}");
            _tracedFrames++;
        }
        _slot = -1;
        _consecutiveErrors = 0;
    }

    /// <summary>Render thread. Must not touch IL2CPP objects (see RenderThread).</summary>
    private void OnRenderThread(RenderThread.Op op, int slot)
    {
        var xr = _xr;
        if (xr == null) return;
        if (op == RenderThread.Op.BeginFrame) xr.RT_BeginFrame(slot);
        else if (op == RenderThread.Op.EndFrame) xr.RT_EndFrame(slot, GfxDevice.Context);
    }

    // ================================================================== hotkey actions

    private static bool CharacterMode => Settings.Follow.Value == FollowMode.Character;

    private void ToggleHud()
    {
        _hudEnabled = !_hudEnabled;
        Settings.Hud.Value = _hudEnabled;
        Log.Info($"HUD layer {(_hudEnabled ? "ON" : "OFF")}");
    }

    private void Recenter()
    {
        _wantRecenter = true;
        Log.Info("Hotkey: recenter");
    }

    private static void Toggle(ConfigEntry<bool> e)
    {
        e.Value = !e.Value;
        Log.Info($"Hotkey: {e.Definition.Key}={e.Value}");
    }

    private static void Nudge(ConfigEntry<float> e, float delta)
    {
        e.Value = Mathf.Round((e.Value + delta) * 100f) / 100f;
        Log.Info($"Hotkey: {e.Definition.Key}={e.Value}");
    }

    private static void Scale(float factor)
    {
        Settings.WorldScale.Value = Mathf.Round(Settings.WorldScale.Value * factor * 100f) / 100f;
        Log.Info($"Hotkey: WorldScale={Settings.WorldScale.Value}");
    }

    // ================================================================== diagnostics

    private void Stats()
    {
        if (Time.realtimeSinceStartup < _statsAt) return;
        _statsAt = Time.realtimeSinceStartup + 5f;
        var x = _xr;
        long waits = x.Waits - _lastWaits;
        _lastWaits = x.Waits;
        Log.Info($"VRSTATS state={x.State} xrFps={waits / 5f:F1} waitAvg={(x.Waits > 0 ? x.WaitMsTotal / x.Waits : 0):F2}ms waitMax={x.WaitMsMax:F1}ms " +
                 $"period={x.LastPredictedPeriod / 1e6:F2}ms begins={x.Begins} ends={x.Ends} endFail={x.EndFailures} discarded={x.Discarded} " +
                 $"noRender={x.ShouldRenderFalse} layers proj/hud/screen/vignette={x.ProjectionSubmits}/{x.HudSubmits}/{x.ScreenSubmits}/{x.VignetteSubmits}" +
                 $"copyFail={x.Eyes[0].Failures + x.Eyes[1].Failures + x.HudChain.Failures + x.ScreenChain.Failures} " +
                 $"eyes={(_rig.Active ? "on" : "off")} hud={(_hud.Active ? "on" : "off")} test={_testPattern} screenToggle={_screenToggle} " +
                 $"scale={Settings.WorldScale.Value} anchor F/U={Settings.AnchorForward.Value}/{Settings.AnchorUp.Value}");
        Log.Info($"RIG {_rig.DebugLine()} turn={_rig.TurnSpeed:F0}deg/s vignettePeak={_composer.PeakVignette:F2} follow={Settings.Follow.Value}");
        _composer.PeakVignette = 0;
        Log.Info(FrameProfiler.Summary() + $" eye={_eye.w}x{_eye.h} shadows={Settings.EyeShadows.Value} drawDist={Settings.EyeDrawDistance.Value}");
        x.WaitMsMax = 0;
        GfxDevice.CheckRemoved("vrstats");
    }

    private void DumpPngs()
    {
        try
        {
            string dir = Path.Combine(Log.Dir, "dumps", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            _composer.Dump(dir, _rig, _hud);
            Log.Info($"PNG dump written to {dir}");
        }
        catch (Exception ex) { Log.Error("PNG dump", ex); }
    }

    /// <summary>Many consecutive errors means something is fundamentally broken: drop VR, keep the game running.</summary>
    private void Error(string where, Exception ex)
    {
        _consecutiveErrors++;
        Log.Limited("vrerr-" + where, 20, $"{where}: {ex}", 'E');
        if (_consecutiveErrors <= 200 || _phase != Phase.Running) return;
        Log.Error($"VR DISABLED after {_consecutiveErrors} consecutive errors (last in {where}). The game continues flat.");
        TearDown();
        _phase = Phase.Failed;
    }

    public void TearDown()
    {
        _phase = Phase.Failed; // callers that want to reconnect set Disconnected afterwards (RetryLater)
        try
        {
            Log.Info("VR teardown");
            _hud?.Destroy();          // restores the game's canvases first
            _xr?.Dispose();           // fences the render thread before destroying swapchains
            _rig?.Destroy();
            _composer?.Destroy();
            ApplyGameSettings(vrRunning: false);
        }
        catch (Exception ex) { Log.Error("TearDown", ex); }
        _hud = null; _xr = null; _rig = null; _composer = null;
        _slot = -1;
    }
}
