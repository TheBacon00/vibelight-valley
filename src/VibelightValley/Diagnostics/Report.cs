using System;
using System.Collections.Generic;
using System.Text;
using VibelightValley.Core;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace VibelightValley.Diagnostics;

/// <summary>Human-readable dumps of engine state. Every section is isolated in try/catch.</summary>
public static class Report
{
    private static void Section(StringBuilder sb, string name, Action body)
    {
        try { body(); }
        catch (Exception ex) { sb.AppendLine($"  !! {name} failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    public static string TypeName(Il2CppSystem.Object o)
    {
        try { return o == null ? "null" : o.GetIl2CppType().FullName; }
        catch { return "?"; }
    }

    public static string PathOf(Transform t)
    {
        if (t == null) return "<null>";
        var parts = new List<string>();
        int guard = 0;
        while (t != null && guard++ < 64) { parts.Add(t.name); t = t.parent; }
        parts.Reverse();
        return string.Join("/", parts);
    }

    public static string Startup()
    {
        var sb = new StringBuilder();
        Section(sb, "app", () =>
        {
            sb.AppendLine($"Unity {Application.unityVersion}  product='{Application.productName}' version='{Application.version}' platform={Application.platform}");
            sb.AppendLine($"dataPath={Application.dataPath}  persistentDataPath={Application.persistentDataPath}");
            sb.AppendLine($"targetFrameRate={Application.targetFrameRate} runInBackground={Application.runInBackground}");
        });
        Section(sb, "system", () =>
        {
            sb.AppendLine($"OS: {SystemInfo.operatingSystem}  CPU: {SystemInfo.processorType} x{SystemInfo.processorCount}  RAM: {SystemInfo.systemMemorySize}MB");
            sb.AppendLine($"GPU: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceVendor}) VRAM {SystemInfo.graphicsMemorySize}MB");
            sb.AppendLine($"Graphics API: {SystemInfo.graphicsDeviceType}  '{SystemInfo.graphicsDeviceVersion}'  multithreaded={SystemInfo.graphicsMultiThreaded}");
        });
        Section(sb, "threading", () => sb.AppendLine($"renderingThreadingMode={SystemInfo.renderingThreadingMode}"));
        Section(sb, "quality", () =>
        {
            sb.AppendLine($"colorSpace={QualitySettings.activeColorSpace} quality='{QualitySettings.names[QualitySettings.GetQualityLevel()]}' vSync={QualitySettings.vSyncCount} msaa={QualitySettings.antiAliasing}");
        });
        Section(sb, "screen", () =>
        {
            sb.AppendLine($"Screen {Screen.width}x{Screen.height} mode={Screen.fullScreenMode} fullScreen={Screen.fullScreen} current={Screen.currentResolution.width}x{Screen.currentResolution.height}@{Screen.currentResolution.refreshRateRatio.value:F2}Hz dpi={Screen.dpi}");
        });
        Section(sb, "pipeline", () => sb.Append(Pipeline()));
        return sb.ToString();
    }

    public static string Pipeline()
    {
        var sb = new StringBuilder();
        Section(sb, "rp", () =>
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            sb.AppendLine($"currentRenderPipeline: {(rp == null ? "null (built-in)" : rp.name + " [" + TypeName(rp) + "]")}");
            var urp = rp?.TryCast<UniversalRenderPipelineAsset>();
            if (urp != null)
            {
                sb.AppendLine($"  URP renderScale={urp.renderScale} msaa={urp.msaaSampleCount} hdr={urp.supportsHDR} upscaling={urp.upscalingFilter} " +
                              $"depthTex={urp.supportsCameraDepthTexture} opaqueTex={urp.supportsCameraOpaqueTexture} shadowDist={urp.shadowDistance}");
            }
        });
        Section(sb, "rpinstance", () =>
        {
            var inst = RenderPipelineManager.currentPipeline;
            sb.AppendLine($"currentPipeline instance: {TypeName(inst)}");
        });
        Section(sb, "features", () => sb.Append(Vr.RendererFeatures.Describe()));
        return sb.ToString();
    }

    public static string Cameras(bool includeInactive)
    {
        var sb = new StringBuilder();
        Section(sb, "scenes", () =>
        {
            sb.AppendLine($"Active scene '{SceneManager.GetActiveScene().name}', {SceneManager.sceneCount} loaded:");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                sb.AppendLine($"  [{i}] '{s.name}' loaded={s.isLoaded} roots={s.rootCount}");
            }
        });
        Section(sb, "main", () =>
        {
            var m = Camera.main;
            sb.AppendLine($"Camera.main = {(m == null ? "null" : PathOf(m.transform) + " id=" + m.GetInstanceID())}");
        });
        Section(sb, "cameras", () =>
        {
            var arr = includeInactive
                ? Resources.FindObjectsOfTypeAll(Il2CppType.Of<Camera>())
                : Object.FindObjectsOfType(Il2CppType.Of<Camera>());
            sb.AppendLine($"Cameras found: {arr.Length} (includeInactive={includeInactive})");
            foreach (var o in arr)
            {
                var c = o.TryCast<Camera>();
                if (c == null) continue;
                Section(sb, "camera", () => DescribeCamera(sb, c));
            }
        });
        return sb.ToString();
    }

    private static void DescribeCamera(StringBuilder sb, Camera c)
    {
        var t = c.transform;
        bool isAsset = !c.gameObject.scene.IsValid();
        sb.AppendLine($"- {PathOf(t)} id={c.GetInstanceID()} {(isAsset ? "[ASSET/PREFAB]" : "")}");
        sb.AppendLine($"    enabled={c.enabled} activeInHierarchy={c.gameObject.activeInHierarchy} tag={c.tag} layer={LayerMask.LayerToName(c.gameObject.layer)} depth={c.depth}");
        sb.AppendLine($"    pos={t.position} euler={t.eulerAngles} lossyScale={t.lossyScale}");
        sb.AppendLine($"    fov={c.fieldOfView:F1} near={c.nearClipPlane} far={c.farClipPlane} ortho={c.orthographic} aspect={c.aspect:F3} rect={c.rect}");
        sb.AppendLine($"    clear={c.clearFlags} bg={c.backgroundColor} hdr={c.allowHDR} msaa={c.allowMSAA} dynRes={c.allowDynamicResolution} stereoTarget={c.stereoTargetEye}");
        sb.AppendLine($"    target={(c.targetTexture == null ? "screen" : c.targetTexture.name + " " + c.targetTexture.width + "x" + c.targetTexture.height + " " + c.targetTexture.format)} display={c.targetDisplay}");
        sb.AppendLine($"    cullingMask=0x{c.cullingMask:X8} [{LayerNames(c.cullingMask)}]");
        sb.AppendLine($"    usePhysicalProperties={c.usePhysicalProperties} useOcclusionCulling={c.useOcclusionCulling} depthTextureMode={c.depthTextureMode}");
        try
        {
            var ad = c.GetComponent<UniversalAdditionalCameraData>();
            if (ad != null)
            {
                sb.AppendLine($"    URP: renderType={ad.renderType} postFX={ad.renderPostProcessing} aa={ad.antialiasing}/{ad.antialiasingQuality} " +
                              $"shadows={ad.renderShadows} depthTex={ad.requiresDepthTexture} colorTex={ad.requiresColorTexture} " +
                              $"volumeMask=0x{ad.volumeLayerMask.value:X} [{LayerNames(ad.volumeLayerMask.value)}] volumeTrigger={(ad.volumeTrigger == null ? "null" : ad.volumeTrigger.name)} " +
                              $"stopNaN={ad.stopNaN} dithering={ad.dithering}");
                try
                {
                    var r = ad.scriptableRenderer;
                    sb.AppendLine($"    URP renderer: {TypeName(r)}");
                }
                catch (Exception ex) { sb.AppendLine($"    URP renderer: <{ex.GetType().Name}>"); }
                try
                {
                    var stack = ad.cameraStack;
                    if (stack != null && stack.Count > 0)
                    {
                        var names = new List<string>();
                        for (int i = 0; i < stack.Count; i++) names.Add(stack[i] == null ? "null" : PathOf(stack[i].transform));
                        sb.AppendLine($"    URP cameraStack: {string.Join(", ", names)}");
                    }
                }
                catch { }
            }
            else sb.AppendLine("    URP: no UniversalAdditionalCameraData");
        }
        catch (Exception ex) { sb.AppendLine($"    URP data error: {ex.Message}"); }
        sb.AppendLine($"    components: {Components(c.gameObject)}");
        var p = t.parent;
        int depth = 0;
        while (p != null && depth++ < 4)
        {
            sb.AppendLine($"    parent[{depth}] {p.name}: {Components(p.gameObject)}");
            p = p.parent;
        }
    }

    public static string Components(GameObject go)
    {
        try
        {
            var comps = go.GetComponents(Il2CppType.Of<Component>());
            var names = new List<string>();
            foreach (var c in comps)
            {
                if (c == null) { names.Add("<missing>"); continue; }
                string n = TypeName(c);
                var b = c.TryCast<Behaviour>();
                if (b != null && !b.enabled) n += "(disabled)";
                names.Add(n);
            }
            return string.Join(", ", names);
        }
        catch (Exception ex) { return $"<{ex.Message}>"; }
    }

    public static string LayerNames(int mask)
    {
        if (mask == -1) return "Everything";
        if (mask == 0) return "Nothing";
        var l = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0)
            {
                string n = LayerMask.LayerToName(i);
                l.Add(string.IsNullOrEmpty(n) ? i.ToString() : $"{i}:{n}");
            }
        return string.Join(",", l);
    }

