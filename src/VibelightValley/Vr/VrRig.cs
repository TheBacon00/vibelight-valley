using System;
using System.Collections.Generic;
using VibelightValley.Core;
using VibelightValley.Diagnostics;
using VibelightValley.Xr;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace VibelightValley.Vr;

/// <summary>
/// Two extra URP cameras that render the world from the headset's eye poses. They are anchored to the game's own
/// camera, so the player "hovers" where the flat game puts the view, with head tracking added on top.
///
/// Mapping (Unity space):  eyeWorld = anchorPos + anchorRot * (headLocal * worldScale)
///   anchor    = game camera position (+ configurable offset) and its yaw (optionally pitch)
///   headLocal = OpenXR LOCAL-space eye pose relative to the recenter origin
/// <see cref="WorldToLocal"/> is the inverse, used to place the HUD quad.
/// </summary>
public sealed class VrRig
{
    public readonly Camera[] Eyes = new Camera[2];
    public readonly EyeTarget[] Targets = new EyeTarget[2];
    private readonly GameObject _root;

    private Camera _source;            // the game camera we follow (null = eyes off)
    private int _sourceId;
    private float _nextSync;

    public bool Active { get; private set; }
    public bool MenuOpen { get; private set; }

    // Recenter origin: head pose in OpenXR LOCAL space (Unity handedness), yaw only.
    public Vector3 OriginPos;
    public Quaternion OriginYaw = Quaternion.identity;
    public bool HasOrigin;

    // Anchor of the current frame, captured in ApplyViews.
    private Vector3 _anchorPos, _camPos;
    private Quaternion _anchorRot = Quaternion.identity, _camRot = Quaternion.identity;
    private float _anchorScale = 1f, _camFovY = 40f, _camAspect = 16f / 9f;
    private bool _hasAnchor;

    public VrRig(int width, int height, long swapchainFormat)
    {
        _root = new GameObject("VibelightValley_Rig");
        Object.DontDestroyOnLoad(_root); // deliberately no hideFlags: hidden cameras may be skipped by the pipeline
        for (int i = 0; i < 2; i++)
        {
            Targets[i] = new EyeTarget(i == 0 ? "EyeL" : "EyeR", width, height, 24, swapchainFormat);
            var go = new GameObject(i == 0 ? "VibelightValley_EyeL" : "VibelightValley_EyeR");
            go.transform.SetParent(_root.transform, false);
            var cam = go.AddComponent<Camera>();
            go.AddComponent<UniversalAdditionalCameraData>();
            cam.enabled = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.targetTexture = Targets[i].Texture;
            Eyes[i] = cam;
        }
        _format = swapchainFormat;
        _eyeIds = new[] { Eyes[0].GetInstanceID(), Eyes[1].GetInstanceID() };
        PipelineMonitor.BeginCamera += OnBeginCamera;
        PipelineMonitor.EndCamera += OnEndCamera;
        PipelineMonitor.BeginContext += OnBeginContext;
        PipelineMonitor.EndContext += OnEndContext;
        Log.Info($"VrRig created: eyes {width}x{height}");
    }

    private readonly long _format;

    /// <summary>
    /// New eye resolution. The caller must have fenced the render thread first: the old textures are destroyed here.
    /// </summary>
    public void Resize(int width, int height)
    {
        for (int i = 0; i < 2; i++)
        {
            Targets[i].Destroy();
            Targets[i] = new EyeTarget(i == 0 ? "EyeL" : "EyeR", width, height, 24, _format);
            Eyes[i].targetTexture = Targets[i].Texture;
        }
        Log.Info($"VrRig: eyes resized to {width}x{height}");
    }

