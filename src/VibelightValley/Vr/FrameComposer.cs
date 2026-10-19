using System;
using System.IO;
using VibelightValley.Core;
using VibelightValley.Diagnostics;
using VibelightValley.Xr;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VibelightValley.Vr;

/// <summary>
/// Decides what goes into each OpenXR frame and prepares the textures for it (main thread, end of frame):
///  - projection layer : the eye renders as they are (FastEyeSubmit: SteamVR flips them via the layer's field of
///                       view), or the eye renders / test pattern blitted the right way up into submit textures
///  - HUD quad         : the game's overlay UI (HudLayer) filling the game camera's view, see-through
///  - screen quad      : a capture of the desktop window, for title screen / menus / F8
/// The render thread later copies these textures into the swapchains.
/// </summary>
public sealed class FrameComposer
{
    // Blit targets for the eyes: only needed for the test pattern or with FastEyeSubmit off, so created on first use
    // (two full-resolution textures otherwise sit unused in video memory).
    private readonly EyeTarget[] _eyeSubmit = new EyeTarget[2];
    private int _eyeW, _eyeH;
    private readonly EyeTarget _hudSubmit;
    private readonly EyeTarget _screenSubmit;
    private RenderTexture _screenCapture;
    private Material _uiBlend;
    private bool _uiBlendTried;

    private readonly long _format;

    public FrameComposer((int w, int h) eye, (int w, int h) hud, (int w, int h) screen, long format)
    {
        _format = format;
        (_eyeW, _eyeH) = eye;
        _hudSubmit = new EyeTarget("SubmitHud", hud.w, hud.h, 0, format);
        // The desktop capture is already gamma-encoded: copy its bytes as-is.
        _screenSubmit = new EyeTarget("SubmitScreen", screen.w, screen.h, 0, format, rawBytes: true);
    }

    public void Compose(ref FrameRecord f, VrRig rig, HudLayer hud, bool testPattern, bool screenToggle)
    {
        f.Hud = default;
        f.Screen = default;
        f.Vignette = default;
        f.EyeTex0 = f.EyeTex1 = IntPtr.Zero;
        f.FlipProjectionY = false;
        if (!f.ShouldRender) return;

        // Unity render textures are bottom-up; OpenXR swapchains are top-down. Either SteamVR flips the eye renders
        // (the layer declares a flipped field of view - no extra work), or a blit flips them into submit textures.
        bool flip = Settings.FlipEyesY.Value;
        if (testPattern && f.PosesValid)
        {
            for (int i = 0; i < 2; i++) Blit(TestPattern.Get(i), EyeSubmit(i), flip);
            f.EyeTex0 = EyeSubmit(0).Native;
            f.EyeTex1 = EyeSubmit(1).Native;
            f.SubmitProjection = true;
        }
        else if (f.SubmitProjection)
        {
            if (Settings.FastEyeSubmit.Value)
            {
                f.EyeTex0 = rig.Targets[0].Native;
                f.EyeTex1 = rig.Targets[1].Native;
                f.FlipProjectionY = flip;
            }
            else
            {
                for (int i = 0; i < 2; i++) Blit(rig.Targets[i].Texture, EyeSubmit(i), flip);
                f.EyeTex0 = EyeSubmit(0).Native;
                f.EyeTex1 = EyeSubmit(1).Native;
            }
            UpdateVignette(ref f, rig.TurnSpeed);
        }
        if (!rig.HasOrigin) return;

        bool hudReady = hud.Active && hud.Texture != null;
        bool world = f.SubmitProjection && !testPattern;

        // Lighter desktop view: the game camera drew nothing this frame, so give the desktop window the left eye.
        if (world && rig.DesktopStripped && Settings.Desktop.Value == DesktopView.Mirror) MirrorToDesktop(rig.Targets[0].Texture);

        // In the world: the UI floats see-through in the game camera's view (head-locked in front of you with F8).
        if (world && hudReady)
        {
            SubmitHud(ref f, hud, blendOntoDesktop: true);
            if (screenToggle || !rig.HudQuad(Settings.HudDistance.Value, out f.Hud.Pose, out f.Hud.Size))
                rig.FrontQuad(Settings.ScreenDistance.Value, Settings.ScreenWidth.Value, Aspect(hud.Texture), out f.Hud.Pose, out f.Hud.Size);
            return;
        }

        // Title screen, loading, full-screen menus (or Always/F8): the virtual screen shows the desktop capture, and the
        // overlay UI (which the capture lacks) is laid over it at exactly the same place and size.
        bool showScreen = Settings.Screen.Value == ScreenMode.Always
                          || (Settings.Screen.Value == ScreenMode.Auto && (screenToggle || !f.SubmitProjection || testPattern));
        if (!showScreen || !CaptureScreen()) return;
        f.Screen.Texture = _screenSubmit.Native;
        f.Screen.Submit = true;
        rig.FrontQuad(Settings.ScreenDistance.Value, Settings.ScreenWidth.Value, Aspect(_screenCapture), out f.Screen.Pose, out f.Screen.Size);
        if (hudReady)
        {
            // Optionally also blended onto the desktop for spectators. The screen capture runs after this frame's blits,
            // so the virtual screen then contains the UI too and the HUD layer repeats it at the same place - the
            // headset keeps its own UI layer either way, so menus can't go missing there.
            SubmitHud(ref f, hud, blendOntoDesktop: Settings.DesktopMenus.Value);
            f.Hud.Pose = f.Screen.Pose;
            f.Hud.Size = f.Screen.Size;
        }
    }

