using System;
using System.Runtime.InteropServices;

namespace VibelightValley.Core;

internal static class Win32
{
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    public static bool GameHasFocus()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(h, out uint pid);
        return pid == GetCurrentProcessId();
    }
}

/// <summary>
/// Keyboard hotkeys read via GetAsyncKeyState so they work regardless of the game's input setup (Rewired,
/// legacy input possibly disabled). Only active while the game window has focus.
/// </summary>
public sealed class Hotkey
{
    private readonly int _vk;
    private bool _wasDown;
    public string Name { get; }

    public Hotkey(string name, int vk) { Name = name; _vk = vk; }

    public bool Pressed()
    {
        bool down = (Win32.GetAsyncKeyState(_vk) & 0x8000) != 0;
        bool pressed = down && !_wasDown;
        _wasDown = down;
        return pressed && Win32.GameHasFocus();
    }

    public static class VK
    {
        public const int F5 = 0x74, F6 = 0x75, F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B;
        public const int Home = 0x24, End = 0x23, PageUp = 0x21, PageDown = 0x22, Insert = 0x2D, Delete = 0x2E;
        public const int Up = 0x26, Down = 0x28, Left = 0x25, Right = 0x27;
        public const int NumPad0 = 0x60, Add = 0x6B, Subtract = 0x6D;
    }
}