    // ------------------------------------------------------------------ lighter desktop view
    // In the world the eyes render everything the headset shows; the game camera's own render only feeds the desktop
    // window - a full third world render. With DesktopView Mirror/Minimal it renders nothing instead (no geometry,
    // post-processing or shadows), and the desktop gets a mirror of the left eye. The game rewrites its camera
    // settings every frame, so the stripped values are applied only for the duration of the render context that
    // contains the game camera (after all game scripts ran) and restored right after. The game never sees them.
    private struct SavedCamera
    {
        public int CullingMask;
        public CameraClearFlags ClearFlags;
        public Color Background;
        public bool Hdr, PostFX, Shadows, DepthTex, ColorTex;
        public AntialiasingMode AA;
    }

    private Camera _stripped;
    private SavedCamera _saved;
    private bool _stripLogged;

    /// <summary>True when the game camera rendered nothing this frame (the desktop needs a mirror instead).</summary>
    public bool DesktopStripped { get; private set; }

    private void OnBeginContext(UnityEngine.Rendering.ScriptableRenderContext ctx, Il2CppSystem.Collections.Generic.List<Camera> cams)
    {
        var c = _source;
        if (c == null || !Active || _stripped != null || Settings.Desktop.Value == DesktopView.Full) return;
        int id = c.GetInstanceID();
        bool inContext = false;
        for (int i = 0; i < cams.Count && !inContext; i++) inContext = cams[i] != null && cams[i].GetInstanceID() == id;
        if (!inContext) return;

        var ad = c.GetComponent<UniversalAdditionalCameraData>();
        _saved = new SavedCamera
        {
            CullingMask = c.cullingMask, ClearFlags = c.clearFlags, Background = c.backgroundColor, Hdr = c.allowHDR,
            PostFX = ad != null && ad.renderPostProcessing, Shadows = ad == null || ad.renderShadows,
            DepthTex = ad != null && ad.requiresDepthTexture, ColorTex = ad != null && ad.requiresColorTexture,
            AA = ad != null ? ad.antialiasing : AntialiasingMode.None,
        };
        _stripped = c;
        c.cullingMask = 0;
        c.clearFlags = CameraClearFlags.SolidColor;
        c.backgroundColor = Color.black;
        c.allowHDR = false;
        if (ad != null)
        {
            ad.renderPostProcessing = false;
            ad.antialiasing = AntialiasingMode.None;
            ad.renderShadows = false;
            ad.requiresDepthTexture = false;
            ad.requiresColorTexture = false;
        }
        DesktopStripped = true;
        if (_stripLogged) return;
        _stripLogged = true;
        Log.Info($"Desktop view {Settings.Desktop.Value}: game camera '{c.name}' skips its world render while VR shows the world");
    }

    private void OnEndContext(Il2CppSystem.Collections.Generic.List<Camera> cams) => RestoreGameCamera();

    private void RestoreGameCamera()
    {
        var c = _stripped;
        _stripped = null;
        if (c == null) return;
        try
        {
            c.cullingMask = _saved.CullingMask;
            c.clearFlags = _saved.ClearFlags;
            c.backgroundColor = _saved.Background;
            c.allowHDR = _saved.Hdr;
            var ad = c.GetComponent<UniversalAdditionalCameraData>();
            if (ad == null) return;
            ad.renderPostProcessing = _saved.PostFX;
            ad.antialiasing = _saved.AA;
            ad.renderShadows = _saved.Shadows;
            ad.requiresDepthTexture = _saved.DepthTex;
            ad.requiresColorTexture = _saved.ColorTex;
        }
        catch (Exception ex) { Log.Limited("restorecam", 5, $"Restoring the game camera failed: {ex.Message}", 'W'); }
    }

    // ------------------------------------------------------------------ per-eye render effects
    // Some of the game's full-screen effects may look different per eye (e.g. computed for the game camera, or
    // screen-space patterns), which is uncomfortable in stereo. Rendering.EyeDisabledFeatures switches chosen ones
    // off for the eye cameras only; F3 tries them one at a time to find the culprit.
    private readonly FeatureSuspension _eyeFeatures = new();
    private readonly int[] _eyeIds;
    private int _diagFeature = -1;      // F3: index of the single feature switched off for testing (-1 = none)
    private string _disabledRaw;
    private string[] _disabled = Array.Empty<string>();

