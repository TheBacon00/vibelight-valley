using System.Collections.Generic;
using UnityEngine.Rendering.Universal;

namespace VibelightValley.Vr;

/// <summary>
/// Switches some of the game's renderer features off for the duration of one camera's render: call
/// <see cref="Suspend"/> from beginCameraRendering and <see cref="Restore"/> from endCameraRendering. Features are
/// shared by every camera on the renderer, so they must always be restored.
/// (URP reads isActive while building that camera's passes, which happens after beginCameraRendering.)
/// </summary>
public sealed class FeatureSuspension
{
    private readonly List<ScriptableRendererFeature> _suspended = new();

    /// <summary>The features this instance currently holds switched off.</summary>
    public IReadOnlyList<ScriptableRendererFeature> Suspended => _suspended;

    /// <param name="which">
    /// Per feature, by index into <see cref="RendererFeatures.All"/>: switch it off? Null = all of them.
    /// Features the game itself has switched off are left alone.
    /// </param>
    public void Suspend(bool[] which = null)
    {
        var all = RendererFeatures.All;
        for (int i = 0; i < all.Count; i++)
        {
            if (which != null && (i >= which.Length || !which[i])) continue;
            var feature = all[i];
            if (feature == null || !feature.isActive) continue;
            feature.SetActive(false);
            _suspended.Add(feature);
        }
    }

    public void Restore()
    {
        foreach (var feature in _suspended)
            if (feature != null) feature.SetActive(true);
        _suspended.Clear();
    }
}
