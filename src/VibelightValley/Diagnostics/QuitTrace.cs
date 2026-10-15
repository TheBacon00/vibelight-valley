using System;
using VibelightValley.Core;
using HarmonyLib;
using UnityEngine;

namespace VibelightValley.Diagnostics;

/// <summary>
/// Logs who shuts the game down: Application.Quit (called by game code) vs the window being closed.
/// </summary>
public static class QuitTrace
{
    public static void Install(Harmony harmony)
    {
        try
        {
            var prefix = new HarmonyMethod(typeof(QuitTrace).GetMethod(nameof(QuitPrefix)));
            foreach (var m in typeof(Application).GetMethods())
            {
                if (m.Name != "Quit") continue;
                harmony.Patch(m, prefix: prefix);
                Log.Info($"QuitTrace: patched {m}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"QuitTrace: could not patch Application.Quit: {ex.Message}");
        }
    }

    public static void QuitPrefix()
    {
        Log.Warn("Application.Quit() CALLED by game code");
        try { Log.Block("Application.Quit unity stack", StackTraceUtility.ExtractStackTrace()); } catch (Exception ex) { Log.Warn($"ExtractStackTrace failed: {ex.Message}"); }
        try { Log.Block("Application.Quit il2cpp stack", new Il2CppSystem.Diagnostics.StackTrace().ToString()); } catch (Exception ex) { Log.Warn($"il2cpp StackTrace failed: {ex.Message}"); }
    }
}