    private bool IsEyeId(int id) => id == _eyeIds[0] || id == _eyeIds[1];

    // Shadow distance is a URP-asset setting shared by all cameras, so it is shortened only while an eye renders.
    private UniversalRenderPipelineAsset _urp;
    private float _savedShadowDistance = -1f;

    private void LimitEyeShadows()
    {
        float d = Settings.EyeShadowDistance.Value;
        if (d <= 0f) return;
        _urp ??= UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
        if (_urp == null || _urp.shadowDistance <= d) return;
        _savedShadowDistance = _urp.shadowDistance;
        _urp.shadowDistance = d;
    }

    private void RestoreShadows()
    {
        if (_savedShadowDistance < 0f || _urp == null) return;
        _urp.shadowDistance = _savedShadowDistance;
        _savedShadowDistance = -1f;
    }

    // LOD selection (and LOD culling, i.e. pop-in) depends on an object's size on screen, which depends on the camera's
    // field of view. The eyes see ~100 degrees vs the game camera's ~40, so objects look several times smaller and would
    // switch to low detail or vanish much closer - at slightly different moments per eye. QualitySettings.lodBias is
    // global, so it is raised only while an eye renders: automatically by tan(eyeFov/2) / tan(gameFov/2).
    private float _savedLodBias = -1f;
    private float _eyeFovY = 100f;
    private bool _lodLogged;

