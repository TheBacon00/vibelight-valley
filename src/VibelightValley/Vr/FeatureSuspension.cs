using System;
using System.Collections.Generic;
using UnityEngine.Rendering.Universal;

namespace VibelightValley.Vr;

/// <summary>
/// Temporarily switches off some of the game's URP renderer features (Weather, Painterly, FilmEffect, ...) for the
/// duration of one camera's render: call <see cref="Suspend"/> from beginCameraRendering and <see cref="Restore"/>
/// from endCameraRendering. Features are shared by every camera on the renderer, so they must always be restored.
/// (URP reads isActive while building that camera's passes, which happens after beginCameraRendering.)
/// </summary>
public sealed class FeatureSuspension
{
    private List<ScriptableRendererFeature> _all;
    private readonly List<ScriptableRendererFeature> _suspended = new();

    /// <summary>All renderer features of the game's URP renderers (read once via reflection, then cached).</summary>
    public List<ScriptableRendererFeature> All
    {
        get
        {
            if (_all != null) return _all;
            _all = new List<ScriptableRendererFeature>();
            foreach (var data in RendererFeatures.RendererDataList()) _all.AddRange(RendererFeatures.Features(data));
            return _all;
        }
    }

    public List<ScriptableRendererFeature> Suspended => _suspended;

    public void Suspend(Func<ScriptableRendererFeature, bool> which)
    {
        foreach (var f in All)
            if (f != null && f.isActive && which(f)) { f.SetActive(false); _suspended.Add(f); }
    }

    public void Restore()
    {
        foreach (var f in _suspended) if (f != null) f.SetActive(true);
        _suspended.Clear();
    }
}
