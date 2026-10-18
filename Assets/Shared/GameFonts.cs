using UnityEngine;

// One place for the fonts every game's on-screen text uses (Catch, Shockwave, Star):
//   - Titles, headings and anything bold -> Astro Space
//   - Everything else                    -> Clean Typewriter
//
// It also keeps OnGUI text sharp:
//   - Font atlases use Point filtering. Unity draws OnGUI text at the exact pixel size,
//     so bilinear filtering only adds blur when a label lands between two pixels
//     (common with centered text or rects computed from Screen.width / 2).
//   - Unity's "fake bold" is never applied to these fonts (it smears the letters).
//
// In the Editor the fonts load straight from Assets/Fonts. For a build, each game keeps
// font references in its scene (titleFont / bodyFont fields) and hands them over in Awake.
public static class GameFonts
{
    public const string TitlePath = "Assets/Fonts/AstroSpace.ttf";
    public const string BodyPath = "Assets/Fonts/Clean Typewriter.ttf";

    static Font title, body;
    static bool hooked;

    public static Font Title
    {
        get { if (!title) title = Load(TitlePath); MakeCrisp(title); return title; }
        set { if (value && !IsOldFont(value)) title = value; }
    }

    public static Font Body
    {
        get { if (!body) body = Load(BodyPath); MakeCrisp(body); return body; }
        set { if (value && !IsOldFont(value)) body = value; }
    }

    /// <summary>Bold styles get the title font, everything else the body font. Never fake-bold.</summary>
    public static GUIStyle Apply(GUIStyle s)
    {
        bool heading = s.fontStyle == FontStyle.Bold || s.fontStyle == FontStyle.BoldAndItalic;
        return heading ? AsTitle(s) : AsBody(s);
    }

    public static GUIStyle AsTitle(GUIStyle s)
    {
        if (Title) { s.font = Title; s.fontStyle = FontStyle.Normal; }
        return s;
    }

    public static GUIStyle AsBody(GUIStyle s)
    {
        if (Body) { s.font = Body; s.fontStyle = FontStyle.Normal; }
        return s;
    }

    /// <summary>Rounds a rect to whole pixels (another guard against soft text).</summary>
    public static Rect Px(Rect r) =>
        new Rect(Mathf.Round(r.x), Mathf.Round(r.y), Mathf.Round(r.width), Mathf.Round(r.height));

    /// <summary>True for the font we replaced, so old scene references get swapped out.</summary>
    public static bool IsOldFont(Font f) => f && f.name.ToUpperInvariant().Contains("ELEGANT");

    /// <summary>Point-filters a dynamic font's atlas, and keeps it that way when Unity rebuilds it.</summary>
    public static void MakeCrisp(Font f)
    {
        if (!f) return;
        if (!hooked)
        {
            Font.textureRebuilt += SetPoint;
            hooked = true;
        }
        SetPoint(f);
    }

    static void SetPoint(Font f)
    {
        if (f && f.material && f.material.mainTexture && f.material.mainTexture.filterMode != FilterMode.Point)
            f.material.mainTexture.filterMode = FilterMode.Point;
    }

    static Font Load(string path)
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(path);
#else
        return null;
#endif
    }
}
