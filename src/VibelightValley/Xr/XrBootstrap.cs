using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VibelightValley.Core;
using VibelightValley.Gfx;

namespace VibelightValley.Xr;

/// <summary>Creates the OpenXR instance + finds the HMD system, logging everything about the runtime.</summary>
public sealed unsafe class XrBootstrap : IDisposable
{
    public ulong Instance;
    public ulong SystemId;
    public string RuntimeName;
    public string RuntimeVersion;
    public string SystemName;
    public XrViewConfigurationView[] Views = Array.Empty<XrViewConfigurationView>();
    public XrGraphicsRequirementsD3D11KHR GraphicsRequirements;
    public uint MaxLayerCount;

    public const string D3D11Ext = "XR_KHR_D3D11_enable";
    public const string UserPresenceExt = "XR_EXT_user_presence";
    public const string RefreshRateExt = "XR_FB_display_refresh_rate";
    public const string ViewsChangeExt = "XR_EXT_view_configuration_views_change";
    public bool UserPresence, RefreshRateControl;

    public static XrBootstrap Create(string pluginDir, bool validation)
    {
        XrApi.Load(pluginDir);
        var b = new XrBootstrap();
        b.CreateInstance(pluginDir, validation);
        b.QuerySystem();
        return b;
    }

    private void CreateInstance(string pluginDir, bool validation)
    {
        var layersToEnable = new List<string>();
        if (validation)
        {
            string layerDir = Path.Combine(pluginDir, "api_layers");
            Environment.SetEnvironmentVariable("XR_API_LAYER_PATH", layerDir);
            Log.Info($"OpenXR: XR_API_LAYER_PATH={layerDir}");
        }

        try
        {
            var layers = XrApi.EnumerateApiLayers();
            Log.Info($"OpenXR API layers available ({layers.Count}): {string.Join(", ", layers)}");
            if (validation && layers.Contains("XR_APILAYER_LUNARG_core_validation"))
                layersToEnable.Add("XR_APILAYER_LUNARG_core_validation");
        }
        catch (Exception ex) { Log.Warn($"OpenXR: enumerate API layers failed: {ex.Message}"); }

        var exts = XrApi.EnumerateExtensions();
        Log.Block("OpenXR runtime extensions", string.Join("\n", exts));
        if (!exts.Any(e => e.StartsWith(D3D11Ext + " ")))
            throw new InvalidOperationException($"Active OpenXR runtime does not offer {D3D11Ext}. Is SteamVR set as the OpenXR runtime?");

        var ici = new XrInstanceCreateInfo { Type = XrStructureType.InstanceCreateInfo };
        XrStr.Write(ici.ApplicationInfo.ApplicationName, 128, "Vibelight Valley");
        ici.ApplicationInfo.ApplicationVersion = 1;
        XrStr.Write(ici.ApplicationInfo.EngineName, 128, "Unity");
        ici.ApplicationInfo.EngineVersion = 6000;
        ici.ApplicationInfo.ApiVersion = XrConst.MakeVersion(1, 0, 0);

        // Optional: tells us when the headset is put on, so we can recenter then (see XrSession.PollEvents).
        var extsToEnable = new List<string> { D3D11Ext };
        UserPresence = exts.Any(e => e.StartsWith(UserPresenceExt + " "));
        if (UserPresence) extsToEnable.Add(UserPresenceExt);
        // Optional: lets us choose the headset refresh rate (Rendering.RefreshRate).
        RefreshRateControl = exts.Any(e => e.StartsWith(RefreshRateExt + " "));
        if (RefreshRateControl) extsToEnable.Add(RefreshRateExt);
        // Optional: allows the runtime to change the recommended eye resolution during the session (SteamVR's
        // resolution slider); the controller polls for it and resizes. Without it the size is fixed per session.
        if (exts.Any(e => e.StartsWith(ViewsChangeExt + " "))) extsToEnable.Add(ViewsChangeExt);

        var strings = new List<IntPtr>();
        try
        {
            byte** extPtrs = stackalloc byte*[extsToEnable.Count];
            for (int i = 0; i < extsToEnable.Count; i++) strings.Add((IntPtr)(extPtrs[i] = (byte*)Marshal.StringToHGlobalAnsi(extsToEnable[i])));
            byte** layerPtrs = stackalloc byte*[Math.Max(1, layersToEnable.Count)];
            for (int i = 0; i < layersToEnable.Count; i++) strings.Add((IntPtr)(layerPtrs[i] = (byte*)Marshal.StringToHGlobalAnsi(layersToEnable[i])));

            ici.EnabledExtensionCount = (uint)extsToEnable.Count;
            ici.EnabledExtensionNames = extPtrs;
            ici.EnabledApiLayerCount = (uint)layersToEnable.Count;
            ici.EnabledApiLayerNames = layersToEnable.Count > 0 ? layerPtrs : null;
            ulong inst = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            XrApi.Check(XrApi.xrCreateInstance(&ici, &inst), "xrCreateInstance");
            Instance = inst;
            Log.Info($"OpenXR: xrCreateInstance OK in {sw.ElapsedMilliseconds} ms, extensions=[{string.Join(",", extsToEnable)}] layers=[{string.Join(",", layersToEnable)}]");
        }
        finally
        {
            foreach (var p in strings) Marshal.FreeHGlobal(p);
        }

        XrApi.LoadInstanceFunctions(Instance);
        if (RefreshRateControl) XrApi.LoadRefreshRateFunctions(Instance);
        var ip = new XrInstanceProperties { Type = XrStructureType.InstanceProperties };
        XrApi.Check(XrApi.xrGetInstanceProperties(Instance, &ip), "xrGetInstanceProperties");
        RuntimeName = XrStr.Read(ip.RuntimeName, 128);
        RuntimeVersion = XrConst.VersionString(ip.RuntimeVersion);
        Log.Info($"OpenXR runtime: '{RuntimeName}' version {RuntimeVersion}");
    }

