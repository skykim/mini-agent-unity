using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Small runtime-built UI assets shared by the agent UI (speech bubble, chat bar):
/// a rounded-rect 9-slice sprite and a Korean-capable OS font lookup.
/// </summary>
public static class UiRuntimeAssets
{
    static readonly Dictionary<float, Sprite> s_Rounded = new();

    /// <summary>Rounded-rect 9-slice sprite (48px texture, corner radius r). Cached per radius.</summary>
    public static Sprite RoundedSprite(float r)
    {
        if (s_Rounded.TryGetValue(r, out var cached) && cached != null) return cached;

        const int S = 48; const float aa = 1.5f;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
        { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        float hw = S / 2f, hh = S / 2f;
        var px = new Color32[S * S];
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            float dx = Mathf.Max(Mathf.Abs(x + 0.5f - hw) - (hw - r), 0f);
            float dy = Mathf.Max(Mathf.Abs(y + 0.5f - hh) - (hh - r), 0f);
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            float a = 1f - Mathf.Clamp01((dist - (r - aa)) / aa);
            px[y * S + x] = new Color(1f, 1f, 1f, a);
        }
        tex.SetPixels32(px); tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f,
                                   0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));  // 9-slice border
        s_Rounded[r] = sprite;
        return sprite;
    }

    /// <summary>
    /// First available Korean-capable OS font, or null if none is installed —
    /// callers decide whether to fall back to the built-in font or keep the authored one.
    /// </summary>
    public static Font KoreanOsFont(int size)
    {
        foreach (var n in new[] { "Apple SD Gothic Neo", "AppleGothic", "Malgun Gothic" })
        {
            try
            {
                var f = Font.CreateDynamicFontFromOSFont(n, size);
                if (f != null) return f;
            }
            catch { }
        }
        return null;
    }
}
