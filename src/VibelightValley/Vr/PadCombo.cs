using System;
using System.Collections.Generic;
using VibelightValley.Core;
using Rewired;

namespace VibelightValley.Vr;

/// <summary>
/// Detects a two-button gamepad chord (default: click both sticks) on any joystick Rewired knows about, such as the
/// Steam virtual gamepad the Frame controllers appear as. Buttons are matched by Rewired element name; every
/// button name is logged once per joystick so the config can be adjusted.
/// </summary>
public sealed class PadCombo
{
    private readonly Func<string> _config;
    private string _configRaw;
    private string[] _names = Array.Empty<string>();
    private readonly Dictionary<int, (int a, int b)> _indices = new(); // joystick id -> button indices
    private bool _wasDown;

    public PadCombo(Func<string> config) { _config = config; }

    /// <summary>True on the frame the chord becomes fully pressed.</summary>
    public bool Pressed()
    {
        try
        {
            if (!ReInput.isReady) return false;
            string raw = _config() ?? "";
            if (raw != _configRaw)
            {
                _configRaw = raw;
                _names = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                _indices.Clear();
            }
            if (_names.Length != 2) return false;

            bool down = false;
            var joys = ReInput.controllers.Joysticks;
            for (int i = 0, n = ReInput.controllers.joystickCount; i < n; i++)
            {
                var j = joys[i];
                if (j == null) continue;
                if (!_indices.TryGetValue(j.id, out var idx)) _indices[j.id] = idx = Resolve(j);
                if (idx.a >= 0 && idx.b >= 0 && j.GetButton(idx.a) && j.GetButton(idx.b)) down = true;
            }
            bool pressed = down && !_wasDown;
            _wasDown = down;
            return pressed;
        }
        catch (Exception ex)
        {
            Log.Limited("padcombo", 5, $"PadCombo: {ex.GetType().Name}: {ex.Message}", 'W');
            return false;
        }
    }

    private (int, int) Resolve(Joystick j)
    {
        var ids = j.ButtonElementIdentifiers;
        int count = ids.Cast<Il2CppSystem.Collections.Generic.ICollection<ControllerElementIdentifier>>().Count;
        var buttons = new string[count];
        for (int b = 0; b < count; b++) buttons[b] = ids[b].name ?? "";

        int a = Find(buttons, _names[0]), c = Find(buttons, _names[1]);
        Log.Info($"PadCombo: joystick [{j.id}] '{j.name}' buttons: {string.Join(", ", buttons)}");
        Log.Info($"PadCombo: '{_names[0]}' -> {a}, '{_names[1]}' -> {c}" +
                 (a < 0 || c < 0 ? " (NOT FOUND - set Input.HudToggleButtons to names from the list above)" : ""));
        return (a, c);
    }

    private static int Find(string[] buttons, string name)
    {
        int exact = Array.FindIndex(buttons, b => b.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return exact;
        // Looser: "Left Stick Button" also matches "Left Stick Click" / "Left Stick Press".
        string loose = name.Replace(" Button", "", StringComparison.OrdinalIgnoreCase);
        return Array.FindIndex(buttons, b => b.Contains(loose, StringComparison.OrdinalIgnoreCase));
    }
}