    private void SubmitHud(ref FrameRecord f, HudLayer hud, bool blendOntoDesktop)
    {
        Blit(hud.Texture, _hudSubmit, Settings.FlipEyesY.Value);
        f.Hud.Texture = _hudSubmit.Native;
        f.Hud.Submit = true;
        // The overlay UI no longer reaches the desktop back buffer by itself: blend it on top there too.
        if (blendOntoDesktop) BlendUiOntoDesktop(hud.Texture);
    }

    private static float Aspect(Texture t) => t.height / (float)Math.Max(1, t.width);

    /// <summary>Left eye, centre-cropped to the window's aspect ratio, onto the desktop back buffer.</summary>
    private static void MirrorToDesktop(RenderTexture eye)
    {
        float windowAspect = UnityEngine.Screen.width / (float)Math.Max(1, UnityEngine.Screen.height);
        float eyeAspect = eye.width / (float)Math.Max(1, eye.height);
        // Wide window: full eye width, matching horizontal band. Tall window: full height, matching vertical band.
        float sx = 1f, sy = 1f;
        if (windowAspect >= eyeAspect) sy = eyeAspect / windowAspect; else sx = windowAspect / eyeAspect;
        Graphics.Blit(eye, (RenderTexture)null, new Vector2(sx, sy), new Vector2((1f - sx) * 0.5f, (1f - sy) * 0.5f));
    }

    private EyeTarget EyeSubmit(int i) => _eyeSubmit[i] ??= new EyeTarget(i == 0 ? "SubmitL" : "SubmitR", _eyeW, _eyeH, 0, _format);

    /// <summary>New eye resolution. The caller must have fenced the render thread (old textures are destroyed).</summary>
    public void ResizeEyes(int width, int height)
    {
        (_eyeW, _eyeH) = (width, height);
        for (int i = 0; i < 2; i++)
        {
            _eyeSubmit[i]?.Destroy();
            _eyeSubmit[i] = null;
        }
    }

