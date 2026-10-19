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
    private const int VkControl = 0x11;
    private readonly int _vk;
    private readonly bool _ctrl;
    private bool _wasDown;
    public string Name { get; }

    /// <param name="ctrl">Ctrl must be held. Without it the key only fires while Ctrl is up, so F3 and Ctrl+F3 never both fire.</param>
    public Hotkey(string name, int vk, bool ctrl = false) { Name = name; _vk = vk; _ctrl = ctrl; }

    public bool Pressed()
    {
        bool down = (Win32.GetAsyncKeyState(_vk) & 0x8000) != 0;
        bool pressed = down && !_wasDown;
        _wasDown = down;
        if (!pressed) return false;
        bool ctrlDown = (Win32.GetAsyncKeyState(VkControl) & 0x8000) != 0;
        return ctrlDown == _ctrl && Win32.GameHasFocus();
    }

    public static class VK
    {
        public const int F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75, F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B;
        public const int Home = 0x24, End = 0x23, PageUp = 0x21, PageDown = 0x22, Insert = 0x2D, Delete = 0x2E;
        public const int Up = 0x26, Down = 0x28, Left = 0x25, Right = 0x27;
        public const int NumPad0 = 0x60, Add = 0x6B, Subtract = 0x6D;
    }
}
