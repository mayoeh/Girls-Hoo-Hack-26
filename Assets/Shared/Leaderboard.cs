using System;
using System.Collections;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// One leaderboard per game, stored in Tiger Data by the game API on Vultr (Server/game-api/leaderboard.js).
// Each game makes one Leaderboard, calls NewResult(score) when a game finishes, and draws it on its results screen:
//
//     board = new Leaderboard(this, "star", "%", 0);   // the server knows which games count lower-is-better
//     board.NewResult(82);                       // shows the current board + a name box to add the score
//     board.Draw(rect, headStyle, textStyle, buttonStyle);   // or DrawLayout(width, ...) inside GUILayout
//
// The player types a name once (remembered on this computer) and presses Enter or "Add".
// Their best score per name counts. If the server can't be reached the board just says so; the game is unaffected.
public class Leaderboard
{
    public static string BaseUrl = "https://149-28-227-46.sslip.io/api/shockwave";
    public const int MaxName = 16;
    const string NameKey = "LeaderboardName";
    const string FieldName = "leaderboard-name";

    /// <summary>True while the player is typing their name: games should ignore their keyboard shortcuts.</summary>
    public static bool Typing => GUI.GetNameOfFocusedControl() == FieldName;

    readonly MonoBehaviour host;
    readonly string game, unit;
    readonly int decimals;
    public int rows = 10;

    enum State { Idle, Loading, Sending, Ready, Offline }
    State state = State.Idle;
    float score;
    string detailJson;
    bool hasScore, submitted;
    string playerName, message = "";
    Entry[] top = new Entry[0];
    int rank, players;
    bool newBest;
    int version;   // ignores replies from an older game

    [Serializable] public class Entry { public string name; public float score; public string when; }
    [Serializable] class Reply { public bool ok; public string error; public int rank, players; public bool newBest; public Entry[] top; }
    [Serializable] class Submission { public string game, name, playerId; public float score; public int limit; }

    public Leaderboard(MonoBehaviour host, string game, string unit, int decimals)
    {
        this.host = host; this.game = game; this.unit = unit; this.decimals = decimals;
        playerName = PlayerPrefs.GetString(NameKey, "");
    }

    /// <summary>A game just finished: show the board, with a box to add this score. detailJson is optional extra info (a JSON object).</summary>
    public void NewResult(float newScore, string detailJsonObject = null)
    {
        score = newScore; detailJson = detailJsonObject;
        hasScore = true; submitted = false; message = ""; rank = 0; newBest = false;
        // Finishing a game lights its star on the Stellar website (WebGL builds on stellargame.us).
        StellarProgress.MarkComplete(game == "windtunnel" ? "shockwave" : game);
        Refresh();
    }

    /// <summary>Just show the board (no score to add).</summary>
    public void Refresh()
    {
        version++;
        state = State.Loading;
        host.StartCoroutine(Fetch(version));
    }

