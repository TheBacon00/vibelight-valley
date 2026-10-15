using System;
using System.Collections.Generic;
using VibelightValley.Core;
using VibelightValley.Gfx;

namespace VibelightValley.Xr;

/// <summary>
/// Everything the render thread needs to finish one OpenXR frame. Filled on the main thread, read on the render
/// thread. Lives in a small ring (<see cref="XrSession.Slots"/>) so a slot is never rewritten while in flight.
/// </summary>
public struct FrameRecord
{
    // From xrWaitFrame / xrLocateViews
    public long DisplayTime;
    public bool ShouldRender;
    public bool PosesValid;            // both views located with unit quaternions (required for a projection layer)
    public XrView View0, View1;
    public int UnityFrame;

    // Layers to submit
    public bool SubmitProjection;
    public IntPtr EyeTex0, EyeTex1;    // native D3D11 textures, copy-compatible with the eye swapchains
    public QuadLayer Vignette;         // head-locked darkening of the view edges while turning (VIEW space)
    public QuadLayer Hud;              // see-through game UI (alpha blended)
    public QuadLayer Screen;           // opaque virtual screen

    public volatile bool Begun;        // set by the render thread after a successful xrBeginFrame
}

public struct QuadLayer
{
    public bool Submit;
    public IntPtr Texture;
    public XrPosef Pose;               // in LOCAL space
    public XrExtent2Df Size;           // metres
}

/// <summary>
/// OpenXR session on Unity's D3D11 device.
///
/// Threading model (the part that is easy to get wrong):
///  - main thread  : PollEvents, xrWaitFrame + xrLocateViews, xrBegin/EndSession
///  - render thread: xrBeginFrame, swapchain copies, xrEndFrame (queued via <see cref="RenderThread"/>)
/// Every successful xrWaitFrame MUST be followed by exactly one BeginFrame and one EndFrame, otherwise the
/// runtime blocks the next xrWaitFrame forever. Begin/End share a lock with session begin/end.
/// </summary>
public sealed unsafe class XrSession : IDisposable
{
    public const int Slots = 4;

    public XrBootstrap Boot;
    public ulong Session, LocalSpace, ViewSpace;
    public XrSessionState State = XrSessionState.Unknown;
    public bool Running;               // between xrBeginSession and xrEndSession
    public bool ExitRequested;
    public bool RecenterRequested;     // runtime-side recenter (reference space change)

    public readonly XrSwapchain[] Eyes = new XrSwapchain[2];
    public XrSwapchain HudChain, ScreenChain, VignetteChain;
    public const int VignetteSize = 256;
    public long ColorFormat;

    private readonly FrameRecord[] _frames = new FrameRecord[Slots];
    private int _slot;
    private readonly object _frameLock = new();
    private ulong _viewStateFlags;

    // Stats for the periodic log line (approximate; written by both threads).
    public long Waits, Begins, Ends, EndFailures, Discarded, ShouldRenderFalse, ProjectionSubmits, HudSubmits, ScreenSubmits, VignetteSubmits;
    public double WaitMsTotal, WaitMsMax;
    public long LastPredictedPeriod;

    public static XrSession Create(XrBootstrap boot, IntPtr device, (int w, int h) eye, (int w, int h) hud, (int w, int h) screen, bool preferSrgb)
    {
        var s = new XrSession { Boot = boot };
        s.CreateSession(device);
        s.CreateSpaces();
        s.ColorFormat = s.ChooseFormat(preferSrgb);
        s.Eyes[0] = new XrSwapchain("eyeL", s.Session, s.ColorFormat, eye.w, eye.h);
        s.Eyes[1] = new XrSwapchain("eyeR", s.Session, s.ColorFormat, eye.w, eye.h);
        s.HudChain = new XrSwapchain("hud", s.Session, s.ColorFormat, hud.w, hud.h);
        s.ScreenChain = new XrSwapchain("screen", s.Session, s.ColorFormat, screen.w, screen.h);
        s.VignetteChain = new XrSwapchain("vignette", s.Session, s.ColorFormat, VignetteSize, VignetteSize);
        return s;
    }

    /// <summary>
    /// Replaces both eye swapchains with new ones of the given size. Main thread; the caller must have fenced the
    /// render thread and must not have an OpenXR frame open (no frame may reference the old swapchains).
    /// </summary>
    public void ResizeEyes(int width, int height)
    {
        lock (_frameLock)
        {
            for (int i = 0; i < 2; i++)
            {
                Eyes[i].Dispose();
                Eyes[i] = new XrSwapchain(i == 0 ? "eyeL" : "eyeR", Session, ColorFormat, width, height);
            }
        }
    }

