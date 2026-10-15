using System;
using System.Collections.Generic;
using System.Text;
using VibelightValley.Core;
using VibelightValley.Diagnostics;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VibelightValley.Vr;

/// <summary>
/// Access to the URP asset's renderers and their renderer features (custom full-screen passes the game adds).
/// The renderer list is private in URP; the interop assembly exposes the backing fields as properties.
/// </summary>
public static class RendererFeatures
{
    public static List<ScriptableRendererData> RendererDataList()
    {
        var result = new List<ScriptableRendererData>();
        var urp = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
        if (urp == null) return result;
        if (urp.GetType().GetProperty("m_RendererDataList")?.GetValue(urp) is Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ScriptableRendererData> arr)
            foreach (var d in arr) result.Add(d);
        return result;
    }

    public static int DefaultRendererIndex()
    {
        var urp = GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
        return urp?.GetType().GetProperty("m_DefaultRendererIndex")?.GetValue(urp) is int i ? i : 0;
    }

    public static List<ScriptableRendererFeature> Features(ScriptableRendererData data)
    {
        var result = new List<ScriptableRendererFeature>();
        var list = data?.rendererFeatures;
        if (list == null) return result;
        for (int i = 0; i < list.Count; i++) if (list[i] != null) result.Add(list[i]);
        return result;
    }

    public static string Describe()
    {
        var sb = new StringBuilder();
        var datas = RendererDataList();
        sb.AppendLine($"URP renderers: {datas.Count} (default index {DefaultRendererIndex()})");
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
