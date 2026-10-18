using UnityEngine;

// Shared fonts for Shockwave's on-screen panels, same pair as Catch:
// Astro Space for titles and panel headings, Elegant Typewriter for everything else.
// In the Editor they load straight from Assets/Fonts. For a build, TestBench keeps
// references to both fonts in the scene and hands them over in Awake.
public static class ShockwaveFonts
{
    public const string TitlePath = "Assets/Fonts/AstroSpace.ttf";
    public const string BodyPath = "Assets/Fonts/ELEGANT TYPEWRITER Regular.ttf";

    static Font title, body;

    public static Font Title
    {
        get { if (!title) title = Load(TitlePath); return title; }
        set { if (value) title = value; }
    }

    public static Font Body
    {
        get { if (!body) body = Load(BodyPath); return body; }
        set { if (value) body = value; }
    }

    // Heading style: Astro Space, without Unity's fake bold (it smears the letters).
    public static GUIStyle AsTitle(GUIStyle s)
    {
        if (Title) { s.font = Title; s.fontStyle = FontStyle.Normal; }
        return s;
    }

    public static GUIStyle AsBody(GUIStyle s)
    {
        if (Body) s.font = Body;
        return s;
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
