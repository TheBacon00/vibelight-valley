using System;
using VibelightValley.Core;
using VibelightValley.Gfx;

namespace VibelightValley.Xr;

/// <summary>One OpenXR swapchain of D3D11 textures, filled by copying a Unity texture into it on the render thread.</summary>
public sealed unsafe class XrSwapchain : IDisposable
{
    public readonly string Name;
    public ulong Handle;
    public readonly int Width, Height;
    private readonly IntPtr[] _images;
    public long Copies, Failures;

    public XrSwapchain(string name, ulong session, long format, int width, int height)
    {
        Name = name;
        Width = width;
        Height = height;
        var ci = new XrSwapchainCreateInfo
        {
            Type = XrStructureType.SwapchainCreateInfo,
            UsageFlags = XrConst.SwapchainUsageColorAttachment | XrConst.SwapchainUsageTransferDst | XrConst.SwapchainUsageSampled,
            Format = format,
            SampleCount = 1,
            Width = (uint)width,
            Height = (uint)height,
            FaceCount = 1,
            ArraySize = 1,
            MipCount = 1,
        };
        ulong h = 0;
        XrApi.Check(XrApi.xrCreateSwapchain(session, &ci, &h), $"xrCreateSwapchain({name})");
        Handle = h;

        uint count = 0;
        XrApi.Check(XrApi.xrEnumerateSwapchainImages(h, 0, &count, null), $"xrEnumerateSwapchainImages({name})");
        var imgs = new XrSwapchainImageD3D11KHR[count];
        for (int i = 0; i < count; i++) imgs[i].Type = XrStructureType.SwapchainImageD3D11KHR;
        fixed (XrSwapchainImageD3D11KHR* p = imgs)
            XrApi.Check(XrApi.xrEnumerateSwapchainImages(h, count, &count, p), $"xrEnumerateSwapchainImages({name})");
        _images = new IntPtr[count];
        for (int i = 0; i < count; i++) _images[i] = imgs[i].Texture;

        string desc = count > 0 ? D3D11.GetDesc(_images[0]).ToString() : "?";
        Log.Info($"Swapchain '{name}': {width}x{height} {DxgiFormat.Name((uint)format)}, {count} images ({desc})");
    }

    /// <summary>
    /// Render thread only: acquire -> wait -> CopyResource(src) -> release. An image that was waited on must always
    /// be released; one whose wait failed must not be.
    /// </summary>
    public bool CopyFrom(IntPtr context, IntPtr srcTexture)
    {
        if (Handle == 0 || srcTexture == IntPtr.Zero || context == IntPtr.Zero) return false;

        uint index = 0;
        var ai = new XrSwapchainImageAcquireInfo { Type = XrStructureType.SwapchainImageAcquireInfo };
        XrResult r = XrApi.xrAcquireSwapchainImage(Handle, &ai, &index);
        if (r < 0) return Fail($"xrAcquireSwapchainImage -> {XrApi.ResultName(r)}");

        var wi = new XrSwapchainImageWaitInfo { Type = XrStructureType.SwapchainImageWaitInfo, Timeout = XrConst.InfiniteDuration };
        r = XrApi.xrWaitSwapchainImage(Handle, &wi);
        if (r != XrResult.Success) return Fail($"xrWaitSwapchainImage -> {XrApi.ResultName(r)}");

        bool ok = index < _images.Length;
        if (ok) { D3D11.CopyResource(context, _images[index], srcTexture); Copies++; }
        else Fail($"acquired index {index} out of range");

        var ri = new XrSwapchainImageReleaseInfo { Type = XrStructureType.SwapchainImageReleaseInfo };
        r = XrApi.xrReleaseSwapchainImage(Handle, &ri);
        if (r < 0) return Fail($"xrReleaseSwapchainImage -> {XrApi.ResultName(r)}");
        return ok;
    }

    private bool Fail(string msg)
    {
        Failures++;
        Log.Limited("swapchain-" + Name, 10, $"Swapchain '{Name}': {msg}", 'E');
        return false;
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        XrApi.xrDestroySwapchain(Handle);
        Handle = 0;
    }
}