    public static string Canvases()
    {
        var sb = new StringBuilder();
        Section(sb, "canvases", () =>
        {
            var arr = Object.FindObjectsOfType(Il2CppType.Of<Canvas>());
            sb.AppendLine($"Active canvases: {arr.Length}");
            foreach (var o in arr)
            {
                var c = o.TryCast<Canvas>();
                if (c == null || !c.isRootCanvas) continue;
                sb.AppendLine($"- {PathOf(c.transform)} mode={c.renderMode} enabled={c.enabled} order={c.sortingOrder} layer={LayerMask.LayerToName(c.gameObject.layer)} " +
                              $"camera={(c.worldCamera == null ? "null" : c.worldCamera.name)} planeDist={c.planeDistance} scale={c.scaleFactor} pixelRect={c.pixelRect}");
            }
        });
        return sb.ToString();
    }

    public static string Volumes()
    {
        var sb = new StringBuilder();
        Section(sb, "volumes", () =>
        {
            var arr = Object.FindObjectsOfType(Il2CppType.Of<Volume>());
            sb.AppendLine($"Active volumes: {arr.Length}");
            foreach (var o in arr)
            {
                var v = o.TryCast<Volume>();
                if (v == null) continue;
                var prof = v.HasInstantiatedProfile() ? v.profile : v.sharedProfile;
                sb.AppendLine($"- {PathOf(v.transform)} global={v.isGlobal} priority={v.priority} weight={v.weight} enabled={v.enabled} layer={LayerMask.LayerToName(v.gameObject.layer)} profile={(prof == null ? "null" : prof.name)}");
                if (prof == null) continue;
                foreach (var comp in prof.components)
                {
                    if (comp == null) continue;
                    sb.AppendLine($"    {TypeName(comp)} active={comp.active}");
                }
            }
        });
        return sb.ToString();
    }

