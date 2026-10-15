using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using VibelightValley.Core;

namespace VibelightValley.Xr;

public sealed class XrException : Exception
{
    public XrResult Result { get; }
    public XrException(string call, XrResult r) : base($"{call} failed: {XrApi.ResultName(r)} ({(int)r})") { Result = r; }
}

/// <summary>
/// OpenXR entry points as C# function pointers resolved through xrGetInstanceProcAddr of the
/// Khronos loader shipped next to the plugin. Every call result goes through <see cref="Check"/>.
/// </summary>
public static unsafe class XrApi
{
    public static bool Loaded { get; private set; }
    public static string LoaderPath { get; private set; }
    private static IntPtr _lib;
    private static ulong _instance;

    public static delegate* unmanaged[Stdcall]<ulong, byte*, IntPtr*, XrResult> xrGetInstanceProcAddr;
    public static delegate* unmanaged[Stdcall]<uint, uint*, XrApiLayerProperties*, XrResult> xrEnumerateApiLayerProperties;
    public static delegate* unmanaged[Stdcall]<byte*, uint, uint*, XrExtensionProperties*, XrResult> xrEnumerateInstanceExtensionProperties;
    public static delegate* unmanaged[Stdcall]<XrInstanceCreateInfo*, ulong*, XrResult> xrCreateInstance;

    public static delegate* unmanaged[Stdcall]<ulong, XrResult> xrDestroyInstance;
    public static delegate* unmanaged[Stdcall]<ulong, XrInstanceProperties*, XrResult> xrGetInstanceProperties;
    public static delegate* unmanaged[Stdcall]<ulong, XrResult, byte*, XrResult> xrResultToString;
    public static delegate* unmanaged[Stdcall]<ulong, XrSystemGetInfo*, ulong*, XrResult> xrGetSystem;
    public static delegate* unmanaged[Stdcall]<ulong, ulong, XrSystemProperties*, XrResult> xrGetSystemProperties;
    public static delegate* unmanaged[Stdcall]<ulong, ulong, int, uint, uint*, XrViewConfigurationView*, XrResult> xrEnumerateViewConfigurationViews;
    public static delegate* unmanaged[Stdcall]<ulong, ulong, int, uint, uint*, int*, XrResult> xrEnumerateEnvironmentBlendModes;
    public static delegate* unmanaged[Stdcall]<ulong, ulong, XrGraphicsRequirementsD3D11KHR*, XrResult> xrGetD3D11GraphicsRequirementsKHR;
    public static delegate* unmanaged[Stdcall]<ulong, XrSessionCreateInfo*, ulong*, XrResult> xrCreateSession;
    public static delegate* unmanaged[Stdcall]<ulong, XrResult> xrDestroySession;
    public static delegate* unmanaged[Stdcall]<ulong, XrSessionBeginInfo*, XrResult> xrBeginSession;
    public static delegate* unmanaged[Stdcall]<ulong, XrResult> xrEndSession;
    public static delegate* unmanaged[Stdcall]<ulong, XrResult> xrRequestExitSession;
    public static delegate* unmanaged[Stdcall]<ulong, XrEventDataBuffer*, XrResult> xrPollEvent;
    public static delegate* unmanaged[Stdcall]<ulong, XrReferenceSpaceCreateInfo*, ulong*, XrResult> xrCreateReferenceSpace;
    public static delegate* unmanaged[Stdcall]<ulong, XrResult> xrDestroySpace;
    public static delegate* unmanaged[Stdcall]<ulong, ulong, long, XrSpaceLocation*, XrResult> xrLocateSpace;
    public static delegate* unmanaged[Stdcall]<ulong, uint, uint*, long*, XrResult> xrEnumerateSwapchainFormats;
    public static delegate* unmanaged[Stdcall]<ulong, XrSwapchainCreateInfo*, ulong*, XrResult> xrCreateSwapchain;
    public static delegate* unmanaged[Stdcall]<ulong, XrResult> xrDestroySwapchain;
    public static delegate* unmanaged[Stdcall]<ulong, uint, uint*, XrSwapchainImageD3D11KHR*, XrResult> xrEnumerateSwapchainImages;
    public static delegate* unmanaged[Stdcall]<ulong, XrSwapchainImageAcquireInfo*, uint*, XrResult> xrAcquireSwapchainImage;
    public static delegate* unmanaged[Stdcall]<ulong, XrSwapchainImageWaitInfo*, XrResult> xrWaitSwapchainImage;
    public static delegate* unmanaged[Stdcall]<ulong, XrSwapchainImageReleaseInfo*, XrResult> xrReleaseSwapchainImage;
    public static delegate* unmanaged[Stdcall]<ulong, XrFrameWaitInfo*, XrFrameState*, XrResult> xrWaitFrame;
    public static delegate* unmanaged[Stdcall]<ulong, XrFrameBeginInfo*, XrResult> xrBeginFrame;
    public static delegate* unmanaged[Stdcall]<ulong, XrFrameEndInfo*, XrResult> xrEndFrame;
    public static delegate* unmanaged[Stdcall]<ulong, XrViewLocateInfo*, XrViewState*, uint, uint*, XrView*, XrResult> xrLocateViews;

