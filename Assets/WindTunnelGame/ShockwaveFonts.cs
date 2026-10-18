using UnityEngine;

// Shockwave's font helper, now a thin wrapper around GameFonts (shared by every game):
// Astro Space for titles, headings and bold text, Clean Typewriter for everything else.
public static class ShockwaveFonts
{
    public const string TitlePath = GameFonts.TitlePath;
    public const string BodyPath = GameFonts.BodyPath;

    public static Font Title
    {
        get => GameFonts.Title;
        set => GameFonts.Title = value;
    }

    public static Font Body
    {
        get => GameFonts.Body;
        set => GameFonts.Body = value;
    }

    // Heading style: Astro Space, without Unity's fake bold (it smears the letters).
    public static GUIStyle AsTitle(GUIStyle s) => GameFonts.AsTitle(s);

    // Regular text. Styles marked Bold count as headings and get Astro Space.
    public static GUIStyle AsBody(GUIStyle s) => GameFonts.Apply(s);
}
