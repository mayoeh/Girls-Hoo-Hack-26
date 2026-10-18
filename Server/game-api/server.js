// Shockwave feedback server: the game sends its recent tunnel runs here, this calls Gemini,
// and returns a short, friendly tip. The Gemini key stays on this machine, never in the game.
//
//   1. Put your key in a file called .env next to this one:   GEMINI_API_KEY=your-key-here
//   2. node server.js              (starts on http://localhost:3000)
//   On Vultr it runs as the "shockwave" service behind Caddy: see deploy/README.md.
//      node server.js --selftest   (sends one sample request to Gemini and prints the reply)
//
// Star game (breathing): see star.js. It stores each game in Tiger Data (needs `npm install` for the "pg" package)
//      node server.js --star-selftest   (sends a made-up breathing game through Tiger Data + Gemini)

const http = require("http");
const fs = require("fs");
const path = require("path");

loadEnv(path.join(__dirname, ".env"));
const star = require("./star");   // after loadEnv, so it sees TIGER_DATABASE_URL
const boards = require("./leaderboard");
const PORT = Number(process.env.PORT || 3000);
const HOST = process.env.HOST || undefined;                    // Vultr sets 127.0.0.1 (only Caddy can reach it)
const API_KEY = process.env.GEMINI_API_KEY || "";
const MODEL = process.env.GEMINI_MODEL || "gemini-3.5-flash-lite";
const THINKING = process.env.GEMINI_THINKING || "";          // optional: low / medium / high, if the model supports it
const TIMEOUT_MS = Number(process.env.GEMINI_TIMEOUT_MS || 5000);

// ---------- the prompt ----------

const SYSTEM = `You are a friendly lab partner in a wind tunnel game. The player shapes the nose of a model rocket,
tests it in a supersonic wind tunnel, and tries to keep the airflow smooth along the model for as long as possible.
Players have little or no background in engineering or aerodynamics.

How the tunnel behaves (use this to reason, but never state it as a rule or give the answer away):
- A blunter, rounder tip makes the airflow turn rough much sooner. This effect is strongest at lower tunnel speeds.
- A wider nose angle makes the airflow turn rough a little sooner.
- At higher tunnel speeds, the tunnel speed itself matters much less for sharp noses than for blunt ones.
- Each run has a little random variation, so a change of 1 or 2 percentage points can just be noise.
These patterns come from real experiments by NASA engineer Mary Jackson in the late 1950s.

Write the tip for the run that just finished:
- 2 short sentences, in plain, everyday language. No jargon. If you must use a term like "nose angle" or
  "bluntness", say what it means in a few words.
- Compare the latest run with the earlier ones: say what changed and what happened to the smooth flow.
- Point to ONE thing worth changing or comparing next. Do not give exact numbers to use, and do not reveal the rules above.
- Be encouraging and specific, never condescending. No greetings, no sign-offs, no emojis, no markdown.`;

function describeRuns(runs) {
  const lines = runs.map((r, i) => {
    const latest = i === runs.length - 1 ? " (latest)" : "";
    return `${r.name}${latest}: tunnel speed Mach ${r.mach.toFixed(2)}, smooth flow ${pct(r.smoothFlow)} of the model, ` +
      `nose angle ${Math.round(r.noseAngle)} degrees, bluntness ${r.bluntness.toFixed(2)} (0 = needle-sharp, 1 = fully round), ` +
      `nose length ${r.length.toFixed(2)} body radii, tip bend ${Math.round(r.tipBend)} degrees` +
      (r.mirrored ? "" : ", top and bottom shaped differently");
  });
  return `Runs so far, oldest first:\n${lines.join("\n")}\n\nWrite the tip for the latest run.`;
}

const pct = (f) => `${Math.round(f * 100)}%`;

// ---------- Gemini ----------