    private void CreateSession(IntPtr device)
    {
        var binding = new XrGraphicsBindingD3D11KHR { Type = XrStructureType.GraphicsBindingD3D11KHR, Device = device };
        var ci = new XrSessionCreateInfo { Type = XrStructureType.SessionCreateInfo, Next = &binding, SystemId = Boot.SystemId };
        ulong s = 0;
        XrApi.Check(XrApi.xrCreateSession(Boot.Instance, &ci, &s), "xrCreateSession");
        Session = s;
        Log.Info($"OpenXR session created: 0x{s:X} on device 0x{device.ToInt64():X}");
    }

    private void CreateSpaces()
    {
        var ci = new XrReferenceSpaceCreateInfo { Type = XrStructureType.ReferenceSpaceCreateInfo, ReferenceSpaceType = XrConst.ReferenceSpaceLocal, PoseInReferenceSpace = XrPosef.Identity };
        ulong local = 0, view = 0;
        XrApi.Check(XrApi.xrCreateReferenceSpace(Session, &ci, &local), "xrCreateReferenceSpace(LOCAL)");
        ci.ReferenceSpaceType = XrConst.ReferenceSpaceView;
        XrApi.Check(XrApi.xrCreateReferenceSpace(Session, &ci, &view), "xrCreateReferenceSpace(VIEW)");
        LocalSpace = local;
        ViewSpace = view;
    }

    /// <summary>
    /// 8-bit sRGB is correct for Unity's linear colour space: Unity writes gamma-encoded bytes into sRGB render
    /// textures, and CopyResource moves them byte-for-byte into an sRGB swapchain that the compositor decodes.
    /// </summary>
    private long ChooseFormat(bool preferSrgb)
    {
        uint count = 0;
        XrApi.Check(XrApi.xrEnumerateSwapchainFormats(Session, 0, &count, null), "xrEnumerateSwapchainFormats(count)");
        long* fmts = stackalloc long[(int)count];
        XrApi.Check(XrApi.xrEnumerateSwapchainFormats(Session, count, &count, fmts), "xrEnumerateSwapchainFormats");
        var supported = new List<long>();
        for (int i = 0; i < count; i++) supported.Add(fmts[i]);
        Log.Info($"OpenXR swapchain formats: {string.Join(", ", supported.ConvertAll(f => DxgiFormat.Name((uint)f)))}");

        long[] preference = preferSrgb
            ? new long[] { DxgiFormat.R8G8B8A8_UNORM_SRGB, DxgiFormat.B8G8R8A8_UNORM_SRGB, DxgiFormat.R8G8B8A8_UNORM, DxgiFormat.B8G8R8A8_UNORM }
            : new long[] { DxgiFormat.R8G8B8A8_UNORM, DxgiFormat.B8G8R8A8_UNORM, DxgiFormat.R8G8B8A8_UNORM_SRGB, DxgiFormat.B8G8R8A8_UNORM_SRGB };
        foreach (long f in preference)
        {
            if (!supported.Contains(f)) continue;
            Log.Info($"Chosen swapchain format: {DxgiFormat.Name((uint)f)}");
            return f;
        }
        throw new InvalidOperationException("OpenXR runtime offers no 8-bit RGBA/BGRA swapchain format");
    }

    // ------------------------------------------------------------------ session lifecycle (main thread)

    /// <summary>Drains OpenXR events. Returns false once the session/instance is gone and VR must be torn down.</summary>
    public bool PollEvents()
    {
        var buf = new XrEventDataBuffer();
        for (int guard = 0; guard < 64; guard++)
        {
            buf.Type = XrStructureType.EventDataBuffer;
            buf.Next = null;
            XrResult r = XrApi.xrPollEvent(Boot.Instance, &buf);
            if (r == XrResult.EventUnavailable) break;
            XrApi.Check(r, "xrPollEvent");
            switch (buf.Type)
            {
                case XrStructureType.EventDataSessionStateChanged:
                    var e = (XrEventDataSessionStateChanged*)&buf;
                    Log.Info($"OpenXR session state: {State} -> {e->State}");
                    State = e->State;
                    OnStateChanged();
                    break;
                case XrStructureType.EventDataInstanceLossPending:
                    Log.Error("OpenXR: instance loss pending (SteamVR shutting down?)");
                    ExitRequested = true;
                    return false;
                case XrStructureType.EventDataReferenceSpaceChangePending:
                    Log.Info("OpenXR: reference space change pending (recentered in SteamVR?)");
                    RecenterRequested = true;
                    break;
                case XrStructureType.EventDataUserPresenceChangedEXT:
                    // Headset put on / taken off. Recentering on "put on" means a session that started while the
                    // headset lay on the desk (facing anywhere) is corrected as soon as the user looks through it.
                    bool present = ((XrEventDataUserPresenceChangedEXT*)&buf)->IsUserPresent != 0;
                    Log.Info($"OpenXR: user {(present ? "put the headset ON - recentering" : "took the headset off")}");
                    if (present) RecenterRequested = true;
                    break;
                case XrStructureType.EventDataDisplayRefreshRateChangedFB:
                    var rc = (XrEventDataDisplayRefreshRateChangedFB*)&buf;
                    Log.Info($"OpenXR: headset refresh rate {rc->FromDisplayRefreshRate:F1} -> {rc->ToDisplayRefreshRate:F1} Hz");
                    break;
                case XrStructureType.EventDataEventsLost:
                    Log.Warn("OpenXR: events lost");
                    break;
            }
        }
        return !(State is XrSessionState.Exiting or XrSessionState.LossPending || ExitRequested);
    }