    private void BoostLod()
    {
        float factor = Settings.EyeLodBias.Value;
        if (factor <= 0f && _source != null)
            factor = Mathf.Tan(_eyeFovY * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(Mathf.Max(5f, _camFovY) * 0.5f * Mathf.Deg2Rad);
        factor = Mathf.Clamp(factor, 1f, 8f);
        if (factor <= 1.01f) return;
        _savedLodBias = QualitySettings.lodBias;
        QualitySettings.lodBias = _savedLodBias * factor;
        if (_lodLogged) return;
        _lodLogged = true;
        Log.Info($"Eye LOD bias {_savedLodBias:F2} x {factor:F2} = {_savedLodBias * factor:F2} (eye FOV {_eyeFovY:F0} vs game {_camFovY:F0} deg)");
    }

    private void RestoreLod()
    {
        if (_savedLodBias < 0f) return;
        QualitySettings.lodBias = _savedLodBias;
        _savedLodBias = -1f;
    }

    private void OnBeginCamera(Camera cam)
    {
        if (!IsEyeId(cam.GetInstanceID())) return;
        LimitEyeShadows();
        if (_savedLodBias < 0f) BoostLod();
        string raw = Settings.EyeDisabledFeatures.Value ?? "";
        if (raw != _disabledRaw)
        {
            _disabledRaw = raw;
            _disabled = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (_disabled.Length > 0) Log.Info($"Eye cameras render without: {string.Join(", ", _disabled)}");
        }
        if (_disabled.Length == 0 && _diagFeature < 0) return;
        var diag = _diagFeature >= 0 && _diagFeature < _eyeFeatures.All.Count ? _eyeFeatures.All[_diagFeature] : null;
        _eyeFeatures.Suspend(f => f == diag || Array.Exists(_disabled, n => n.Equals(f.name, StringComparison.OrdinalIgnoreCase)));
    }

    private void OnEndCamera(Camera cam)
    {
        if (cam != null && !IsEyeId(cam.GetInstanceID())) return;
        _eyeFeatures.Restore();
        RestoreShadows();
        RestoreLod();
    }

    /// <summary>F3: switch the next single render effect off in the headset (cycles back to none).</summary>
    public string CycleDiagnosticFeature()
    {
        var all = _eyeFeatures.All;
        _diagFeature = _diagFeature + 1 >= all.Count ? -1 : _diagFeature + 1;
        return _diagFeature < 0 ? "all effects on" : $"'{all[_diagFeature].name}' OFF ({_diagFeature + 1}/{all.Count})";
    }

    // ------------------------------------------------------------------ per frame (Update)

    /// <summary>Picks the game camera to follow, refreshes the copied settings, and enables/disables the eyes.</summary>
    public void Prepare(bool wantActive)
    {
        RestoreGameCamera();          // safety: never leave the game camera stripped outside a render context
        DesktopStripped = false;
        for (int i = 0; i < 2; i++)
        {
            if (Targets[i].EnsureValid()) Eyes[i].targetTexture = Targets[i].Texture;
        }

        Camera main = wantActive ? Camera.main : null;
        if (main != null && (!main.isActiveAndEnabled || IsEye(main))) main = null;

        // DDLV menus each bring their own MainCamera-tagged camera that renders a blurred backdrop.
        // Never follow those; the controller shows the virtual screen instead.
        bool menu = main != null && IsMenuCamera(main);
        if (menu != MenuOpen)
        {
            MenuOpen = menu;
            Log.Info($"VrRig: menu camera {(menu ? "active -> eyes off" : "gone")}");
        }
        if (menu) main = null;

        int id = main == null ? 0 : main.GetInstanceID();
        if (id != _sourceId)
        {
            Log.Info($"VrRig: following {(main == null ? "nothing (eyes off)" : Report.PathOf(main.transform))}");
            _source = main;
            _sourceId = id;
            _nextSync = 0;
        }
        if (main != null && Time.unscaledTime >= _nextSync)
        {
            SyncFrom(main);
            _nextSync = Time.unscaledTime + 2f; // the game rarely changes camera settings; 2 s is plenty
        }

        bool active = main != null;
        if (active == Active) return;
        Active = active;
        Eyes[0].enabled = Eyes[1].enabled = active;
        Log.Info($"VrRig: eyes {(active ? "ENABLED" : "disabled")}");
    }

    private bool IsEye(Camera c)
    {
        int id = c.GetInstanceID();
        return id == Eyes[0].GetInstanceID() || id == Eyes[1].GetInstanceID();
    }

    // Menu classification needs a hierarchy path walk, so it is cached per camera instance.
    private readonly Dictionary<int, bool> _menuCache = new();
    private string _menuMarkersRaw;
    private string[] _menuMarkers = Array.Empty<string>();

    private bool IsMenuCamera(Camera c)
    {
        string raw = Settings.MenuCameraMarkers.Value ?? "";
        if (raw != _menuMarkersRaw)
        {
            _menuMarkersRaw = raw;
            _menuMarkers = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _menuCache.Clear();
        }
        int id = c.GetInstanceID();
        if (_menuCache.TryGetValue(id, out bool isMenu)) return isMenu;
        string path = Report.PathOf(c.transform);
        isMenu = Array.Exists(_menuMarkers, m => path.Contains(m, StringComparison.OrdinalIgnoreCase));
        if (_menuCache.Count > 256) _menuCache.Clear();
        _menuCache[id] = isMenu;
        return isMenu;
    }

    /// <summary>Copies the game camera's settings (culling, clear, URP data) onto both eye cameras.</summary>
    private void SyncFrom(Camera main)
    {
        var src = main.GetComponent<UniversalAdditionalCameraData>();
        for (int i = 0; i < 2; i++)
        {
            var e = Eyes[i];
            try { e.CopyFrom(main); }
            catch (Exception ex) { Log.Limited("copyfrom", 5, $"Camera.CopyFrom failed: {ex.Message}", 'E'); }

            // CopyFrom copies everything, including things the eyes must not inherit.
            e.tag = "Untagged"; // never become Camera.main
            e.name = i == 0 ? "VibelightValley_EyeL" : "VibelightValley_EyeR";
            e.stereoTargetEye = StereoTargetEyeMask.None;
            e.targetTexture = Targets[i].Texture;
            e.depth = main.depth - 2 + i;
            e.allowDynamicResolution = false;
            e.allowMSAA = false;
            e.rect = new Rect(0, 0, 1, 1);
            e.farClipPlane = main.farClipPlane;
            if (src != null) CopyUrp(src, main.transform, e.GetComponent<UniversalAdditionalCameraData>());
        }
    }

    private static void CopyUrp(UniversalAdditionalCameraData s, Transform srcTransform, UniversalAdditionalCameraData d)
    {
        if (d == null) return;
        try
        {
            d.renderType = CameraRenderType.Base;
            d.renderPostProcessing = s.renderPostProcessing;
            d.antialiasing = s.antialiasing;
            d.antialiasingQuality = s.antialiasingQuality;
            d.renderShadows = s.renderShadows && Settings.EyeShadows.Value; // each eye renders its own shadow maps
            d.requiresDepthTexture = s.requiresDepthTexture;
            d.requiresColorTexture = s.requiresColorTexture;
            d.volumeLayerMask = s.volumeLayerMask;
            d.volumeTrigger = s.volumeTrigger != null ? s.volumeTrigger : srcTransform;
            d.stopNaN = s.stopNaN;
            d.dithering = s.dithering;
            // m_RendererIndex is private in URP; the interop assembly exposes it as a property.
            if (s.GetType().GetProperty("m_RendererIndex")?.GetValue(s) is int rendererIndex) d.SetRenderer(rendererIndex);
        }
        catch (Exception ex) { Log.Limited("copyurp", 5, $"Copy URP camera data failed: {ex.Message}", 'E'); }
    }

    // ------------------------------------------------------------------ poses

    public void Recenter(in XrPosef head)
    {
        OriginPos = XrMath.ToUnity(head.Position);
        OriginYaw = XrMath.YawOnly(XrMath.ToUnity(head.Orientation));
        HasOrigin = true;
        Log.Info($"Recentered: origin {OriginPos} yaw {OriginYaw.eulerAngles.y:F1}");
    }

    /// <summary>
    /// Called from beginContextRendering: after the game has moved its camera this frame, before URP renders.
    /// Places both eyes from the predicted OpenXR views and builds their projections. With SymmetricEyeFrustum the
    /// views' FOV is widened to a symmetric one (written back, so the submitted layer matches what was rendered).
    /// </summary>
    public void ApplyViews(ref XrView v0, ref XrView v1)
    {
        if (!Active || _source == null) return;
        var st = _source.transform;
        ComfortFilter(st, out Vector3 camPos, out float yawDeg);
        Quaternion yaw = Quaternion.Euler(0f, yawDeg, 0f);
        _anchorRot = Settings.FollowPitch.Value ? Quaternion.Euler(st.eulerAngles.x, yawDeg, 0f) : yaw;
        _anchorPos = camPos + yaw * new Vector3(0f, Settings.AnchorUp.Value, Settings.AnchorForward.Value);
        _anchorScale = Settings.WorldScale.Value;
        _camPos = st.position;
        _camRot = st.rotation;
        _camFovY = _source.fieldOfView;
        _camAspect = _source.aspect;
        _hasAnchor = true;

        Quaternion invOrigin = Quaternion.Inverse(OriginYaw);
        float near = Mathf.Max(0.005f, Settings.NearClip.Value * _anchorScale);
        float far = Settings.EyeDrawDistance.Value > 0f ? Mathf.Min(Settings.EyeDrawDistance.Value, _source.farClipPlane) : _source.farClipPlane;
        bool symmetric = Settings.SymmetricEyes.Value;
        if (symmetric)
        {
            v0.Fov = XrMath.Symmetric(v0.Fov);
            v1.Fov = XrMath.Symmetric(v1.Fov);
        }
        for (int i = 0; i < 2; i++)
        {
            XrView v = i == 0 ? v0 : v1;
            var eye = Eyes[i];
            Vector3 local = invOrigin * (XrMath.ToUnity(v.Pose.Position) - OriginPos);
            Quaternion localRot = invOrigin * XrMath.ToUnity(v.Pose.Orientation);
            eye.transform.SetPositionAndRotation(_anchorPos + _anchorRot * (local * _anchorScale), _anchorRot * localRot);
            eye.nearClipPlane = near;
            eye.farClipPlane = far;
            if (symmetric)
            {
                // fieldOfView/aspect describe the projection exactly, for effects that rebuild view rays from them.
                eye.fieldOfView = 2f * v.Fov.AngleUp * Mathf.Rad2Deg;
                _eyeFovY = eye.fieldOfView;
                eye.aspect = Mathf.Tan(v.Fov.AngleRight) / Mathf.Tan(v.Fov.AngleUp);
            }
            eye.projectionMatrix = XrMath.Projection(v.Fov, near, far);
        }
    }

    // ------------------------------------------------------------------ comfort filter
    // Riding the game camera 1:1 is nauseating: the right stick swings it up/down on an arc and spins it around the
    // character quickly. So:
    //  - Character mode (default): fixed height/distance behind the character; only the camera's turning is used.
    //  - height  : eased with time constant HeightSmoothing (stairs, jumps, or the camera arc in camera mode)
    //  - turning : eased with time constant TurnSmoothing, orbiting the character so it stays in view.
    // Walking is not delayed: the reference point moves every frame.
    private float _smoothY, _smoothYaw, _lastSmoothYaw;
    private Vector3 _lastCamPos;
    private bool _smoothInit;

    /// <summary>Turn rate of the view in degrees per second (drives the vignette).</summary>
    public float TurnSpeed { get; private set; }

    private readonly PlayerLocator _player = new();

    private void ComfortFilter(Transform cam, out Vector3 pos, out float yawDeg)
    {
        float camYaw = XrMath.YawOnly(cam.rotation).eulerAngles.y;
        float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);

        // Character mode: the reference point is the character's feet and the game camera's position is ignored, so
        // its up/down tilt arc has no effect at all. Otherwise (or if the character can't be found) ride the camera.
        Vector3 feet = default;
        bool character = Settings.Follow.Value == FollowMode.Character && _player.TryGetPosition(out feet);
        Vector3 reference = character ? feet : cam.position;

        // Snap on the first frame, on mode switches and on teleports/cuts instead of drifting for seconds.
        bool jump = !_smoothInit || character != _lastWasCharacter || (reference - _lastCamPos).sqrMagnitude > 5f * 5f;
        _lastCamPos = reference;
        _lastWasCharacter = character;
        _smoothInit = true;

        float th = Settings.HeightSmoothing.Value, tt = Settings.TurnSmoothing.Value;
        _smoothY = jump || th <= 0f ? reference.y : Mathf.Lerp(_smoothY, reference.y, 1f - Mathf.Exp(-dt / th));
        _smoothYaw = jump || tt <= 0f ? camYaw : Mathf.LerpAngle(_smoothYaw, camYaw, 1f - Mathf.Exp(-dt / tt));
        TurnSpeed = jump ? 0f : Mathf.Abs(Mathf.DeltaAngle(_lastSmoothYaw, _smoothYaw)) / dt;
        _lastSmoothYaw = _smoothYaw;
        yawDeg = _smoothYaw;
        Quaternion smoothYaw = Quaternion.Euler(0f, _smoothYaw, 0f);

        if (character)
        {
            // Hover behind the character, turning around it with the (smoothed) game camera direction.
            pos = new Vector3(feet.x, _smoothY + Settings.FollowHeight.Value, feet.z) - smoothYaw * new Vector3(0f, 0f, Settings.FollowDistance.Value);
            return;
        }
        // Camera mode: orbit a pivot OrbitDistance in front of the camera (roughly the character) while turning.
        float d = Settings.OrbitDistance.Value;
        Vector3 pivot = reference + Quaternion.Euler(0f, camYaw, 0f) * new Vector3(0f, 0f, d);
        pos = pivot - smoothYaw * new Vector3(0f, 0f, d);
        pos.y = _smoothY;
    }