    /// <summary>Checks that every engine API the VR path needs is callable. Returns true if all critical ones pass.</summary>
    public static bool ProbeApis(out string text)
    {
        var sb = new StringBuilder();
        bool ok = true;
        void P(string name, bool critical, Action a)
        {
            try { a(); sb.AppendLine($"  OK    {name}"); }
            catch (Exception ex)
            {
                sb.AppendLine($"  {(critical ? "FAIL " : "warn ")} {name}: {ex.GetType().Name}: {ex.Message}");
                if (critical) ok = false;
            }
        }

        RenderTexture rt = null;
        P("new RenderTexture + Create + GetNativeTexturePtr", true, () =>
        {
            rt = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            if (rt.GetNativeTexturePtr() == IntPtr.Zero) throw new Exception("null native ptr");
        });
        P("Camera ctor via GameObject.AddComponent<Camera>", true, () =>
        {
            var go = new GameObject("VibelightValley_probe_cam");
            go.SetActive(false);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.projectionMatrix = Matrix4x4.Perspective(90, 1, 0.1f, 100);
            cam.worldToCameraMatrix = Matrix4x4.identity;
            cam.ResetWorldToCameraMatrix();
            cam.ResetProjectionMatrix();
            var ad = go.AddComponent<UniversalAdditionalCameraData>();
            ad.renderPostProcessing = true;
            if (Camera.main != null) cam.CopyFrom(Camera.main);
            Object.Destroy(go);
        });
        P("RenderPipelineManager.beginContextRendering add/remove", true, () =>
        {
            var d = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>>(
                new Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>((_, _) => { }));
            RenderPipelineManager.add_beginContextRendering(d);
            RenderPipelineManager.remove_beginContextRendering(d);
        });
        P("RenderPipelineManager.endContextRendering add/remove", true, () =>
        {
            var d = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>>(
                new Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>((_, _) => { }));
            RenderPipelineManager.add_endContextRendering(d);
            RenderPipelineManager.remove_endContextRendering(d);
        });
        P("RenderPipelineManager.beginCameraRendering add/remove", false, () =>
        {
            var d = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                new Action<ScriptableRenderContext, Camera>((_, _) => { }));
            RenderPipelineManager.add_beginCameraRendering(d);
            RenderPipelineManager.remove_beginCameraRendering(d);
        });
        P("Graphics.Blit(rt, rt2)", true, () =>
        {
            var rt2 = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt2.Create();
            Graphics.Blit(rt, rt2, new Vector2(1, -1), new Vector2(0, 1));
            rt2.Release();
            Object.Destroy(rt2);
        });
        P("CommandBuffer ClearRenderTarget + Graphics.ExecuteCommandBuffer", true, () =>
        {
            var cb = new CommandBuffer { name = "VibelightValley_probe" };
            cb.SetRenderTarget(rt);
            cb.ClearRenderTarget(true, true, Color.magenta);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
        });
        P("ScreenCapture.CaptureScreenshotIntoRenderTexture (exists)", false, () =>
        {
            var m = typeof(ScreenCapture).GetMethod("CaptureScreenshotIntoRenderTexture");
            if (m == null) throw new MissingMethodException("not in interop");
        });
        P("GL.IssuePluginEvent (exists)", true, () =>
        {
            var m = typeof(GL).GetMethod("IssuePluginEvent", new[] { typeof(IntPtr), typeof(int) });
            if (m == null) throw new MissingMethodException("not in interop (icall fallback will be used)");
        });
        P("Texture2D.ReadPixels/EncodeToPNG (F10 dumps)", false, () =>
        {
            var t = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            t.ReadPixels(new Rect(0, 0, 16, 16), 0, 0);
            RenderTexture.active = prev;
            var png = ImageConversion.EncodeToPNG(t);
            if (png == null || png.Length == 0) throw new Exception("empty png");
            Object.Destroy(t);
        });
        P("Volume/VolumeProfile runtime creation", false, () =>
        {
            var prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var dof = ScriptableObject.CreateInstance<DepthOfField>();
            prof.components.Add(dof);
            Object.Destroy(dof);
            Object.Destroy(prof);
        });
        if (rt != null) { rt.Release(); Object.Destroy(rt); }

        text = sb.ToString();
        return ok;
    }
}
