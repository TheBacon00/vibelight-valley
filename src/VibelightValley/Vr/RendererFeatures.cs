using System.Collections.Generic;
using System.Text;
using VibelightValley.Diagnostics;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VibelightValley.Vr;

/// <summary>
/// The game's URP renderer features: custom passes (Weather, Grass, Painterly, FilmEffect, ...) that run for every
/// camera on the renderer. The renderer list is private in URP; the interop assembly exposes the backing fields as
/// properties. Reading them takes reflection and string marshalling, so the list is cached per pipeline asset.
/// </summary>
public static class RendererFeatures
{
    private static readonly List<ScriptableRendererFeature> Cached = new();
    private static readonly List<string> CachedNames = new();
    private static bool _loaded;
    private static int _assetId;

    /// <summary>Counts re-reads of the list, so callers can tell when indices into it went stale.</summary>
    public static int Version { get; private set; }

    /// <summary>Every renderer feature of the current pipeline asset, switched on or not.</summary>
    public static List<ScriptableRendererFeature> All
    {
        get
        {
            if (!_loaded) Load(CurrentAsset());
            return Cached;
        }
    }

    /// <summary>Feature names, parallel to <see cref="All"/>.</summary>
    public static List<string> Names
    {
        get
        {
            if (!_loaded) Load(CurrentAsset());
            return CachedNames;
        }
    }

    /// <summary>Re-reads the list if the game switched pipeline assets (it has one per quality level).</summary>
    public static void Refresh(UniversalRenderPipelineAsset current)
    {
        int id = current == null ? 0 : current.GetInstanceID();
        if (!_loaded || id != _assetId) Load(current);
    }

    private static void Load(UniversalRenderPipelineAsset urp)
    {
        _loaded = true;
        _assetId = urp == null ? 0 : urp.GetInstanceID();
        Cached.Clear();
        CachedNames.Clear();
        foreach (var data in RendererDataList(urp))
        foreach (var feature in Features(data))
        {
            Cached.Add(feature);
            CachedNames.Add(feature.name);
        }
        Version++;
    }

    private static UniversalRenderPipelineAsset CurrentAsset() =>
        GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();

    private static List<ScriptableRendererData> RendererDataList(UniversalRenderPipelineAsset urp)
    {
        var result = new List<ScriptableRendererData>();
        if (urp == null) return result;
        if (urp.GetType().GetProperty("m_RendererDataList")?.GetValue(urp) is Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ScriptableRendererData> arr)
            foreach (var d in arr) result.Add(d);
        return result;
    }

    private static int DefaultRendererIndex(UniversalRenderPipelineAsset urp) =>
        urp?.GetType().GetProperty("m_DefaultRendererIndex")?.GetValue(urp) is int i ? i : 0;

    private static List<ScriptableRendererFeature> Features(ScriptableRendererData data)
    {
        var result = new List<ScriptableRendererFeature>();
        var list = data?.rendererFeatures;
        if (list == null) return result;
        for (int i = 0; i < list.Count; i++) if (list[i] != null) result.Add(list[i]);
        return result;
    }

    /// <summary>The renderers and their features, for the startup report and the F9 dump.</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        var urp = CurrentAsset();
        var datas = RendererDataList(urp);
        sb.AppendLine($"URP renderers: {datas.Count} (default index {DefaultRendererIndex(urp)})");
        for (int i = 0; i < datas.Count; i++)
        {
            var d = datas[i];
            sb.AppendLine($"  [{i}] {(d == null ? "null" : d.name + " [" + Report.TypeName(d) + "]")}");
            foreach (var f in Features(d))
                sb.AppendLine($"      feature '{f.name}' [{Report.TypeName(f)}] active={f.isActive}");
        }
        return sb.ToString();
    }
}