    private void OnStateChanged()
    {
        switch (State)
        {
            case XrSessionState.Ready:
                var bi = new XrSessionBeginInfo { Type = XrStructureType.SessionBeginInfo, PrimaryViewConfigurationType = XrConst.ViewConfigurationPrimaryStereo };
                lock (_frameLock)
                {
                    XrApi.Check(XrApi.xrBeginSession(Session, &bi), "xrBeginSession");
                    Running = true;
                }
                Log.Info("OpenXR: session begun (frame loop starts)");
                RequestRefreshRate(Settings.RefreshRate.Value);
                break;
            case XrSessionState.Stopping:
                StopRunning("STOPPING");
                break;
            case XrSessionState.Exiting:
            case XrSessionState.LossPending:
                StopRunning(State.ToString());
                ExitRequested = true;
                break;
        }
    }

    /// <summary>
    /// Asks the runtime to drive the headset at the supported rate nearest to <paramref name="hz"/> (0 = leave it).
    /// xrWaitFrame then paces the game at that rate. Needs XR_FB_display_refresh_rate.
    /// </summary>
    private void RequestRefreshRate(float hz)
    {
        if (hz <= 0) return;
        if (XrApi.xrRequestDisplayRefreshRateFB == null)
        {
            Log.Warn($"RefreshRate={hz}: the runtime does not support choosing the refresh rate; set it in SteamVR instead");
            return;
        }
        try
        {
            uint count = 0;
            XrApi.Check(XrApi.xrEnumerateDisplayRefreshRatesFB(Session, 0, &count, null), "xrEnumerateDisplayRefreshRatesFB(count)");
            float* rates = stackalloc float[(int)Math.Max(count, 1)];
            XrApi.Check(XrApi.xrEnumerateDisplayRefreshRatesFB(Session, count, &count, rates), "xrEnumerateDisplayRefreshRatesFB");
            float current = 0;
            XrApi.Check(XrApi.xrGetDisplayRefreshRateFB(Session, &current), "xrGetDisplayRefreshRateFB");

            var available = new List<float>();
            for (int i = 0; i < count; i++) available.Add(rates[i]);
            if (available.Count == 0) { Log.Warn("Headset reports no selectable refresh rates"); return; }
            float best = available[0];
            foreach (float r in available) if (Math.Abs(r - hz) < Math.Abs(best - hz)) best = r;

            Log.Info($"Headset refresh rates: {string.Join(", ", available.ConvertAll(r => r.ToString("F1")))} Hz; current {current:F1}, requested {hz:F1} -> {best:F1}");
            if (Math.Abs(best - current) < 0.5f) return;
            XrApi.Check(XrApi.xrRequestDisplayRefreshRateFB(Session, best), "xrRequestDisplayRefreshRateFB");
        }
        catch (Exception ex) { Log.Warn($"Setting the refresh rate failed: {ex.Message}"); }
    }

    private void StopRunning(string why)
    {
        if (!Running) return;
        RenderThread.WaitForFence(1000); // let queued Begin/End events run before the session goes away
        lock (_frameLock)
        {
            XrResult r = XrApi.xrEndSession(Session);
            Running = false;
            Log.Info($"OpenXR: xrEndSession ({why}) -> {XrApi.ResultName(r)}");
        }
    }

    // ------------------------------------------------------------------ frame (main thread)

