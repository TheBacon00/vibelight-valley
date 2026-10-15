using System;
using System.Diagnostics;
using UnityEngine;

namespace VibelightValley.Diagnostics;

/// <summary>
/// Splits each frame into where the time goes, to tell "CPU-bound game logic" from "CPU-bound rendering" from
/// "GPU-bound". Main thread timeline per frame:
///   [end of previous frame] -- logic --> [first render context] -- render (CPU draw submission) --> ...
///   ... --> [end of frame] -- compose (our blits/submit) --> [next frame]
/// The xrWaitFrame wait happens inside the first context's handler and is reported separately (not in render).
/// GPU and render-thread times come from Unity's FrameTimingManager when the build exposes it.
/// Summarised every 5 s by <see cref="Summary"/>.
/// </summary>
public static class FrameProfiler
{
    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    private static long _frameEnd, _ctxStart, _handlersDone;
    private static bool _sawContext;

    private struct Acc
    {
        public double Sum, Max;
        public void Add(double v) { Sum += v; if (v > Max) Max = v; }
    }

    private static Acc _logic, _render, _wait, _compose, _gpu, _renderThread;
    private static int _frames, _gpuFrames;
    private static int _timingState; // 0 = unknown, 1 = available, -1 = unavailable
    // An IL2CPP array: a managed FrameTiming[] would be copied on the call and never receive the results.
    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<FrameTiming> Timings = new(1);

    /// <summary>PipelineMonitor: a render context starts (before VR handlers run).</summary>
    public static void ContextBegin()
    {
        long now = Stopwatch.GetTimestamp();
        if (!_sawContext && _frameEnd != 0) _logic.Add((now - _frameEnd) * TickMs);
        _sawContext = true;
        _ctxStart = now;
    }

    /// <summary>PipelineMonitor: VR handlers (incl. xrWaitFrame) finished; URP starts submitting.</summary>
    public static void ContextHandlersDone()
    {
        _handlersDone = Stopwatch.GetTimestamp();
        _wait.Add((_handlersDone - _ctxStart) * TickMs);
    }

    /// <summary>PipelineMonitor: a render context ended.</summary>
    public static void ContextEnd() => _render.Add((Stopwatch.GetTimestamp() - _handlersDone) * TickMs);

    private static long _composeStart;
    public static void ComposeBegin() => _composeStart = Stopwatch.GetTimestamp();

    /// <summary>End of our end-of-frame work: closes the frame.</summary>
    public static void FrameEnd()
    {
        _frameEnd = Stopwatch.GetTimestamp();
        if (_composeStart != 0) _compose.Add((_frameEnd - _composeStart) * TickMs);
        _composeStart = 0;
        _sawContext = false;
        _frames++;
        SampleUnityTimings();
    }

    private static void SampleUnityTimings()
    {
        if (_timingState < 0) return;
        try
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, Timings) == 0) { if (_timingState == 0 && _frames > 300) _timingState = -1; return; }
            _timingState = 1;
            if (Timings[0].gpuFrameTime > 0) { _gpu.Add(Timings[0].gpuFrameTime); _gpuFrames++; }
            _renderThread.Add(Timings[0].cpuRenderThreadFrameTime);
        }
        catch { _timingState = -1; }
    }

    /// <summary>Averages (and worst) per frame since the last call; resets the counters.</summary>
    public static string Summary()
    {
        if (_frames == 0) return "no frames";
        string F(Acc a, int n) => n == 0 ? "-" : $"{a.Sum / n:F1}/{a.Max:F1}";
        string s = $"PERF ms avg/max: logic={F(_logic, _frames)} render(cpu)={F(_render, _frames)} xrWait+handlers={F(_wait, _frames)} " +
                   $"compose={F(_compose, _frames)}";
        s += _timingState == 1 ? $" gpu={F(_gpu, _gpuFrames)} renderThread={F(_renderThread, _frames)}" : " gpu=n/a (FrameTimingManager disabled in this build)";
        _logic = _render = _wait = _compose = _gpu = _renderThread = default;
        _frames = _gpuFrames = 0;
        return s;
    }
}