async function askGemini(runs) {
  if (!API_KEY) throw new Error("GEMINI_API_KEY is not set (add it to .env)");

  const body = {
    model: MODEL,
    system_instruction: SYSTEM,
    input: describeRuns(runs),
    generation_config: { max_output_tokens: 1024 },   // covers any thinking plus the answer
  };
  if (THINKING) body.generation_config.thinking_level = THINKING;

  const res = await fetch("https://generativelanguage.googleapis.com/v1beta/interactions", {
    method: "POST",
    headers: { "Content-Type": "application/json", "x-goog-api-key": API_KEY },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(`Gemini ${res.status}: ${data?.error?.message || JSON.stringify(data).slice(0, 300)}`);

  const text = extractText(data).trim();
  if (!text) throw new Error("Gemini returned no text: " + JSON.stringify(data).slice(0, 300));
  return text;
}

// Reads the reply text. Handles the Interactions API shape, plus the older generateContent shape just in case.
function extractText(data) {
  if (typeof data.output_text === "string") return data.output_text;
  if (Array.isArray(data.steps)) {
    for (let i = data.steps.length - 1; i >= 0; i--) {
      const step = data.steps[i];
      if (step.type && step.type !== "model_output") continue;
      const text = (step.content || []).filter((c) => c.type === "text" && c.text).map((c) => c.text).join("");
      if (text) return text;
    }
  }
  const parts = data?.candidates?.[0]?.content?.parts;
  if (Array.isArray(parts)) return parts.map((p) => p.text || "").join("");
  return "";
}

// ---------- HTTP server ----------

function validRuns(payload) {
  const runs = Array.isArray(payload?.runs) ? payload.runs : [];
  return runs
    .filter((r) => r && typeof r.smoothFlow === "number")
    .slice(-8)
    .map((r) => ({
      name: String(r.name || "Trial").slice(0, 40),
      mach: Number(r.mach) || 0,
      smoothFlow: Number(r.smoothFlow) || 0,
      noseAngle: Number(r.noseAngle) || 0,
      bluntness: Number(r.bluntness) || 0,
      length: Number(r.length) || 0,
      tipBend: Number(r.tipBend) || 0,
      mirrored: r.mirrored !== false,
    }));
}

// Web builds of the games run on another site (itch.io, the project website, ...), so the browser needs CORS headers.
const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
  "Access-Control-Max-Age": "86400",
};

function send(res, status, obj) {
  res.writeHead(status, { "Content-Type": "application/json", ...CORS });
  res.end(JSON.stringify(obj));
}

const server = http.createServer(async (req, res) => {
  if (req.method === "OPTIONS") { res.writeHead(204, CORS); return res.end(); }   // browser preflight for POSTs
  if (req.method === "GET" && (req.url === "/health" || req.url === "/api/shockwave/health"))
    return send(res, 200, { ok: true, model: MODEL, hasKey: !!API_KEY, hasDb: star.hasDb(), dbError: star.dbStatus() || undefined });

  // Leaderboards (all games): submit a score, or read a game's board.
  if (req.method === "POST" && req.url === "/api/shockwave/scores") {
    let raw = "";
    req.on("data", (chunk) => { raw += chunk; if (raw.length > 20_000) req.destroy(); });
    req.on("end", async () => {
      let payload;
      try { payload = JSON.parse(raw); } catch { return send(res, 400, { ok: false, error: "Body must be JSON" }); }
      try { send(res, 200, await boards.submit(payload)); }
      catch (err) { console.error("[board]", err.message); send(res, 200, { ok: false, error: "Leaderboard unavailable" }); }
    });
    return;
  }
  if (req.method === "GET" && req.url.startsWith("/api/shockwave/leaderboard")) {
    const q = new URL(req.url, "http://x").searchParams;
    try { return send(res, 200, await boards.leaderboard(q.get("game") || "", q.get("limit"))); }
    catch (err) { console.error("[board]", err.message); return send(res, 200, { error: "Leaderboard unavailable" }); }
  }

  // Star game: one finished game (breathing samples + score) in, coaching + timeline + history out.
  if (req.method === "POST" && req.url === "/api/shockwave/star/session") {
    let raw = "";
    req.on("data", (chunk) => { raw += chunk; if (raw.length > 400_000) req.destroy(); });
    req.on("end", async () => {
      let payload;
      try { payload = JSON.parse(raw); } catch { return send(res, 400, { coach: "", error: "Body must be JSON" }); }
      try { send(res, 200, await star.handleSession(payload)); }
      catch (err) { console.error("[star]", err.message); send(res, 200, { coach: "", error: err.message }); }
    });
    return;
  }

  if (req.method === "POST" && req.url === "/api/shockwave/feedback") {
    let raw = "";
    req.on("data", (chunk) => { raw += chunk; if (raw.length > 50_000) req.destroy(); });
    req.on("end", async () => {
      let runs;
      try { runs = validRuns(JSON.parse(raw)); } catch { return send(res, 400, { text: "", error: "Body must be JSON" }); }
      if (runs.length === 0) return send(res, 400, { text: "", error: "No runs in the request" });
      try {
        const text = await askGemini(runs);
        console.log(`[tip] ${runs[runs.length - 1].name}: ${text}`);
        send(res, 200, { text });
      } catch (err) {
        console.error("[error]", err.message);
        send(res, 200, { text: "", error: err.message });   // the game falls back to its own tip
      }
    });
    return;
  }

  send(res, 404, { error: "Not found" });
});

// ---------- startup ----------

if (process.argv.includes("--star-selftest")) {
  console.log(`Database: ${star.hasDb() ? "Tiger Data (TIGER_DATABASE_URL)" : "none (set TIGER_DATABASE_URL to store games)"}`);
  console.log(`Model: ${MODEL}\nSending a made-up 30 s breathing game...`);
  star.handleSession(star.sampleSession())
    .then((r) => {
      console.log(`\nStored in Tiger Data: ${r.stored}${r.sessionId ? ` (session ${r.sessionId})` : ""}`);
      console.log("Timeline:", r.buckets.map((b) => `${b.start}s ${b.match < 0 ? "--" : Math.round(b.match * 100) + "%"}`).join("  "));
      console.log(`Your best: ${r.yourBest}%   Best today: ${r.bestToday}%   Earlier games: ${r.history.length}`);
      console.log(`\nCoach (${r.source || "none"}): ${r.coach || "(none)"}`);
      if (r.error) { console.log("\nProblems: " + r.error); process.exitCode = 1; }
      process.exit();
    })
    .catch((err) => { console.error("\nFailed: " + err.message); process.exit(1); });
} else if (process.argv.includes("--selftest")) {
  const sample = [
    { name: "Trial 1", mach: 2.0, smoothFlow: 0.31, noseAngle: 63, bluntness: 1.0, length: 1.0, tipBend: 0, mirrored: true },
    { name: "Trial 2", mach: 2.0, smoothFlow: 0.58, noseAngle: 48, bluntness: 0.45, length: 1.8, tipBend: 0, mirrored: true },
  ];
  console.log(`Model: ${MODEL}\nSending a sample request...`);
  askGemini(sample)
    .then((text) => console.log("\nGemini says:\n" + text))
    .catch((err) => { console.error("\nFailed: " + err.message); process.exitCode = 1; });
} else {
  server.listen(PORT, HOST, () => {
    console.log(`Shockwave feedback server on http://localhost:${PORT}  (model ${MODEL}${API_KEY ? "" : ", NO API KEY SET"})`);
    if (star.hasDb())
      star.initDb().then(() => boards.initBoards()).then(() => console.log("Tiger Data: connected, star game + leaderboard tables ready"))
        .catch((err) => console.error("Tiger Data: " + err.message));
    else console.log("Tiger Data: not configured (star games won't be saved)");
  });
}

function loadEnv(file) {
  if (!fs.existsSync(file)) return;
  for (const line of fs.readFileSync(file, "utf8").split(/\r?\n/)) {
    const m = line.match(/^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$/);
    if (m && !process.env[m[1]]) process.env[m[1]] = m[2].replace(/^["']|["']$/g, "");
  }
}