    /// <summary>
    /// xrWaitFrame (blocks until the headset wants the next frame - this paces the whole game) + xrLocateViews.
    /// Returns the ring slot for this frame, or -1. On success the caller must queue BeginFrame and EndFrame.
    /// </summary>
    public int WaitFrame(out FrameRecord rec)
    {
        rec = default;
        if (!Running) return -1;
        var wi = new XrFrameWaitInfo { Type = XrStructureType.FrameWaitInfo };
        var fs = new XrFrameState { Type = XrStructureType.FrameState };
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        XrResult r = XrApi.xrWaitFrame(Session, &wi, &fs);
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (r < 0)
        {
            Log.Limited("waitframe", 20, $"xrWaitFrame -> {XrApi.ResultName(r)}", 'E');
            return -1;
        }
        Waits++;
        WaitMsTotal += ms;
        if (ms > WaitMsMax) WaitMsMax = ms;
        LastPredictedPeriod = fs.PredictedDisplayPeriod;

        _slot = (_slot + 1) % Slots;
        rec.DisplayTime = fs.PredictedDisplayTime;
        rec.ShouldRender = fs.ShouldRender != 0;
        if (!rec.ShouldRender) { ShouldRenderFalse++; return _slot; }

        var li = new XrViewLocateInfo { Type = XrStructureType.ViewLocateInfo, ViewConfigurationType = XrConst.ViewConfigurationPrimaryStereo, DisplayTime = fs.PredictedDisplayTime, Space = LocalSpace };
        var vs = new XrViewState { Type = XrStructureType.ViewState };
        XrView* views = stackalloc XrView[2];
        views[0] = new XrView { Type = XrStructureType.View };
        views[1] = new XrView { Type = XrStructureType.View };
        uint n = 0;
        XrResult lr = XrApi.xrLocateViews(Session, &li, &vs, 2, &n, views);
        if (lr < 0 || n != 2)
        {
            Log.Limited("locateviews", 20, $"xrLocateViews -> {XrApi.ResultName(lr)} count={n}", 'E');
            return _slot;
        }
        rec.View0 = views[0];
        rec.View1 = views[1];
        _viewStateFlags = vs.ViewStateFlags;
        rec.PosesValid = (_viewStateFlags & XrConst.ViewStateOrientationValid) != 0
                         && IsUnit(rec.View0.Pose.Orientation) && IsUnit(rec.View1.Pose.Orientation);
        return _slot;
    }

    private static bool IsUnit(XrQuaternionf q)
    {
        float l = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
        return l > 0.98f && l < 1.02f;
    }

    /// <summary>Head pose (VIEW space in LOCAL space) at the given time.</summary>
    public bool LocateHead(long time, out XrPosef pose)
    {
        pose = XrPosef.Identity;
        var loc = new XrSpaceLocation { Type = XrStructureType.SpaceLocation };
        XrResult r = XrApi.xrLocateSpace(ViewSpace, LocalSpace, time, &loc);
        if (r < 0 || (loc.LocationFlags & XrConst.SpaceLocationOrientationValid) == 0) return false;
        pose = loc.Pose;
        return true;
    }

    /// <summary>Main thread: store a freshly waited record, then queue xrBeginFrame for it.</summary>
    public void StoreAndBegin(int slot, in FrameRecord rec)
    {
        _frames[slot] = rec;
        _frames[slot].Begun = false;
        RenderThread.Issue(RenderThread.Op.BeginFrame, slot);
    }

    /// <summary>Main thread: edit layer fields of a slot before <see cref="IssueEnd"/>. Never replace the whole record after begin.</summary>
    public ref FrameRecord Frame(int slot) => ref _frames[slot];

    public void IssueEnd(int slot) => RenderThread.Issue(RenderThread.Op.EndFrame, slot);

    // ------------------------------------------------------------------ frame (render thread)

    public void RT_BeginFrame(int slot)
    {
        lock (_frameLock)
        {
            if (!Running) return;
            var bi = new XrFrameBeginInfo { Type = XrStructureType.FrameBeginInfo };
            XrResult r = XrApi.xrBeginFrame(Session, &bi);
            if (r == XrResult.FrameDiscarded) { Discarded++; Log.Limited("discard", 10, "xrBeginFrame -> FRAME_DISCARDED (previous frame was not ended)", 'W'); }
            else if (r < 0) { Log.Limited("beginframe", 20, $"xrBeginFrame -> {XrApi.ResultName(r)}", 'E'); return; }
            Begins++;
            _frames[slot].Begun = true;
        }
    }