    // ------------------------------------------------------------------ turn vignette
    // While the view turns, the edges of the view darken: less peripheral motion = less motion sickness.
    // It is a separate head-locked compositor layer (SteamVR draws it over the world), not something drawn into the
    // eye images with a Unity shader - that approach stayed invisible. Strength ramps from 0 at 15 deg/s to full at
    // 60 deg/s, fades in quickly and out more slowly. A few pre-baked strength levels are swapped in per frame.
    private const int VignetteLevels = 8;
    // Quad 3.4 m wide per metre of distance. The ring starts darkening ~23 deg off-centre and is at full strength by ~48 deg, i.e.
    // inside the headset's view (the first version only reached full strength at ~60 deg, outside the lenses).
    private const float VignetteQuadSize = 3.4f;
    private const float VignetteInner = 0.25f, VignetteOuter = 0.65f;   // radii as a fraction of half the quad size
    private EyeTarget[] _vignetteLevels;
    private float _vignette;
    private bool _vignetteLogged;
    /// <summary>Highest vignette strength since the last stats line (reset by the controller).</summary>
    public float PeakVignette;

    private void UpdateVignette(ref FrameRecord f, float turnDegPerSec)
    {
        float target = Settings.Vignette.Value * Mathf.Clamp01((turnDegPerSec - 15f) / 45f);
        float tau = target > _vignette ? 0.08f : 0.3f;
        _vignette = Mathf.Lerp(_vignette, target, 1f - Mathf.Exp(-Time.unscaledDeltaTime / tau));
        PeakVignette = Mathf.Max(PeakVignette, _vignette);

        int level = Mathf.RoundToInt(_vignette * VignetteLevels);
        if (level <= 0) return;
        EnsureVignetteLevels();
        var tex = _vignetteLevels[Math.Min(level, VignetteLevels) - 1];
        tex.EnsureValid();
        f.Vignette.Texture = tex.Native;
        // Placed in front of the predicted head pose in LOCAL space (the space the HUD/screen layers use), and FARTHER
        // than the HUD: SteamVR draws nearer quads over farther ones regardless of submission order, so a vignette at
        // 1 m covered the HUD. Size scales with distance, so the ring covers the same angles.
        float d = Settings.HudDistance.Value / Mathf.Max(0.01f, Settings.WorldScale.Value) + 2f;
        Vector3 head = (XrMath.ToUnity(f.View0.Pose.Position) + XrMath.ToUnity(f.View1.Pose.Position)) * 0.5f;
        Quaternion rot = XrMath.ToUnity(f.View0.Pose.Orientation);
        f.Vignette.Pose = new XrPosef { Position = XrMath.ToXr(head + rot * (Vector3.forward * d)), Orientation = XrMath.ToXr(rot) };
        f.Vignette.Size = new XrExtent2Df { Width = VignetteQuadSize * d, Height = VignetteQuadSize * d };
        f.Vignette.Submit = true;
        if (!_vignetteLogged) { _vignetteLogged = true; Log.Info($"Vignette layer active (strength {_vignette:F2} at {turnDegPerSec:F0} deg/s)"); }
    }