    public string Format(float v) =>
        v.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), CultureInfo.InvariantCulture) + unit;

    // ---------- network ----------

    IEnumerator Fetch(int v)
    {
        using (var www = UnityWebRequest.Get($"{BaseUrl}/leaderboard?game={game}&limit={rows}"))
        {
            www.timeout = 8;
            yield return www.SendWebRequest();
            if (v != version) yield break;
            if (www.result != UnityWebRequest.Result.Success) { state = State.Offline; yield break; }
            var r = Parse(www.downloadHandler.text);
            if (r == null || !string.IsNullOrEmpty(r.error)) { state = State.Offline; yield break; }
            top = r.top ?? new Entry[0];
            players = r.players;
            state = State.Ready;
        }
    }

    void Submit()
    {
        string n = (playerName ?? "").Trim();
        if (n.Length == 0) { message = "Type your name first."; return; }
        if (state == State.Sending || submitted) return;
        PlayerPrefs.SetString(NameKey, n);
        PlayerPrefs.Save();
        GUI.FocusControl(null);
        version++;
        host.StartCoroutine(Send(version, n));
    }

    IEnumerator Send(int v, string n)
    {
        state = State.Sending; message = "";
        string json = JsonUtility.ToJson(new Submission { game = game, name = n, playerId = PlayerId(), score = score, limit = rows });
        if (!string.IsNullOrEmpty(detailJson)) json = json.Substring(0, json.Length - 1) + ",\"detail\":" + detailJson + "}";
        using (var www = new UnityWebRequest($"{BaseUrl}/scores", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.timeout = 8;
            yield return www.SendWebRequest();
            if (v != version) yield break;
            if (www.result != UnityWebRequest.Result.Success) { state = State.Offline; message = "Couldn't reach the leaderboard."; yield break; }
            var r = Parse(www.downloadHandler.text);
            if (r == null) { state = State.Offline; message = "Couldn't reach the leaderboard."; yield break; }
            if (!r.ok) { state = State.Ready; message = string.IsNullOrEmpty(r.error) ? "Couldn't add that score." : r.error; yield break; }
            submitted = true;
            top = r.top ?? new Entry[0];
            rank = r.rank; players = r.players; newBest = r.newBest;
            state = State.Ready;
        }
    }

    static Reply Parse(string text)
    {
        try { return JsonUtility.FromJson<Reply>(text); } catch (Exception) { return null; }
    }

    static string PlayerId()
    {
        string id = SystemInfo.deviceUniqueIdentifier ?? "guest";
        uint h = 2166136261;
        foreach (char c in id) { h ^= c; h *= 16777619; }
        return "pc-" + h.ToString("x8");
    }

    // ---------- drawing ----------

    static GUIStyle field;
    static float Line(GUIStyle s) => Mathf.Ceil(s.CalcHeight(new GUIContent("Ag"), 1000f));

    /// <summary>How tall Draw() will be at this width.</summary>
    public float Height(GUIStyle head, GUIStyle text)
    {
        float lh = Line(text), h = Line(head) + 6;
        if (hasScore) h += Mathf.Max(lh + 12, 34) + 6;          // name box, or "You're #3"
        if (message.Length > 0) h += lh + 4;
        int n = top.Length == 0 ? 1 : Mathf.Min(rows, top.Length);
        h += n * (lh + 4) + 4;
        return h;
    }

    public void DrawLayout(float width, GUIStyle head, GUIStyle text, GUIStyle button)
    {
        Rect r = GUILayoutUtility.GetRect(width, Height(head, text), GUILayout.Width(width));
        Draw(r, head, text, button);
    }

    public void Draw(Rect r, GUIStyle head, GUIStyle text, GUIStyle button)
    {
        if (field == null || field.font != text.font || field.fontSize != text.fontSize)
            field = new GUIStyle(GUI.skin.textField) { font = text.font, fontSize = text.fontSize, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 8, 4, 4) };
        var oldAlign = text.alignment;
        var oldHeadAlign = head.alignment;
        bool oldRich = text.richText;
        text.richText = false;
        float lh = Line(text), y = r.y;
        Color dim = new Color(1f, 1f, 1f, 0.6f), gold = new Color(1f, 0.85f, 0.4f), soft = new Color(0.6f, 0.78f, 0.9f);

        head.alignment = TextAnchor.UpperLeft;
        string count = players > 0 ? $"   ({players} player{(players == 1 ? "" : "s")})" : "";
        Label(new Rect(r.x, y, r.width, Line(head)), "LEADERBOARD" + count, head, soft);
        y += Line(head) + 6;

        if (hasScore)
        {
            float rowH = Mathf.Max(lh + 12, 34);
            if (!submitted)
            {
                string prompt = $"Your score {Format(score)}. Name:";
                float pw = text.CalcSize(new GUIContent(prompt)).x + 10, bw = 110;
                text.alignment = TextAnchor.MiddleLeft;
                Label(new Rect(r.x, y, pw, rowH), prompt, text, Color.white);
                GUI.SetNextControlName(FieldName);
                var fr = new Rect(r.x + pw, y, Mathf.Max(80, r.width - pw - bw - 8), rowH);
                bool enter = Event.current.type == EventType.KeyDown && Typing &&
                             (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                playerName = GUI.TextField(fr, playerName ?? "", MaxName, field);
                GUI.enabled = state != State.Sending;
                if (GUI.Button(new Rect(fr.xMax + 8, y, bw, rowH), state == State.Sending ? "..." : "Add", button) || enter)
                {
                    if (enter) Event.current.Use();
                    Submit();
                }
                GUI.enabled = true;
            }
            else
            {
                text.alignment = TextAnchor.MiddleLeft;
                string you = rank > 0 ? $"You're #{rank} of {players}" : "Score added";
                if (newBest && rank > 0) you += "  ·  new personal best!";
                else if (rank > 0) you += "  ·  your best counts";
                Label(new Rect(r.x, y, r.width, rowH), you, text, gold);
            }
            y += rowH + 6;
        }

        if (message.Length > 0)
        {
            text.alignment = TextAnchor.UpperLeft;
            Label(new Rect(r.x, y, r.width, lh), message, text, new Color(1f, 0.65f, 0.2f));
            y += lh + 4;
        }

        if (top.Length == 0)
        {
            text.alignment = TextAnchor.UpperLeft;
            string empty = state == State.Loading ? "Loading..." : state == State.Offline ? "Leaderboard offline right now." : "No scores yet. Be the first!";
            Label(new Rect(r.x, y, r.width, lh), empty, text, dim);
        }
        else
        {
            string me = (playerName ?? "").Trim();
            for (int i = 0; i < top.Length && i < rows; i++)
            {
                var e = top[i];
                bool mine = submitted && string.Equals(e.name, me, StringComparison.OrdinalIgnoreCase);
                Color c = mine ? gold : Color.white;
                if (mine) Fill(new Rect(r.x - 4, y - 1, r.width + 8, lh + 4), new Color(1f, 0.85f, 0.4f, 0.12f));
                text.alignment = TextAnchor.UpperLeft;
                Label(new Rect(r.x, y, 44, lh), (i + 1) + ".", text, i < 3 ? gold : dim);
                Label(new Rect(r.x + 44, y, r.width - 44 - 120, lh), e.name, text, c);
                text.alignment = TextAnchor.UpperRight;
                Label(new Rect(r.xMax - 120, y, 120, lh), Format(e.score), text, c);
                y += lh + 4;
            }
        }
        text.alignment = oldAlign;
        head.alignment = oldHeadAlign;
        text.richText = oldRich;
    }

    static void Label(Rect r, string s, GUIStyle st, Color c)
    {
        var old = GUI.color;
        GUI.color = new Color(0, 0, 0, 0.8f * c.a);
        GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), s, st);
        GUI.color = c;
        GUI.Label(r, s, st);
        GUI.color = old;
    }

    static void Fill(Rect r, Color c)
    {
        if (Event.current.type != EventType.Repaint) return;
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }
}
