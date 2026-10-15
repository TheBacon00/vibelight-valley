using System;
using System.Collections.Generic;
using VibelightValley.Core;
using VibelightValley.Diagnostics;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace VibelightValley.Vr;

/// <summary>
/// Renders the game's screen-space-overlay UI (icons, HUD, overlay menus) into a transparent texture for the VR
/// HUD layer.
///
/// How: every root canvas in ScreenSpaceOverlay mode is switched to ScreenSpaceCamera on our own orthographic camera.
/// The camera sits at (W/2, H/2, -D) looking down +Z with orthographicSize H/2, so the canvas lands at world
/// (W/2, H/2, 0) with scale 1 - exactly where an overlay canvas lives. Game code that positions UI from screen
/// points (interaction icons over world objects) keeps working unchanged.
///
/// Converted canvases are also moved to an unused layer that only our camera renders: some DDLV canvases are on
/// the Default layer, and rendering Default drew world geometry (the sky dome) behind the UI.
/// Everything is restored as soon as the HUD is not wanted (menus, VR off, F4).
/// </summary>
public sealed class HudLayer
{
    private const float PlaneDistance = 100f;
    private const float RescanSeconds = 1f;

    private sealed class Converted
    {
        public Canvas Canvas;
        public Camera OriginalCamera;
        public float OriginalPlaneDistance;
        public readonly List<(GameObject go, int layer)> OriginalLayers = new();
    }

    private readonly Dictionary<int, Converted> _converted = new();
    private GameObject _go;
    private Camera _cam;
    private int _uiLayer = -1;
    private float _nextScan;

    public RenderTexture Texture { get; private set; }
    public bool Active { get; private set; }

    public HudLayer()
    {
        PipelineMonitor.BeginCamera += OnBeginCamera;
        PipelineMonitor.EndCamera += OnEndCamera;
    }

    // ------------------------------------------------------------------ renderer features
    // DDLV's renderer adds full-screen passes (Weather sky/fog, Painterly, FilmEffect, ...) to EVERY camera on it,
    // including ours - culling masks don't affect them. That painted a sky behind the UI. While the UI camera
    // renders we switch every active feature off and switch them back on right after, so other cameras are
    // unaffected.
    private readonly FeatureSuspension _features = new();
    private bool _featuresLogged;

    private void OnBeginCamera(Camera cam)
    {
        if (_cam == null || cam != _cam) return;
        _features.Suspend(_ => true);
        if (_featuresLogged) return;
        _featuresLogged = true;
        Log.Info($"HUD: suspending {_features.Suspended.Count} renderer features while the UI camera renders: {string.Join(", ", _features.Suspended.ConvertAll(f => f.name))}");
    }

    private void OnEndCamera(Camera cam)
    {
        if (cam == null || cam == _cam) _features.Restore();
    }

    public void Update(bool want)
    {
        if (!want)
        {
            if (Active) Deactivate("not wanted");
            return;
        }
        try
        {
            EnsureCamera();
            if (!Active)
            {
                Active = true;
                _nextScan = 0;
                _cam.enabled = true;
                Log.Info("HUD: activated (overlay canvases -> UI camera)");
            }
            // New canvases appear over time (popups, menus), so rescan periodically.
            if (Time.realtimeSinceStartup >= _nextScan)
            {
                _nextScan = Time.realtimeSinceStartup + RescanSeconds;
                Scan();
            }
        }
        catch (Exception ex) { Log.Limited("hudupdate", 10, $"HUD update failed: {ex}", 'E'); }
    }

    private void EnsureCamera()
    {
        if (_cam == null) CreateCamera();
        int w = Math.Max(16, UnityEngine.Screen.width), h = Math.Max(16, UnityEngine.Screen.height);
        if (Texture != null && Texture.IsCreated() && Texture.width == w && Texture.height == h) return;

        if (Texture != null) { Texture.Release(); Object.Destroy(Texture); }
        Texture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        { name = "VibelightValley_HudUI", hideFlags = HideFlags.HideAndDontSave };
        Texture.Create();
        _cam.targetTexture = Texture;
        _cam.orthographicSize = h * 0.5f;
        _cam.transform.SetPositionAndRotation(new Vector3(w * 0.5f, h * 0.5f, -PlaneDistance), Quaternion.identity);
        Log.Info($"HUD: UI texture {w}x{h}");
    }

