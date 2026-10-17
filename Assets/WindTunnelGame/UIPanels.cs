using System.Collections.Generic;
using UnityEngine;

// Shared helpers for the on-screen panels:
// - each panel reports its screen area, so the nose editor ignores clicks on it
// - collapsible panel headers (open/closed is remembered between sessions)
public static class UIPanels
{
    static readonly Dictionary<(int owner, int slot), Rect> rects = new();
    static readonly Dictionary<string, bool> collapsed = new();
    static readonly HashSet<string> pendingToggles = new();

    public static bool Typing;   // true while a number box has keyboard focus

    // guiRect uses GUI coordinates (origin top-left). slot lets one component report several panels.
    public static void Set(Object owner, Rect guiRect, int slot = 0) => rects[(owner.GetInstanceID(), slot)] = guiRect;

    // Forget a component's panels (call when it is disabled, so hidden panels don't block clicks).
    public static void Clear(Object owner)
    {
        int id = owner.GetInstanceID();
        var remove = new List<(int, int)>();
        foreach (var key in rects.Keys)
            if (key.owner == id) remove.Add(key);
        foreach (var key in remove) rects.Remove(key);
    }

    // screenPos uses Input System pixels (origin bottom-left).
    public static bool Contains(Vector2 screenPos)
    {
        var gui = new Vector2(screenPos.x, Screen.height - screenPos.y);
        foreach (var r in rects.Values)
            if (r.Contains(gui)) return true;
        return false;
    }

    public static bool IsOpen(string key)
    {
        if (!collapsed.TryGetValue(key, out bool isCollapsed))
        {
            isCollapsed = PlayerPrefs.GetInt("panel." + key, 0) == 1;
            collapsed[key] = isCollapsed;
        }
        return !isCollapsed;
    }

    // Force a panel open (e.g. the tunnel panel every time a test starts).
    public static void Open(string key)
    {
        pendingToggles.Remove(key);
        collapsed[key] = false;
        PlayerPrefs.SetInt("panel." + key, 0);
    }

    // Panel title row with a collapse button. Returns true if the panel is open (draw its contents).
    public static bool Header(string key, string title, GUIStyle titleStyle)
    {
        // Clicks are applied at the start of the next layout pass, so a panel never changes size mid-draw.
        if (Event.current.type == EventType.Layout && pendingToggles.Remove(key))
        {
            bool nowCollapsed = IsOpen(key);
            collapsed[key] = nowCollapsed;
            PlayerPrefs.SetInt("panel." + key, nowCollapsed ? 1 : 0);
        }

        bool open = IsOpen(key);
        GUILayout.BeginHorizontal();
        GUILayout.Label(title, titleStyle);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(open ? "–" : "+", GUILayout.Width(24f), GUILayout.Height(18f)))
            pendingToggles.Add(key);
        GUILayout.EndHorizontal();
        return open;
    }
}
