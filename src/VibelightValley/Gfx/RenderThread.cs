extern alias sysinterop;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using VibelightValley.Core;

namespace VibelightValley.Gfx;

/// <summary>
/// Runs managed code on Unity's render thread via GL.IssuePluginEvent. Unity renders on its own thread, so
/// D3D11 immediate-context work (CopyResource) and xrBegin/EndFrame must happen there, in order with Unity's
/// own commands. Code executed here must NOT touch IL2CPP objects (UnityEngine.*), only .NET state and pointers.
/// Event id layout: 0x5652_OOSS  (tag 'VR', op byte, slot byte).
/// </summary>
public static unsafe class RenderThread
{
    public const int Tag = 0x56520000;

    public enum Op : byte
    {
        Ping = 1,       // startup self-test
        Fence = 2,      // WaitForFence
        BeginFrame = 3,
        EndFrame = 4,
    }

    /// <summary>Handler invoked on the render thread for BeginFrame/EndFrame: (op, slot).</summary>
    public static Action<Op, int> Handler;

    private static bool _firstEventLogged;
    public static long PingsReceived;
    private static long _fenceReached;
    private static long _fenceIssued;
    private static int _exceptionCount;

    private static IntPtr _issueIcall;
    private static bool _useIcall;

    public static IntPtr CallbackPointer => (IntPtr)(delegate* unmanaged[Stdcall]<int, void>)&OnRenderEvent;

    [sysinterop::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnRenderEvent(int eventId)
    {
        try
        {
            if ((eventId & unchecked((int)0xFFFF0000)) != Tag) return;
            if (!_firstEventLogged)
            {
                _firstEventLogged = true;
                Log.Info($"RenderThread: first plugin event on managed thread {Environment.CurrentManagedThreadId} (native tid {Win32.GetCurrentThreadId()})");
            }
            var op = (Op)((eventId >> 8) & 0xFF);
            int slot = eventId & 0xFF;
            switch (op)
            {
                case Op.Ping:
                    Interlocked.Increment(ref PingsReceived);
                    break;
                case Op.Fence:
                    Interlocked.Exchange(ref _fenceReached, slot);
                    break;
                default:
                    Handler?.Invoke(op, slot);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into native Unity code.
            if (Interlocked.Increment(ref _exceptionCount) <= 20)
                Log.Error($"RenderThread event 0x{eventId:X8}", ex);
        }
    }

    public static int Encode(Op op, int slot) => Tag | ((int)op << 8) | (slot & 0xFF);

    /// <summary>Queue an event on the render thread (call from main thread).</summary>
    public static void Issue(Op op, int slot = 0)
    {
        int id = Encode(op, slot);
        if (_useIcall)
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, int, void>)_issueIcall)(CallbackPointer, id);
            return;
        }
        try
        {
            UnityEngine.GL.IssuePluginEvent(CallbackPointer, id);
        }
        catch (Exception ex)
        {
            Log.Warn($"GL.IssuePluginEvent via interop failed ({ex.GetType().Name}: {ex.Message}); trying icall fallback");
            if (!TryEnableIcall()) throw;
            ((delegate* unmanaged[Cdecl]<IntPtr, int, void>)_issueIcall)(CallbackPointer, id);
        }
    }

    private static bool TryEnableIcall()
    {
        foreach (string sig in new[]
                 {
                     "UnityEngine.GL::GLIssuePluginEvent(System.IntPtr,System.Int32)",
                     "UnityEngine.GL::GLIssuePluginEvent",
                 })
        {
            IntPtr p = Il2CppInterop.Runtime.IL2CPP.il2cpp_resolve_icall(sig);
            if (p != IntPtr.Zero)
            {
                _issueIcall = p;
                _useIcall = true;
                Log.Info($"RenderThread: using icall '{sig}' @0x{p.ToInt64():X}");
                return true;
            }
        }
        Log.Error("RenderThread: no IssuePluginEvent icall found");
        return false;
    }

    /// <summary>Blocks the main thread until the render thread has processed everything queued so far.</summary>
    public static bool WaitForFence(int timeoutMs = 1000)
    {
        int ticket = (int)(Interlocked.Increment(ref _fenceIssued) & 0xFF);
        if (ticket == 0) ticket = (int)(Interlocked.Increment(ref _fenceIssued) & 0xFF);
        Issue(Op.Fence, ticket);
        try { UnityEngine.GL.Flush(); } catch { /* optional */ }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Interlocked.Read(ref _fenceReached) != ticket)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                Log.Warn($"RenderThread fence {ticket} timed out after {timeoutMs} ms");
                return false;
            }
            Thread.Sleep(1);
        }
        Log.Verbose($"RenderThread fence {ticket} reached in {sw.ElapsedMilliseconds} ms");
        return true;
    }
}
