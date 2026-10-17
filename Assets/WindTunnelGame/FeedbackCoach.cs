using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// After each tunnel run, asks the feedback server (which calls Gemini) for a short tip.
// If the server is off, slow, or errors, it shows a built-in tip instead, so the game never waits on it.
public class FeedbackCoach : MonoBehaviour
{
    [Tooltip("The feedback server's address. Cloud (always on): https://149-28-227-46.sslip.io/api/shockwave/feedback   " +
             "Local testing: http://localhost:3000/api/shockwave/feedback")]
    public string endpoint = "https://149-28-227-46.sslip.io/api/shockwave/feedback";
    [Tooltip("Turn off to always use the built-in tips (no server needed).")]
    public bool useServer = true;
    public float timeoutSeconds = 6f;
    [Tooltip("How many recent runs to send, oldest first.")]
    public int historyLength = 5;

    public string Text { get; private set; } = "";
    public bool Waiting { get; private set; }
    public string Source { get; private set; } = "";   // "Gemini" or "Built-in tip"

    [Serializable] class RunData
    {
        public string name;
        public float mach, smoothFlow, noseAngle, bluntness, length, fullness, tipBend;
        public bool mirrored;
    }
    [Serializable] class FeedbackRequest { public List<RunData> runs = new(); }
    [Serializable] class FeedbackResponse { public string text; public string error; }

    Coroutine running;

    // tested: runs with results, oldest first; the last one is the run that just finished.
    public void RequestFeedback(IList<TrialRecord> tested)
    {
        if (tested == null || tested.Count == 0) return;
        if (running != null) StopCoroutine(running);

        var req = new FeedbackRequest();
        for (int i = Mathf.Max(0, tested.Count - historyLength); i < tested.Count; i++)
            req.runs.Add(ToData(tested[i]));

        string fallback = BuiltInTip(req.runs);
        if (!useServer || string.IsNullOrEmpty(endpoint)) { Show(fallback, "Built-in tip"); return; }
        running = StartCoroutine(Send(req, fallback));
    }

    IEnumerator Send(FeedbackRequest req, string fallback)
    {
        Waiting = true;
        Text = "";
        Source = "";

        byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(req));
        string reply = null;
        using (var www = new UnityWebRequest(endpoint, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(body);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.timeout = Mathf.CeilToInt(timeoutSeconds);

            // Unity refuses plain http:// unless "Allow downloads over HTTP" is on; catch that instead of hanging.
            UnityWebRequestAsyncOperation op = null;
            try { op = www.SendWebRequest(); }
            catch (Exception ex) { Debug.LogWarning($"[FeedbackCoach] {ex.Message}. Using a built-in tip."); }
            if (op != null) yield return op;

            if (op == null) { }
            else if (www.result == UnityWebRequest.Result.Success)
            {
                var res = JsonUtility.FromJson<FeedbackResponse>(www.downloadHandler.text);
                reply = res?.text;
                if (!string.IsNullOrEmpty(res?.error)) Debug.LogWarning($"[FeedbackCoach] Server: {res.error}. Using a built-in tip.");
            }
            else
            {
                Debug.LogWarning($"[FeedbackCoach] {www.error}. Is the feedback server running? Using a built-in tip.");
            }
        }

        if (!string.IsNullOrWhiteSpace(reply)) Show(reply.Trim(), "Gemini");
        else Show(fallback, "Built-in tip");
        running = null;
    }

    void Show(string text, string source)
    {
        Text = text;
        Source = source;
        Waiting = false;
    }

    static RunData ToData(TrialRecord t) => new()
    {
        name = t.name,
        mach = t.mach,
        smoothFlow = t.smoothFlow,
        noseAngle = t.noseAngle,
        bluntness = t.bluntness,
        length = t.shape != null ? t.shape.length : 0f,
        fullness = t.fullness,
        tipBend = t.tipBend,
        mirrored = t.shape == null || t.shape.mirrored,
    };

    // Used when Gemini isn't available: compares the last two runs in plain language.
    static string BuiltInTip(List<RunData> runs)
    {
        var now = runs[^1];
        if (runs.Count == 1)
            return now.smoothFlow >= 0.75f
                ? "Great first run: the air stayed smooth along most of the model. Try a different tunnel speed to see if your design holds up."
                : "First run done. Watch where the streaks turn orange: that's where the air stops flowing smoothly. Try changing one thing about the tip and test again.";

        var prev = runs[^2];
        float diff = now.smoothFlow - prev.smoothFlow;
        string result = diff > 0.02f ? "Smooth flow went up" : diff < -0.02f ? "Smooth flow dropped" : "Smooth flow barely changed";

        string change = null;
        float db = now.bluntness - prev.bluntness, da = now.noseAngle - prev.noseAngle, dm = now.mach - prev.mach;
        if (Mathf.Abs(db) > 0.05f) change = db > 0 ? "the tip got rounder" : "the tip got sharper";
        else if (Mathf.Abs(da) > 3f) change = da > 0 ? "the nose got wider" : "the nose got slimmer";
        else if (Mathf.Abs(dm) > 0.04f) change = dm > 0 ? "the tunnel ran faster" : "the tunnel ran slower";

        return change == null
            ? $"{result}. Try changing just one thing at a time, so you can tell what made the difference."
            : $"{result}, and the biggest change was that {change}. Keep that in mind for your next design.";
    }
}