    /// <summary>Copies the slot's textures into the swapchains and calls xrEndFrame with up to 3 layers.</summary>
    public void RT_EndFrame(int slot, IntPtr context)
    {
        lock (_frameLock)
        {
            if (!Running) return;
            ref FrameRecord f = ref _frames[slot];
            if (!f.Begun)
            {
                Log.Limited("endnobegin", 10, $"EndFrame for slot {slot} without a successful BeginFrame - skipped", 'W');
                return;
            }

            void** layers = stackalloc void*[4];
            uint layerCount = 0;

            // Layers are composited in array order: world, then the virtual screen, then the UI on top of both.
            XrCompositionLayerProjectionView* views = stackalloc XrCompositionLayerProjectionView[2];
            XrCompositionLayerProjection proj = default;
            if (f.ShouldRender && f.SubmitProjection && f.PosesValid
                && Eyes[0].CopyFrom(context, f.EyeTex0) && Eyes[1].CopyFrom(context, f.EyeTex1))
            {
                views[0] = ProjectionView(f.View0, Eyes[0]);
                views[1] = ProjectionView(f.View1, Eyes[1]);
                proj = new XrCompositionLayerProjection { Type = XrStructureType.CompositionLayerProjection, Space = LocalSpace, ViewCount = 2, Views = views };
                layers[layerCount++] = &proj;
                ProjectionSubmits++;
            }

            // Vignette sits directly on the world (re-placed in front of the head every frame), under the screen and UI.
            XrCompositionLayerQuad vignette = default;
            if (f.ShouldRender && TryQuad(f.Vignette, VignetteChain, context, XrConst.LayerBlendTextureSourceAlpha, out vignette))
            {
                layers[layerCount++] = &vignette;
                VignetteSubmits++;
            }

            XrCompositionLayerQuad screen = default;
            if (f.ShouldRender && TryQuad(f.Screen, ScreenChain, context, 0, out screen))
            {
                layers[layerCount++] = &screen;
                ScreenSubmits++;
            }

            XrCompositionLayerQuad hud = default;
            if (f.ShouldRender && TryQuad(f.Hud, HudChain, context, XrConst.LayerBlendTextureSourceAlpha, out hud))
            {
                layers[layerCount++] = &hud;
                HudSubmits++;
            }

            var ei = new XrFrameEndInfo
            {
                Type = XrStructureType.FrameEndInfo,
                DisplayTime = f.DisplayTime,
                EnvironmentBlendMode = XrConst.EnvironmentBlendOpaque,
                LayerCount = layerCount,
                Layers = layerCount > 0 ? layers : null,
            };
            XrResult r = XrApi.xrEndFrame(Session, &ei);
            if (r < 0) { EndFailures++; Log.Limited("endframe" + r, 20, $"xrEndFrame -> {XrApi.ResultName(r)} (layers={layerCount})", 'E'); }
            else Ends++;
            f.Begun = false;
        }
    }

    private bool TryQuad(in QuadLayer q, XrSwapchain chain, IntPtr context, ulong flags, out XrCompositionLayerQuad layer, ulong space = 0)
    {
        layer = default;
        if (!q.Submit || !chain.CopyFrom(context, q.Texture)) return false;
        layer = new XrCompositionLayerQuad
        {
            Type = XrStructureType.CompositionLayerQuad,
            LayerFlags = flags,
            Space = space != 0 ? space : LocalSpace,
            EyeVisibility = XrConst.EyeVisibilityBoth,
            SubImage = FullImage(chain),
            Pose = q.Pose,
            Size = q.Size,
        };
        return true;
    }

    private static XrCompositionLayerProjectionView ProjectionView(in XrView v, XrSwapchain chain) => new()
    {
        Type = XrStructureType.CompositionLayerProjectionView,
        Pose = v.Pose,
        Fov = v.Fov,
        SubImage = FullImage(chain),
    };

    private static XrSwapchainSubImage FullImage(XrSwapchain chain) => new()
    {
        Swapchain = chain.Handle,
        ImageRect = new XrRect2Di { Extent = new XrExtent2Di { Width = chain.Width, Height = chain.Height } },
    };

    public void Dispose()
    {
        try
        {
            StopRunning("dispose");
            RenderThread.WaitForFence(1000);
            foreach (var e in Eyes) e?.Dispose();
            HudChain?.Dispose();
            ScreenChain?.Dispose();
            VignetteChain?.Dispose();
            if (LocalSpace != 0) XrApi.xrDestroySpace(LocalSpace);
            if (ViewSpace != 0) XrApi.xrDestroySpace(ViewSpace);
            if (Session != 0) Log.Info($"OpenXR: xrDestroySession -> {XrApi.xrDestroySession(Session)}");
        }
        catch (Exception ex) { Log.Error("XrSession.Dispose", ex); }
        Session = LocalSpace = ViewSpace = 0;
        Boot?.Dispose();
        Boot = null;
    }
}
