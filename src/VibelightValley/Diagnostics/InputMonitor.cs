using System;
using System.Collections.Generic;
using System.Text;
using VibelightValley.Core;
using Rewired;
using UnityEngine;

namespace VibelightValley.Diagnostics;

/// <summary>
/// Logs what the game's input system (Rewired) sees: its configuration once, the joystick list whenever it
/// changes (the Frame controllers show up as a Steam virtual gamepad), and the first few button presses per
/// joystick - enough to tell "no controller" apart from "controller but no response".
/// </summary>
public static class InputMonitor
{
    private const int PressesToLog = 5;
    private static bool _configLogged;
    private static float _nextListCheck;
    private static string _lastList = "";
    private static readonly Dictionary<int, int> Presses = new();

    public static void Update()
    {
        try
        {
            if (!ReInput.isReady) return;
            if (!_configLogged) LogConfig();
            LogPresses();
            if (Time.realtimeSinceStartup < _nextListCheck) return;
            _nextListCheck = Time.realtimeSinceStartup + 2f;
            LogJoystickList();
        }
        catch (Exception ex) { Log.Limited("inputmon", 5, $"InputMonitor: {ex.GetType().Name}: {ex.Message}", 'W'); }
    }

    private static void LogConfig()
    {
        _configLogged = true;
        var c = ReInput.configuration;
        Log.Info($"Rewired: useXInput={c.useXInput} ignoreInputWhenAppNotInFocus={c.ignoreInputWhenAppNotInFocus} players={ReInput.players.playerCount}");
    }

    private static void LogJoystickList()
    {
        var sb = new StringBuilder();
        var joys = ReInput.controllers.Joysticks;
        int n = ReInput.controllers.joystickCount;
        for (int i = 0; i < n; i++)
        {
            var j = joys[i];
            if (j != null) sb.Append($"[{j.id}] '{j.name}' hw='{j.hardwareName}' enabled={j.enabled}; ");
        }
        string list = sb.ToString();
        if (list == _lastList) return;
        _lastList = list;
        Log.Info($"Rewired joysticks ({n}): {list}");
    }

    private static void LogPresses()
    {
        var joys = ReInput.controllers.Joysticks;
        for (int i = 0, n = ReInput.controllers.joystickCount; i < n; i++)
        {
            var j = joys[i];
            if (j == null || !j.GetAnyButtonDown()) continue;
            Presses.TryGetValue(j.id, out int count);
            if (count >= PressesToLog) continue;
            Presses[j.id] = count + 1;
            Log.Info($"Input: button press on joystick [{j.id}] '{j.name}' (window focused={Application.isFocused})");
        }
    }
}
