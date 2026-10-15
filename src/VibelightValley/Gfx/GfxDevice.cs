using System;
using VibelightValley.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VibelightValley.Gfx;

/// <summary>Finds Unity's ID3D11Device through a texture's native pointer, and logs everything about it.</summary>
public static class GfxDevice
{
    public static IntPtr Device { get; private set; }
    public static IntPtr Context { get; private set; }
    public static D3D11.AdapterInfo Adapter;
    public static uint FeatureLevel;

    public static bool Acquire()
    {
        if (Device != IntPtr.Zero) return true;
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
        {
            Log.Error($"Graphics API is {SystemInfo.graphicsDeviceType}; Vibelight Valley requires Direct3D11 (launch without -force-d3d12).");
            return false;
        }
        var rt = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "VibelightValley_DeviceProbe" };
        try
        {
            rt.Create();
            IntPtr tex = rt.GetNativeTexturePtr();
            if (tex == IntPtr.Zero) { Log.Error("Device probe: GetNativeTexturePtr returned null"); return false; }
            var desc = D3D11.GetDesc(tex);
            Log.Info($"Device probe texture: {desc}");
            IntPtr dev = D3D11.GetDevice(tex); // AddRef'd: we keep this reference for the lifetime of the process
            if (dev == IntPtr.Zero) { Log.Error("Device probe: GetDevice returned null"); return false; }
            Device = dev;
            Context = D3D11.GetImmediateContext(dev); // AddRef'd, kept
            FeatureLevel = D3D11.GetFeatureLevel(dev);
            uint flags = D3D11.GetCreationFlags(dev);
            if (D3D11.TryGetAdapter(dev, out Adapter))
                Log.Info($"D3D11 device=0x{dev.ToInt64():X} ctx=0x{Context.ToInt64():X} FL={D3D11.FeatureLevelName(FeatureLevel)} flags=0x{flags:X} adapter {Adapter}");
            else
                Log.Warn($"D3D11 device=0x{dev.ToInt64():X} FL={D3D11.FeatureLevelName(FeatureLevel)} (adapter query failed)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Device probe", ex);
            return false;
        }
        finally
        {
            rt.Release();
            Object.Destroy(rt);
        }
    }

    public static void CheckRemoved(string when)
    {
        if (Device == IntPtr.Zero) return;
        int hr = D3D11.GetDeviceRemovedReason(Device);
        if (hr != 0) Log.Error($"D3D11 DEVICE REMOVED ({when}): hr=0x{hr:X8}");
    }
}
