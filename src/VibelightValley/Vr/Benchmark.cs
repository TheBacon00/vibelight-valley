using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VibelightValley.Core;
using VibelightValley.Diagnostics;
using VibelightValley.Xr;
using UnityEngine;

namespace VibelightValley.Vr;

/// <summary>
/// Ctrl+F3: measures what each part of the headset image costs. Switches one thing off at a time for a few seconds and
/// compares GPU and CPU frame times with a baseline measured at the start and again at the end - controlled A/B tests
/// instead of guesses. Stand still facing a busy spot while it runs (about 40 s): moving changes what is on screen and so
/// the numbers. Results go to the log: a "BENCH results" table and a one-line summary.
/// </summary>
public sealed class Benchmark
{
    private const float Settle = 1f;     // s after a switch before measuring (Unity reports GPU times a few frames late)
    private const float Measure = 2.5f;  // s of measurement per step

    private sealed class Phase
    {
        public string Name;
        public Action Apply, Revert;
        public double Gpu, Cpu;
        public int Frames;
        public long Waits0 = -1, Waits1;
        public float T0, T1;
        public double GpuAvg => Frames > 0 ? Gpu / Frames : 0;
        public double CpuAvg => Frames > 0 ? Cpu / Frames : 0;
        public double Fps => Waits0 < 0 || T1 <= T0 ? 0 : (Waits1 - Waits0) / (T1 - T0);
    }

    private readonly List<Phase> _phases = new();
    private readonly VrRig _rig;
    private readonly AdaptiveResolution _ares;
    private readonly XrSession _xr;
    private readonly string _context;
    private int _index;
    private float _phaseStart;
    private bool _done;

    public Benchmark(VrRig rig, AdaptiveResolution ares, XrSession xr, bool hudOn, Action<bool> setHud, string context)
    {
        _rig = rig;
        _ares = ares;
        _xr = xr;
        _context = context;
        Add("baseline", null, null);
        Add("internal resolution 0.7", () => ares.Override = 0.7f, () => ares.Override = 1f);
        Add("eye shadows off", () => rig.BenchNoShadows = true, () => { rig.BenchNoShadows = false; rig.ForceSync(); });
        Add("LOD boost off", () => rig.BenchNoLodBoost = true, () => rig.BenchNoLodBoost = false);
        foreach (var (index, name) in rig.ActiveFeatures())
            Add($"effect '{name}' off", () => rig.SetDiagnosticFeature(index), () => rig.SetDiagnosticFeature(-1));
        if (hudOn) Add("HUD layer off", () => setHud(false), () => setHud(true));
        Add("baseline again", null, null);

        ares.Override = 1f;                 // measure at the full eye resolution
        rig.SetDiagnosticFeature(-1);
        Log.Info($"BENCH started ({context}): {_phases.Count} steps of {Settle + Measure:F1} s. Stand still facing a busy spot; " +
                 "render effects switch off one at a time. Ctrl+F3 cancels.");
        Begin(0);
    }

    private void Add(string name, Action apply, Action revert) => _phases.Add(new Phase { Name = name, Apply = apply, Revert = revert });

    private void Begin(int i)
    {
        _index = i;
        _phaseStart = Time.realtimeSinceStartup;
        _phases[i].Apply?.Invoke();
        Log.Info($"BENCH {i + 1}/{_phases.Count}: {_phases[i].Name}");
    }

    /// <summary>Once per frame. Returns false when finished or cancelled.</summary>
    public bool Update(bool eyesActive)
    {
        if (_done) return false;
        if (!eyesActive) { Abort("the world stopped rendering (menu or loading screen)"); return false; }
        var p = _phases[_index];
        float now = Time.realtimeSinceStartup, t = now - _phaseStart;
        if (t < Settle) return true;
        if (p.Waits0 < 0) { p.Waits0 = _xr.Waits; p.T0 = now; }
        if (FrameProfiler.LastGpuMs > 0f)
        {
            p.Gpu += FrameProfiler.LastGpuMs;
            p.Cpu += FrameProfiler.LastCpuMs;
            p.Frames++;
        }
        if (t < Settle + Measure) return true;

        p.Waits1 = _xr.Waits;
        p.T1 = now;
        p.Revert?.Invoke();
        if (_index + 1 < _phases.Count) { Begin(_index + 1); return true; }
        Finish();
        return false;
    }

    private void Finish()
    {
        _done = true;
        ResetOverrides();
        Phase first = _phases[0], last = _phases[^1];
        double baseGpu = (first.GpuAvg + last.GpuAvg) / 2, baseCpu = (first.CpuAvg + last.CpuAvg) / 2;

        var sb = new StringBuilder();
        sb.AppendLine($"{_context}; 'vs base' compares with the average of the first and last step");
        sb.AppendLine($"{"step",-40} {"GPU ms",7} {"vs base",8} {"CPU ms",7} {"vs base",8} {"fps",6}");
        foreach (var p in _phases)
        {
            bool isBase = p == first || p == last;
            string dg = isBase ? "" : (p.GpuAvg - baseGpu).ToString("+0.00;-0.00");
            string dc = isBase ? "" : (p.CpuAvg - baseCpu).ToString("+0.00;-0.00");
            sb.AppendLine($"{p.Name,-40} {p.GpuAvg,7:F2} {dg,8} {p.CpuAvg,7:F2} {dc,8} {p.Fps,6:F1}");
        }
        double drift = baseGpu > 0 ? Math.Abs(first.GpuAvg - last.GpuAvg) / baseGpu : 0;
        if (drift > 0.1) sb.AppendLine($"Note: the two baselines differ by {drift:P0} - the view changed during the run; repeat it standing still.");
        Log.Block("BENCH results", sb.ToString().TrimEnd());

        var savings = _phases.Where(p => p != first && p != last && p.Frames > 0)
            .Select(p => (p.Name, ms: baseGpu - p.GpuAvg))
            .OrderByDescending(x => x.ms)
            .Take(5)
            .Select(x => $"{x.Name} {x.ms:F2} ms");
        Log.Info($"BENCH done. Biggest GPU savings: {string.Join(", ", savings)} (table: BENCH results block)");
    }

    public void Abort(string why)
    {
        if (_done) return;
        _done = true;
        _phases[_index].Revert?.Invoke();
        ResetOverrides();
        Log.Warn($"BENCH cancelled at step {_index + 1}/{_phases.Count}: {why}");
    }

    private void ResetOverrides()
    {
        _ares.Override = 0f;
        _rig.SetDiagnosticFeature(-1);
        _rig.BenchNoShadows = _rig.BenchNoLodBoost = false;
        _rig.ForceSync();
    }
}
