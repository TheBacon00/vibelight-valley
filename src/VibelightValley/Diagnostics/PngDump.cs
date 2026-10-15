using System.IO;
using UnityEngine;

namespace VibelightValley.Diagnostics;

public static class PngDump
{
    /// <summary>
    /// Saves a render texture as PNG (synchronous GPU readback - debugging only). Rows appear as Unity samples them,
    /// so a correct-looking PNG of a submit texture means it is upside down for OpenXR, and vice versa.
    /// </summary>
    public static void Save(RenderTexture rt, string path)
    {
        if (rt == null) return;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, !rt.sRGB);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
        tex.Apply(false);
        RenderTexture.active = prev;
        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
        Object.Destroy(tex);
    }
}