    /// <summary>Recommended/max per-eye sizes. Cheap; polled during the session to follow SteamVR resolution changes.</summary>
    public XrViewConfigurationView[] QueryViews()
    {
        uint count = 0;
        XrApi.Check(XrApi.xrEnumerateViewConfigurationViews(Instance, SystemId, XrConst.ViewConfigurationPrimaryStereo, 0, &count, null), "xrEnumerateViewConfigurationViews(count)");
        var views = new XrViewConfigurationView[count];
        for (int i = 0; i < count; i++) views[i].Type = XrStructureType.ViewConfigurationView;
        fixed (XrViewConfigurationView* p = views)
            XrApi.Check(XrApi.xrEnumerateViewConfigurationViews(Instance, SystemId, XrConst.ViewConfigurationPrimaryStereo, count, &count, p), "xrEnumerateViewConfigurationViews");
        return views;
    }

    private void QuerySystem()
    {
        var gi = new XrSystemGetInfo { Type = XrStructureType.SystemGetInfo, FormFactor = XrConst.FormFactorHeadMountedDisplay };
        ulong sys = 0;
        XrResult r = XrApi.xrGetSystem(Instance, &gi, &sys);
        if (r == XrResult.ErrorFormFactorUnavailable)
            throw new XrException("xrGetSystem (headset not connected / SteamVR not ready?)", r);
        XrApi.Check(r, "xrGetSystem");
        SystemId = sys;

        var sp = new XrSystemProperties { Type = XrStructureType.SystemProperties };
        XrApi.Check(XrApi.xrGetSystemProperties(Instance, SystemId, &sp), "xrGetSystemProperties");
        SystemName = XrStr.Read(sp.SystemName, 256);
        MaxLayerCount = sp.MaxLayerCount;
        Log.Info($"OpenXR system: id=0x{SystemId:X} '{SystemName}' vendor=0x{sp.VendorId:X} maxSwapchain={sp.MaxSwapchainImageWidth}x{sp.MaxSwapchainImageHeight} " +
                 $"maxLayers={sp.MaxLayerCount} orientationTracking={sp.OrientationTracking} positionTracking={sp.PositionTracking}");

        Views = QueryViews();
        for (int i = 0; i < Views.Length; i++)
            Log.Info($"OpenXR view {i}: recommended {Views[i].RecommendedImageRectWidth}x{Views[i].RecommendedImageRectHeight} " +
                     $"max {Views[i].MaxImageRectWidth}x{Views[i].MaxImageRectHeight} samples rec={Views[i].RecommendedSwapchainSampleCount} max={Views[i].MaxSwapchainSampleCount}");

        uint bc = 0;
        XrApi.Check(XrApi.xrEnumerateEnvironmentBlendModes(Instance, SystemId, XrConst.ViewConfigurationPrimaryStereo, 0, &bc, null), "xrEnumerateEnvironmentBlendModes(count)");
        int* modes = stackalloc int[(int)Math.Max(bc, 1)];
        XrApi.Check(XrApi.xrEnumerateEnvironmentBlendModes(Instance, SystemId, XrConst.ViewConfigurationPrimaryStereo, bc, &bc, modes), "xrEnumerateEnvironmentBlendModes");
        var ml = new List<int>();
        for (int i = 0; i < bc; i++) ml.Add(modes[i]);
        Log.Info($"OpenXR blend modes: {string.Join(",", ml)} (1=opaque)");

        var req = new XrGraphicsRequirementsD3D11KHR { Type = XrStructureType.GraphicsRequirementsD3D11KHR };
        XrApi.Check(XrApi.xrGetD3D11GraphicsRequirementsKHR(Instance, SystemId, &req), "xrGetD3D11GraphicsRequirementsKHR");
        GraphicsRequirements = req;
        Log.Info($"OpenXR D3D11 requirements: adapter LUID=0x{req.AdapterLuid:X16} minFeatureLevel={D3D11.FeatureLevelName(req.MinFeatureLevel)}");
    }

    public void Dispose()
    {
        if (Instance != 0)
        {
            XrResult r = XrApi.xrDestroyInstance(Instance);
            Log.Info($"OpenXR: xrDestroyInstance -> {r}");
            Instance = 0;
            XrApi.ClearInstance();
        }
    }
}