    // XR_FB_display_refresh_rate (optional; null when the extension is not enabled)
    public static delegate* unmanaged[Stdcall]<ulong, uint, uint*, float*, XrResult> xrEnumerateDisplayRefreshRatesFB;
    public static delegate* unmanaged[Stdcall]<ulong, float*, XrResult> xrGetDisplayRefreshRateFB;
    public static delegate* unmanaged[Stdcall]<ulong, float, XrResult> xrRequestDisplayRefreshRateFB;

    /// <summary>Loads openxr_loader.dll from the plugin folder and resolves the global functions.</summary>
    public static void Load(string pluginDir)
    {
        if (Loaded) return;
        LoaderPath = Path.Combine(pluginDir, "openxr_loader.dll");
        if (!File.Exists(LoaderPath)) throw new FileNotFoundException("openxr_loader.dll missing next to the plugin", LoaderPath);
        if (!NativeLibrary.TryLoad(LoaderPath, out _lib)) throw new DllNotFoundException($"NativeLibrary.TryLoad failed for {LoaderPath}");
        if (!NativeLibrary.TryGetExport(_lib, "xrGetInstanceProcAddr", out IntPtr gipa))
            throw new EntryPointNotFoundException("xrGetInstanceProcAddr not exported by openxr_loader.dll");
        xrGetInstanceProcAddr = (delegate* unmanaged[Stdcall]<ulong, byte*, IntPtr*, XrResult>)gipa;
        xrEnumerateApiLayerProperties = (delegate* unmanaged[Stdcall]<uint, uint*, XrApiLayerProperties*, XrResult>)Get(0, "xrEnumerateApiLayerProperties");
        xrEnumerateInstanceExtensionProperties = (delegate* unmanaged[Stdcall]<byte*, uint, uint*, XrExtensionProperties*, XrResult>)Get(0, "xrEnumerateInstanceExtensionProperties");
        xrCreateInstance = (delegate* unmanaged[Stdcall]<XrInstanceCreateInfo*, ulong*, XrResult>)Get(0, "xrCreateInstance");
        Loaded = true;
        Log.Info($"OpenXR loader loaded: {LoaderPath} (version {System.Diagnostics.FileVersionInfo.GetVersionInfo(LoaderPath).FileVersion})");
        // The library is intentionally never freed: runtimes keep threads/hooks alive inside the process.
    }