    private bool _lastWasCharacter;

    /// <summary>Inverse of the rig mapping: a game-world pose expressed in OpenXR LOCAL space.</summary>
    public XrPosef WorldToLocal(Vector3 worldPos, Quaternion worldRot)
    {
        Quaternion toLocal = OriginYaw * Quaternion.Inverse(_anchorRot);
        Vector3 l = toLocal * (worldPos - _anchorPos) / _anchorScale + OriginPos;
        return new XrPosef { Position = XrMath.ToXr(l), Orientation = XrMath.ToXr((toLocal * worldRot).normalized) };
    }

    /// <summary>
    /// A quad the size of the game camera's view at <paramref name="distance"/> (game metres), so the UI keeps its
    /// proportions. HudPlacement.View (default): ahead of the VR viewpoint at a fixed downward tilt, turning only with
    /// the view, so it stays in front of you whatever the camera does. HudPlacement.GameCamera: exactly in the game camera's
    /// frustum (icons line up with world objects, but the panel swings around the character with the camera).
    /// </summary>
    public bool HudQuad(float distance, out XrPosef pose, out XrExtent2Df size)
    {
        pose = XrPosef.Identity;
        size = default;
        if (!_hasAnchor || !HasOrigin) return false;
        if (Settings.HudPlacementMode.Value == HudPlacement.GameCamera)
            pose = WorldToLocal(_camPos + _camRot * new Vector3(0f, 0f, distance), _camRot);
        else
        {
            // Fixed tilt (HudPitchOffset, negative = below the horizon), deliberately NOT the game camera's pitch: the
            // right stick tilts that camera up/down and the HUD should stay put. Unity's Euler x is positive = down.
            Quaternion rot = Quaternion.Euler(-Settings.HudPitchOffset.Value, _anchorRot.eulerAngles.y, 0f);
            pose = WorldToLocal(_anchorPos + rot * new Vector3(0f, 0f, distance), rot);
        }
        float h = 2f * distance * Mathf.Tan(_camFovY * 0.5f * Mathf.Deg2Rad) / _anchorScale;
        size = new XrExtent2Df { Width = h * _camAspect, Height = h };
        return true;
    }