    /// <summary>Black ring textures (transparent centre) at increasing opacity, uploaded once.</summary>
    private void EnsureVignetteLevels()
    {
        if (_vignetteLevels != null) return;
        const int n = XrSession.VignetteSize;
        _vignetteLevels = new EyeTarget[VignetteLevels];
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[n * n];
        for (int lvl = 1; lvl <= VignetteLevels; lvl++)
        {
            float strength = lvl / (float)VignetteLevels;
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);                 // 0 centre, 1 edge midpoint
                float a = strength * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(VignetteInner, VignetteOuter, r));
                px[y * n + x] = new Color32(0, 0, 0, (byte)(a * 255));   // black: premultiplied alpha is the same
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            _vignetteLevels[lvl - 1] = new EyeTarget("Vignette" + lvl, n, n, 0, _format);
            Graphics.Blit(tex, _vignetteLevels[lvl - 1].Texture);
        }
        Object.Destroy(tex);
    }

    private static void Blit(Texture src, EyeTarget dst, bool flip)
    {
        dst.EnsureValid();
        if (flip) Graphics.Blit(src, dst.Texture, new Vector2(1f, -1f), new Vector2(0f, 1f));
        else Graphics.Blit(src, dst.Texture);
    }

    /// <summary>Copies the desktop back buffer (game + overlay UI). Used where the HUD path does not apply.</summary>
    private bool CaptureScreen()
    {
        try
        {
            int sw = UnityEngine.Screen.width, sh = UnityEngine.Screen.height;
            if (sw <= 0 || sh <= 0) return false;
            if (_screenCapture == null || _screenCapture.width != sw || _screenCapture.height != sh || !_screenCapture.IsCreated())
            {
                if (_screenCapture != null) { _screenCapture.Release(); Object.Destroy(_screenCapture); }
                _screenCapture = new RenderTexture(sw, sh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { name = "VibelightValley_ScreenCapture", hideFlags = HideFlags.HideAndDontSave };
                _screenCapture.Create();
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_screenCapture);
            Blit(_screenCapture, _screenSubmit, Settings.FlipScreenY.Value);
            return true;
        }
        catch (Exception ex)
        {
            Log.Limited("capture", 5, $"Screen capture failed: {ex.Message}", 'E');
            return false;
        }
    }

    private void BlendUiOntoDesktop(RenderTexture ui)
    {
        try
        {
            if (!_uiBlendTried)
            {
                _uiBlendTried = true;
                foreach (var name in new[] { "UI/Default", "Sprites/Default", "Unlit/Transparent" })
                {
                    var sh = Shader.Find(name);
                    if (sh == null) continue;
                    _uiBlend = new Material(sh) { name = "VibelightValley_UiBlend", hideFlags = HideFlags.HideAndDontSave };
                    Log.Info($"Desktop UI blended with shader '{name}'");
                    break;
                }
                if (_uiBlend == null) Log.Warn("No blend shader found - the desktop window shows no UI while the HUD layer is on");
            }
            if (_uiBlend != null) Graphics.Blit(ui, (RenderTexture)null, _uiBlend);
        }
        catch (Exception ex) { Log.Limited("desktopui", 5, $"Desktop UI blend failed: {ex.Message}", 'W'); }
    }

    public void Dump(string dir, VrRig rig, HudLayer hud)
    {
        if (rig != null)
        {
            PngDump.Save(rig.Targets[0].Texture, Path.Combine(dir, "eyeL_render.png"));
            PngDump.Save(rig.Targets[1].Texture, Path.Combine(dir, "eyeR_render.png"));
        }
        PngDump.Save(_eyeSubmit[0]?.Texture, Path.Combine(dir, "eyeL_submit.png"));
        PngDump.Save(_eyeSubmit[1]?.Texture, Path.Combine(dir, "eyeR_submit.png"));
        PngDump.Save(hud?.Texture, Path.Combine(dir, "hud_ui.png"));
        PngDump.Save(_hudSubmit.Texture, Path.Combine(dir, "hud_submit.png"));
        PngDump.Save(_screenCapture, Path.Combine(dir, "screen_capture.png"));
        PngDump.Save(_screenSubmit.Texture, Path.Combine(dir, "screen_submit.png"));
        if (_vignetteLevels != null) PngDump.Save(_vignetteLevels[VignetteLevels - 1].Texture, Path.Combine(dir, "vignette_full.png"));
    }

    public void Destroy()
    {
        foreach (var t in _eyeSubmit) t?.Destroy();
        _hudSubmit.Destroy();
        _screenSubmit.Destroy();
        if (_screenCapture != null) { _screenCapture.Release(); Object.Destroy(_screenCapture); }
        if (_uiBlend != null) Object.Destroy(_uiBlend);
        if (_vignetteLevels != null) foreach (var t in _vignetteLevels) t.Destroy();
    }
}