    private static IntPtr Get(ulong instance, string name)
    {
        IntPtr fn = IntPtr.Zero;
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes)
        {
            XrResult r = xrGetInstanceProcAddr(instance, p, &fn);
            if (r != XrResult.Success || fn == IntPtr.Zero)
                throw new XrException($"xrGetInstanceProcAddr({name})", r);
        }
        return fn;
    }

    public static void LoadInstanceFunctions(ulong instance)
    {
        _instance = instance;
        xrDestroyInstance = (delegate* unmanaged[Stdcall]<ulong, XrResult>)Get(instance, "xrDestroyInstance");
        xrGetInstanceProperties = (delegate* unmanaged[Stdcall]<ulong, XrInstanceProperties*, XrResult>)Get(instance, "xrGetInstanceProperties");
        xrResultToString = (delegate* unmanaged[Stdcall]<ulong, XrResult, byte*, XrResult>)Get(instance, "xrResultToString");
        xrGetSystem = (delegate* unmanaged[Stdcall]<ulong, XrSystemGetInfo*, ulong*, XrResult>)Get(instance, "xrGetSystem");
        xrGetSystemProperties = (delegate* unmanaged[Stdcall]<ulong, ulong, XrSystemProperties*, XrResult>)Get(instance, "xrGetSystemProperties");
        xrEnumerateViewConfigurationViews = (delegate* unmanaged[Stdcall]<ulong, ulong, int, uint, uint*, XrViewConfigurationView*, XrResult>)Get(instance, "xrEnumerateViewConfigurationViews");
        xrEnumerateEnvironmentBlendModes = (delegate* unmanaged[Stdcall]<ulong, ulong, int, uint, uint*, int*, XrResult>)Get(instance, "xrEnumerateEnvironmentBlendModes");
        xrGetD3D11GraphicsRequirementsKHR = (delegate* unmanaged[Stdcall]<ulong, ulong, XrGraphicsRequirementsD3D11KHR*, XrResult>)Get(instance, "xrGetD3D11GraphicsRequirementsKHR");
        xrCreateSession = (delegate* unmanaged[Stdcall]<ulong, XrSessionCreateInfo*, ulong*, XrResult>)Get(instance, "xrCreateSession");
        xrDestroySession = (delegate* unmanaged[Stdcall]<ulong, XrResult>)Get(instance, "xrDestroySession");
        xrBeginSession = (delegate* unmanaged[Stdcall]<ulong, XrSessionBeginInfo*, XrResult>)Get(instance, "xrBeginSession");
        xrEndSession = (delegate* unmanaged[Stdcall]<ulong, XrResult>)Get(instance, "xrEndSession");
        xrRequestExitSession = (delegate* unmanaged[Stdcall]<ulong, XrResult>)Get(instance, "xrRequestExitSession");
        xrPollEvent = (delegate* unmanaged[Stdcall]<ulong, XrEventDataBuffer*, XrResult>)Get(instance, "xrPollEvent");
        xrCreateReferenceSpace = (delegate* unmanaged[Stdcall]<ulong, XrReferenceSpaceCreateInfo*, ulong*, XrResult>)Get(instance, "xrCreateReferenceSpace");
        xrDestroySpace = (delegate* unmanaged[Stdcall]<ulong, XrResult>)Get(instance, "xrDestroySpace");
        xrLocateSpace = (delegate* unmanaged[Stdcall]<ulong, ulong, long, XrSpaceLocation*, XrResult>)Get(instance, "xrLocateSpace");
        xrEnumerateSwapchainFormats = (delegate* unmanaged[Stdcall]<ulong, uint, uint*, long*, XrResult>)Get(instance, "xrEnumerateSwapchainFormats");
        xrCreateSwapchain = (delegate* unmanaged[Stdcall]<ulong, XrSwapchainCreateInfo*, ulong*, XrResult>)Get(instance, "xrCreateSwapchain");
        xrDestroySwapchain = (delegate* unmanaged[Stdcall]<ulong, XrResult>)Get(instance, "xrDestroySwapchain");
        xrEnumerateSwapchainImages = (delegate* unmanaged[Stdcall]<ulong, uint, uint*, XrSwapchainImageD3D11KHR*, XrResult>)Get(instance, "xrEnumerateSwapchainImages");
        xrAcquireSwapchainImage = (delegate* unmanaged[Stdcall]<ulong, XrSwapchainImageAcquireInfo*, uint*, XrResult>)Get(instance, "xrAcquireSwapchainImage");
        xrWaitSwapchainImage = (delegate* unmanaged[Stdcall]<ulong, XrSwapchainImageWaitInfo*, XrResult>)Get(instance, "xrWaitSwapchainImage");
        xrReleaseSwapchainImage = (delegate* unmanaged[Stdcall]<ulong, XrSwapchainImageReleaseInfo*, XrResult>)Get(instance, "xrReleaseSwapchainImage");
        xrWaitFrame = (delegate* unmanaged[Stdcall]<ulong, XrFrameWaitInfo*, XrFrameState*, XrResult>)Get(instance, "xrWaitFrame");
        xrBeginFrame = (delegate* unmanaged[Stdcall]<ulong, XrFrameBeginInfo*, XrResult>)Get(instance, "xrBeginFrame");
        xrEndFrame = (delegate* unmanaged[Stdcall]<ulong, XrFrameEndInfo*, XrResult>)Get(instance, "xrEndFrame");
        xrLocateViews = (delegate* unmanaged[Stdcall]<ulong, XrViewLocateInfo*, XrViewState*, uint, uint*, XrView*, XrResult>)Get(instance, "xrLocateViews");
        Log.Verbose("OpenXR instance functions resolved");
    }

    public static void LoadRefreshRateFunctions(ulong instance)
    {
        xrEnumerateDisplayRefreshRatesFB = (delegate* unmanaged[Stdcall]<ulong, uint, uint*, float*, XrResult>)Get(instance, "xrEnumerateDisplayRefreshRatesFB");
        xrGetDisplayRefreshRateFB = (delegate* unmanaged[Stdcall]<ulong, float*, XrResult>)Get(instance, "xrGetDisplayRefreshRateFB");
        xrRequestDisplayRefreshRateFB = (delegate* unmanaged[Stdcall]<ulong, float, XrResult>)Get(instance, "xrRequestDisplayRefreshRateFB");
    }

    public static void ClearInstance()
    {
        _instance = 0;
        xrEnumerateDisplayRefreshRatesFB = null;
        xrGetDisplayRefreshRateFB = null;
        xrRequestDisplayRefreshRateFB = null;
    }

    public static string ResultName(XrResult r)
    {
        if (_instance != 0 && xrResultToString != null)
        {
            byte* buf = stackalloc byte[64];
            if (xrResultToString(_instance, r, buf) == XrResult.Success) return XrStr.Read(buf, 64);
        }
        return Enum.IsDefined(typeof(XrResult), r) ? r.ToString() : $"XrResult({(int)r})";
    }

    /// <summary>Throws on any error result; logs non-success qualified results verbosely.</summary>
    public static XrResult Check(XrResult r, string call)
    {
        if (r < 0) throw new XrException(call, r);
        if (r != XrResult.Success) Log.Limited("xr-" + call + r, 10, $"{call} -> {ResultName(r)}", 'V');
        return r;
    }

    public static List<string> EnumerateExtensions()
    {
        var list = new List<string>();
        uint count = 0;
        Check(xrEnumerateInstanceExtensionProperties(null, 0, &count, null), "xrEnumerateInstanceExtensionProperties(count)");
        var props = new XrExtensionProperties[count];
        for (int i = 0; i < count; i++) props[i].Type = XrStructureType.ExtensionProperties;
        fixed (XrExtensionProperties* p = props)
        {
            Check(xrEnumerateInstanceExtensionProperties(null, count, &count, p), "xrEnumerateInstanceExtensionProperties");
            for (int i = 0; i < count; i++) list.Add($"{XrStr.Read(p[i].ExtensionName, 128)} v{p[i].ExtensionVersion}");
        }
        return list;
    }

    public static List<string> EnumerateApiLayers()
    {
        var list = new List<string>();
        uint count = 0;
        Check(xrEnumerateApiLayerProperties(0, &count, null), "xrEnumerateApiLayerProperties(count)");
        if (count == 0) return list;
        var props = new XrApiLayerProperties[count];
        for (int i = 0; i < count; i++) props[i].Type = XrStructureType.ApiLayerProperties;
        fixed (XrApiLayerProperties* p = props)
        {
            Check(xrEnumerateApiLayerProperties(count, &count, p), "xrEnumerateApiLayerProperties");
            for (int i = 0; i < count; i++) list.Add(XrStr.Read(p[i].LayerName, 256));
        }
        return list;
    }
}