    /// <summary>A quad floating in front of the recenter origin (virtual screen / head-locked HUD).</summary>
    public void FrontQuad(float distance, float width, float aspectHOverW, out XrPosef pose, out XrExtent2Df size)
    {
        Vector3 pos = OriginPos + OriginYaw * new Vector3(0f, 0f, distance);
        pose = new XrPosef { Position = XrMath.ToXr(pos), Orientation = XrMath.ToXr(OriginYaw) };
        size = new XrExtent2Df { Width = width, Height = width * aspectHOverW };
    }

    public string DebugLine()
    {
        if (_source == null) return $"source=none menu={MenuOpen} active={Active}";
        var t = _source.transform;
        Vector3 eye = Eyes[0].transform.position;
        return $"source='{_source.name}' pos={t.position} fwd={t.forward} fov={_source.fieldOfView:F1} mask=0x{_source.cullingMask:X} " +
               $"eyeL={eye} eyeFromCam={eye - t.position} heightLag={_smoothY - t.position.y:F2}m yawLag={Mathf.DeltaAngle(XrMath.YawOnly(t.rotation).eulerAngles.y, _smoothYaw):F1}deg origin={OriginPos}";
    }

    public void Destroy()
    {
        PipelineMonitor.BeginCamera -= OnBeginCamera;
        PipelineMonitor.EndCamera -= OnEndCamera;
        PipelineMonitor.BeginContext -= OnBeginContext;
        PipelineMonitor.EndContext -= OnEndContext;
        _eyeFeatures.Restore();
        RestoreShadows();
        RestoreLod();
        RestoreGameCamera();
        foreach (var t in Targets) t?.Destroy();
        if (_root != null) Object.Destroy(_root);
    }
}
