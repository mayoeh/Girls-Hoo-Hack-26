using System.Runtime.InteropServices;
using UnityEngine;

// Call when the player wins:   StellarProgress.MarkComplete("satellite");   ("star", "satellite" or "shockwave")
// In a WebGL build this lights the game's star on the Stellar website (same site only). Elsewhere it just logs.
public static class StellarProgress
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void StellarMarkComplete(string game);
#endif

    public static void MarkComplete(string game)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        StellarMarkComplete(game);
#else
        Debug.Log("[Stellar] completed: " + game);
#endif
    }
}
