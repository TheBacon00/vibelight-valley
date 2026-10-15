using UnityEngine;

namespace VibelightValley.Vr;

/// <summary>
/// Orientation/colour test image. Correct result in the headset:
///   TOP half GREEN, BOTTOM half RED, a WHITE square in the top-LEFT corner of the LEFT eye
///   and in the top-RIGHT corner of the RIGHT eye, and a grey ramp (dark->light, left->right) across the middle.
/// </summary>
public static class TestPattern
{
    private static Texture2D[] _tex;

    public static Texture2D Get(int eye)
    {
        if (_tex == null) _tex = new[] { Make(0), Make(1) };
        return _tex[eye];
    }

    private static Texture2D Make(int eye)
    {
        const int n = 256;
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false, false) { name = "VibelightValley_TestPattern" + eye, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
        // Unity texture row 0 is the BOTTOM of the image.
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            Color c = y >= n / 2 ? new Color(0.1f, 0.8f, 0.1f) : new Color(0.8f, 0.1f, 0.1f);
            if (y >= n / 2 - 16 && y < n / 2 + 16) { float g = x / (float)(n - 1); c = new Color(g, g, g); }
            bool inSquareY = y >= n - 64 && y < n - 16;
            bool inSquareX = eye == 0 ? x >= 16 && x < 64 : x >= n - 64 && x < n - 16;
            if (inSquareY && inSquareX) c = Color.white;
            t.SetPixel(x, y, c);
        }
        t.Apply(false, false);
        return t;
    }
}