    private void CreateCamera()
    {
        _uiLayer = FindUnusedLayer();
        _go = new GameObject("VibelightValley_HudCamera");
        Object.DontDestroyOnLoad(_go);
        _cam = _go.AddComponent<Camera>();
        var ad = _go.AddComponent<UniversalAdditionalCameraData>();
        ad.renderPostProcessing = false;
        ad.antialiasing = AntialiasingMode.None;
        ad.renderShadows = false;
        ad.requiresDepthTexture = false;
        ad.requiresColorTexture = false;
        _cam.orthographic = true;
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = new Color(0, 0, 0, 0);   // transparent: the alpha channel becomes the HUD mask
        _cam.allowHDR = false;                           // HDR intermediates (R11G11B10) have no alpha
        _cam.allowMSAA = false;
        _cam.allowDynamicResolution = false;
        _cam.useOcclusionCulling = false;
        _cam.nearClipPlane = 1f;
        _cam.farClipPlane = PlaneDistance * 2f;
        _cam.depth = 100;
        // Our private layer, plus the standard UI layer as a safety net for UI created later from objects outside the
        // converted hierarchies (nothing in the world sits inside this camera's tiny view box on that layer).
        _cam.cullingMask = (1 << _uiLayer) | (1 << 5);
        _cam.enabled = false;
        Log.Info($"HUD: UI camera created, rendering layers {_uiLayer} and 5 (UI)");
    }

    /// <summary>An unnamed layer the game camera does not render.</summary>
    private static int FindUnusedLayer()
    {
        int gameMask = Camera.main != null ? Camera.main.cullingMask : 0;
        for (int i = 31; i >= 8; i--)
            if (string.IsNullOrEmpty(LayerMask.LayerToName(i)) && (gameMask & (1 << i)) == 0) return i;
        return 30;
    }

    private void Scan()
    {
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Canvas>()))
        {
            var c = o.TryCast<Canvas>();
            if (c == null || !c.isRootCanvas || c.renderMode != RenderMode.ScreenSpaceOverlay) continue;
            int id = c.GetInstanceID();
            if (_converted.ContainsKey(id)) continue;

            var conv = new Converted { Canvas = c, OriginalCamera = c.worldCamera, OriginalPlaneDistance = c.planeDistance };
            // Every UI object (RectTransform), including inactive ones: dropdown lists are copied from a hidden
            // template when opened and become their own canvas with the template's layer, so templates must be on
            // the UI camera's layer too. Plain Transforms (3D models shown by other cameras) are left alone.
            foreach (var rt in c.GetComponentsInChildren<RectTransform>(true))
            {
                var go = rt != null ? rt.gameObject : null;
                if (go == null || go.layer == _uiLayer) continue;
                conv.OriginalLayers.Add((go, go.layer));
                go.layer = _uiLayer;
            }
            c.renderMode = RenderMode.ScreenSpaceCamera;
            c.worldCamera = _cam;
            c.planeDistance = PlaneDistance;
            _converted[id] = conv;
            Log.Verbose($"HUD: canvas '{Report.PathOf(c.transform)}' (order {c.sortingOrder}) -> UI camera");
        }

        // Forget destroyed canvases.
        List<int> dead = null;
        foreach (var kv in _converted)
            if (kv.Value.Canvas == null) (dead ??= new List<int>()).Add(kv.Key);
        if (dead != null) foreach (int id in dead) _converted.Remove(id);
    }

    public void Deactivate(string why)
    {
        int restored = 0;
        foreach (var conv in _converted.Values)
        {
            var c = conv.Canvas;
            if (c == null) continue;
            try
            {
                if (c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera == _cam)
                {
                    c.renderMode = RenderMode.ScreenSpaceOverlay;
                    c.worldCamera = conv.OriginalCamera;
                    c.planeDistance = conv.OriginalPlaneDistance;
                    restored++;
                }
                foreach (var (go, layer) in conv.OriginalLayers)
                    if (go != null) go.layer = layer;
            }
            catch (Exception ex) { Log.Limited("hudrestore", 5, $"HUD: restoring a canvas failed: {ex.Message}", 'W'); }
        }
        _converted.Clear();
        if (_cam != null) _cam.enabled = false;
        if (Active) Log.Info($"HUD: deactivated ({why}); {restored} canvases back to overlay");
        Active = false;
    }

    public void Destroy()
    {
        PipelineMonitor.BeginCamera -= OnBeginCamera;
        PipelineMonitor.EndCamera -= OnEndCamera;
        OnEndCamera(null); // never leave features switched off
        Deactivate("destroy");
        if (_go != null) Object.Destroy(_go);
        if (Texture != null) { Texture.Release(); Object.Destroy(Texture); }
        _go = null; _cam = null; Texture = null;
    }
}
