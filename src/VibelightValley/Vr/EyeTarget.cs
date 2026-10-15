using System;
using System.Collections.Generic;
using VibelightValley.Core;
using VibelightValley.Gfx;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace VibelightValley.Vr;

/// <summary>
/// A Unity RenderTexture whose D3D11 texture can be CopyResource'd into an OpenXR swapchain (same size, same
/// typeless format family). Recreated if Unity releases it (scene loads, device resets).
/// </summary>
public sealed class EyeTarget
{
    public readonly string Name;
    public RenderTexture Texture { get; private set; }
    public IntPtr Native { get; private set; }

    private readonly int _w, _h, _depth;
    private readonly GraphicsFormat _format;
    private readonly long _swapchainFormat;
    // Replaced textures stay alive until Destroy: a queued render-thread copy may still reference their pointer.
    private readonly List<RenderTexture> _retired = new();

    /// <param name="rawBytes">
    /// Use a UNORM (non-sRGB) Unity view even for an sRGB swapchain, so data that is already gamma-encoded
    /// (a capture of the desktop back buffer) is copied byte-for-byte instead of being encoded twice.
    /// </param>
    public EyeTarget(string name, int w, int h, int depth, long swapchainFormat, bool rawBytes = false)
    {
        Name = name;
        _w = w; _h = h; _depth = depth;
        _swapchainFormat = swapchainFormat;
        bool bgra = swapchainFormat is DxgiFormat.B8G8R8A8_UNORM_SRGB or DxgiFormat.B8G8R8A8_UNORM;
        bool srgb = DxgiFormat.IsSrgb((uint)swapchainFormat) && !rawBytes;
        _format = bgra
            ? (srgb ? GraphicsFormat.B8G8R8A8_SRGB : GraphicsFormat.B8G8R8A8_UNorm)
            : (srgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm);
        Create();
    }

    private void Create()
    {
        if (Texture != null) _retired.Add(Texture);
        var desc = new RenderTextureDescriptor(_w, _h, _format, _depth)
        {
            msaaSamples = 1,
            useMipMap = false,
            autoGenerateMips = false,
            sRGB = GraphicsFormatUtility.IsSRGBFormat(_format),
        };
        Texture = new RenderTexture(desc) { name = "VibelightValley_" + Name, hideFlags = HideFlags.HideAndDontSave };
        Texture.Create();
        Native = Texture.GetNativeTexturePtr(); // forces a render-thread sync; only done on (re)creation

        var d = Native == IntPtr.Zero ? default : D3D11.GetDesc(Native);
        bool ok = DxgiFormat.CopyCompatible(d.Format, (uint)_swapchainFormat) && d.Width == _w && d.Height == _h;
        string msg = $"EyeTarget '{Name}': {_format} {_w}x{_h} -> {d}, swapchain {DxgiFormat.Name((uint)_swapchainFormat)}";
        if (ok) Log.Info(msg); else Log.Error(msg + " - NOT copy-compatible, CopyResource will fail");
    }

    /// <summary>Recreates the texture if Unity released it. Returns true if it was recreated.</summary>
    public bool EnsureValid()
    {
        if (Texture != null && Texture.IsCreated()) return false;
        Log.Warn($"EyeTarget '{Name}' was released by Unity - recreating");
        Create();
        return true;
    }

    public void Destroy()
    {
        _retired.Add(Texture);
        foreach (var t in _retired)
        {
            if (t == null) continue;
            t.Release();
            Object.Destroy(t);
        }
        _retired.Clear();
        Texture = null;
        Native = IntPtr.Zero;
    }
}
