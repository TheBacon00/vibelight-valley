using System;
using System.Collections.Generic;
using System.Linq;
using VibelightValley.Core;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using CameraList = Il2CppSystem.Collections.Generic.List<UnityEngine.Camera>;

namespace VibelightValley.Diagnostics;

/// <summary>
/// Hooks URP's render callbacks. <see cref="BeginContext"/> is what the VR frame loop uses (it fires after all game
/// scripts ran, right before rendering). The per-camera hook records which cameras render each frame and logs the
/// list whenever it changes - this is how we learn which camera the game uses for what.
/// </summary>
public static class PipelineMonitor
{
    public static event Action<ScriptableRenderContext, CameraList> BeginContext;
    public static event Action<CameraList> EndContext;
    public static event Action<Camera> BeginCamera, EndCamera;

    // IL2CPP holds these delegates; keep managed references so they are never collected.
    private static Il2CppSystem.Action<ScriptableRenderContext, CameraList> _beginCtx, _endCtx;
    private static Il2CppSystem.Action<ScriptableRenderContext, Camera> _beginCam, _endCam;

    // Cameras rendered this frame vs. last logged list. Instance ids only, so nothing allocates per frame.
    private static readonly List<Camera> FrameCams = new();
    private static readonly List<int> FrameIds = new();
    private static readonly List<int> LoggedIds = new();
    private static int _contextsThisFrame;

    public static void Install()
    {
        try
        {
            _beginCtx = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, CameraList>>(
                new Action<ScriptableRenderContext, CameraList>(OnBeginContext));
            RenderPipelineManager.add_beginContextRendering(_beginCtx);
            _endCtx = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, CameraList>>(
                new Action<ScriptableRenderContext, CameraList>(OnEndContext));
            RenderPipelineManager.add_endContextRendering(_endCtx);
            _beginCam = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                new Action<ScriptableRenderContext, Camera>(OnBeginCamera));
            RenderPipelineManager.add_beginCameraRendering(_beginCam);
            _endCam = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                new Action<ScriptableRenderContext, Camera>(OnEndCamera));
            RenderPipelineManager.add_endCameraRendering(_endCam);
            Log.Info("PipelineMonitor: render pipeline callbacks hooked");
        }
        catch (Exception ex) { Log.Error("PipelineMonitor: hooking render callbacks failed", ex); }
    }

    /// <summary>Called at the start of every Update: logs last frame's camera list if it changed.</summary>
    public static void NewFrame()
    {
        if (FrameIds.Count > 0 && !FrameIds.SequenceEqual(LoggedIds))
        {
            LoggedIds.Clear();
            LoggedIds.AddRange(FrameIds);
            var names = FrameCams.Select(c => c == null ? "<destroyed>"
                : c.targetTexture != null ? $"{c.name}->RT({c.targetTexture.width}x{c.targetTexture.height})" : c.name);
            Log.Info($"Cameras rendering ({_contextsThisFrame} contexts): {string.Join(" | ", names)}");
        }
        FrameCams.Clear();
        FrameIds.Clear();
        _contextsThisFrame = 0;
    }

    private static void OnBeginContext(ScriptableRenderContext ctx, CameraList cams)
    {
        _contextsThisFrame++;
        FrameProfiler.ContextBegin();
        try { BeginContext?.Invoke(ctx, cams); }
        catch (Exception ex) { Log.Limited("beginctx", 20, $"BeginContext handler: {ex}", 'E'); }
        FrameProfiler.ContextHandlersDone();
    }

    private static void OnEndContext(ScriptableRenderContext ctx, CameraList cams)
    {
        FrameProfiler.ContextEnd();
        try { EndContext?.Invoke(cams); }
        catch (Exception ex) { Log.Limited("endctx", 20, $"EndContext handler: {ex}", 'E'); }
    }

    private static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam == null || cam.cameraType != CameraType.Game) return;
        FrameCams.Add(cam);
        FrameIds.Add(cam.GetInstanceID());
        try { BeginCamera?.Invoke(cam); }
        catch (Exception ex) { Log.Limited("begincam", 20, $"BeginCamera handler: {ex}", 'E'); }
    }

    private static void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
    {
        try { EndCamera?.Invoke(cam); }
        catch (Exception ex) { Log.Limited("endcam", 20, $"EndCamera handler: {ex}", 'E'); }
    }
}
