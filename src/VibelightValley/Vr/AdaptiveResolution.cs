using System;
using VibelightValley.Core;
using UnityEngine;

namespace VibelightValley.Vr;

/// <summary>
/// Keeps the GPU inside the headset's frame budget by lowering the eyes' internal render resolution under load and
/// raising it again when there is room: an autoscaler whose SLO is the frame time.
///
/// Mechanism: VrRig applies <see cref="Scale"/> as URP's renderScale only while an eye camera renders, so URP draws the
/// eye at Scale x the eye texture size and upscales into it. Eye textures and OpenXR swapchains keep their size; a change
/// only makes URP reallocate its intermediate textures (a small hitch), so changes are stepped and rate-limited.
///
/// Signal: Unity's GPU frame time. A lower resolution only helps while the GPU is what limits the frame rate, so it
/// steps down only when the GPU takes clearly longer than the busiest CPU thread.
/// </summary>
public sealed class AdaptiveResolution
{
    private static readonly float[] Levels = { 1f, 0.9f, 0.8f, 0.7f, 0.6f, 0.5f, 0.4f, 0.3f };
    private const float Headroom = 0.9f;          // aim for GPU time <= 90% of the frame budget
    private const float DownCooldown = 0.75f;     // s between steps down (new timings take a few frames to arrive)
    private const float UpAfter = 3f;             // s of sustained headroom before a step up
    private const float SettleAfterChange = 4f;   // s after any change before a step up
    private const float WarmUp = 2f;              // s after the eyes start rendering (scene loads cause spikes)
    private const float PixelShare = 0.75f;       // assumed share of the GPU time that scales with the pixel count

    private int _level;
    private float _gpu, _cpu;                     // smoothed ms
    private float _activeSince = -1f, _lastChange = -100f, _headroomSince = -1f;
    private bool _enabledSeen;

    // Stats for the PERF line, reset by Summary()
    private double _scaleSum;
    private int _scaleFrames;
    private float _scaleMin = 1f;

    /// <summary>Fixed scale used by the benchmark (0 = adaptive).</summary>
    public float Override;

    /// <summary>Internal render scale for the eyes this frame (fraction of the eye texture size, per axis).</summary>
    public float Scale => Override > 0f ? Override : Settings.AdaptiveResolution.Value ? Levels[_level] : 1f;

    /// <param name="budgetMs">Frame budget: the headset's refresh period.</param>
    /// <param name="gpuMs">Latest GPU frame time (0 = unknown).</param>
    /// <param name="cpuMs">Latest busy time of the busiest CPU thread, without waits (0 = unknown).</param>
    public void Update(bool eyesActive, float budgetMs, float gpuMs, float cpuMs)
    {
        float now = Time.realtimeSinceStartup;
        bool enabled = Settings.AdaptiveResolution.Value;
        if (enabled != _enabledSeen)
        {
            _enabledSeen = enabled;
            _level = 0;
            _headroomSince = -1f;
            Log.Info($"Adaptive resolution {(enabled ? $"ON (down to {Levels[LowestLevel()]:0.0#} of the eye resolution)" : "OFF (full eye resolution)")}");
        }
        if (eyesActive)
        {
            _scaleSum += Scale;
            _scaleFrames++;
            _scaleMin = Mathf.Min(_scaleMin, Scale);
        }
        else _activeSince = -1f;
        if (!enabled || Override > 0f || !eyesActive || budgetMs <= 0f || gpuMs <= 0f)
        {
            _headroomSince = -1f;
            return;
        }
        if (_activeSince < 0f) _activeSince = now;

        // Smoothed over ~7 frames; a one-off hitch is clamped so a single spike cannot cause a step.
        float g = Mathf.Min(gpuMs, budgetMs * 2f), c = Mathf.Min(cpuMs, budgetMs * 2f);
        _gpu = _gpu <= 0f ? g : _gpu + (g - _gpu) * 0.15f;
        _cpu = _cpu <= 0f ? c : _cpu + (c - _cpu) * 0.15f;
        if (now - _activeSince < WarmUp) return;

        int lowest = LowestLevel();
        if (_level > lowest) { SetLevel(lowest, now, "AdaptiveMinScale changed"); return; }

        float target = budgetMs * Headroom;
        bool gpuBound = _cpu <= 0f || _gpu > _cpu * 1.1f;
        if (_gpu > target && gpuBound && _level < lowest && now - _lastChange > DownCooldown)
        {
            // Two steps when far over budget, so a busy scene recovers within a second or two.
            int steps = _gpu > target * 1.3f ? 2 : 1;
            SetLevel(Math.Min(lowest, _level + steps), now, $"GPU {_gpu:F1} ms > {target:F1} ms target, CPU {_cpu:F1} ms");
            return;
        }

        // Step up only when the next level is expected to still fit, and only after a stretch of headroom.
        if (_level == 0) { _headroomSince = -1f; return; }
        float cur = Levels[_level], next = Levels[_level - 1];
        float predicted = _gpu * (1f - PixelShare + PixelShare * next * next / (cur * cur));
        if (predicted > target * 0.95f) { _headroomSince = -1f; return; }
        if (_headroomSince < 0f) _headroomSince = now;
        if (now - _headroomSince >= UpAfter && now - _lastChange >= SettleAfterChange)
            SetLevel(_level - 1, now, $"GPU {_gpu:F1} ms, ~{predicted:F1} ms expected at {next:0.0#}");
    }

    private void SetLevel(int level, float now, string why)
    {
        if (level == _level) return;
        Log.Limited("ares", 300, $"Adaptive resolution {Levels[_level]:0.0#} -> {Levels[level]:0.0#} ({why})", 'I');
        _level = level;
        _lastChange = now;
        _headroomSince = -1f;
    }

    /// <summary>Index of the smallest level allowed by AdaptiveMinScale.</summary>
    private static int LowestLevel()
    {
        float min = Mathf.Clamp(Settings.AdaptiveMinScale.Value, Levels[^1], 1f);
        int i = 0;
        while (i + 1 < Levels.Length && Levels[i + 1] >= min - 0.001f) i++;
        return i;
    }

    /// <summary>Average and lowest scale since the last call, for the PERF line.</summary>
    public string Summary()
    {
        string mode = Override > 0f ? "fixed" : Settings.AdaptiveResolution.Value ? "adaptive" : "off";
        string s = _scaleFrames == 0 ? $"scale=-({mode})" : $"scale={_scaleSum / _scaleFrames:0.00}(min {_scaleMin:0.0#}, {mode})";
        _scaleSum = 0;
        _scaleFrames = 0;
        _scaleMin = 1f;
        return s;
    }
}
