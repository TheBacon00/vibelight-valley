using System;
using System.Collections.Generic;
using VibelightValley.Core;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VibelightValley.Diagnostics;

/// <summary>
/// Every 10 s, logs the set of active root canvases if it changed: which canvas a menu (e.g. DreamTeams) or HUD
/// element lives on, and its render mode (the HUD layer switches overlay canvases to "cam:VibelightValley_HudCamera").
/// </summary>
public static class CanvasMonitor
{
    private static float _next;
    private static string _last = "";

    public static void Update()
    {
        if (Time.realtimeSinceStartup < _next) return;
        _next = Time.realtimeSinceStartup + 10f; // FindObjectsOfType is not free in busy scenes
        try
        {
            var arr = Object.FindObjectsByType(Il2CppType.Of<Canvas>(), FindObjectsSortMode.None);
            var names = new List<string>();
            foreach (var o in arr)
            {
                var c = o.TryCast<Canvas>();
                if (c == null || !c.isRootCanvas || !c.enabled) continue;
                string mode = c.renderMode switch
                {
                    RenderMode.ScreenSpaceOverlay => "overlay",
                    RenderMode.ScreenSpaceCamera => "cam:" + (c.worldCamera == null ? "null" : c.worldCamera.name),
                    _ => "world",
                };
                names.Add($"{c.name}({mode},{c.sortingOrder})");
            }
            names.Sort(StringComparer.Ordinal);
            string sig = string.Join(" ", names);
            if (sig == _last) return;
            _last = sig;
            Log.Info($"Canvases active ({names.Count}): {sig}");
        }
        catch (Exception ex) { Log.Limited("canvasmon", 3, $"CanvasMonitor: {ex.Message}", 'W'); }
    }
}
