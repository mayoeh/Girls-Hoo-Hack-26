// Star game ("Breathe with the star"): stores each game's breathing in Tiger Data, works out simple facts about the
// player's rhythm, and asks Gemini for a short "Leavitt's notebook" entry about their star (not breathing advice).
//
// Why Tiger Data: Presage measures breathing ~30 times a second, a real time series. Each game's samples go into a
// hypertable (star_samples). The server then uses time_bucket() to see how well the player kept up in each 5-second
// stretch, and looks up the player's earlier games. Gemini turns the facts into a friendly note tied to Henrietta Leavitt.
//
// Settings (in .env, next to server.js):
//   TIGER_DATABASE_URL=postgres://tsdbadmin:PASSWORD@HOST:PORT/tsdb?sslmode=require   (from the Tiger Cloud console)
//   GEMINI_API_KEY=...                                                                 (already used by the wind tunnel)
// Without TIGER_DATABASE_URL it still works: the timeline is worked out here and nothing is saved.

const DATABASE_URL = process.env.TIGER_DATABASE_URL || process.env.DATABASE_URL || "";
const API_KEY = process.env.GEMINI_API_KEY || "";
const MODEL = process.env.GEMINI_MODEL || "gemini-3.5-flash-lite";
const THINKING = process.env.GEMINI_THINKING || "";
const TIMEOUT_MS = Number(process.env.GEMINI_TIMEOUT_MS || 6000);
const BUCKET_SECONDS = 5;

// ---------- database ----------

let pool = null;
let ready = null;          // promise: tables created
let dbError = "";

function hasDb() { return !!DATABASE_URL; }

function getPool() {
  if (!DATABASE_URL) return null;
  if (!pool) {
    const { Pool } = require("pg");   // only needed when a database is configured
    pool = new Pool({ connectionString: DATABASE_URL, max: 4, connectionTimeoutMillis: 8000, idleTimeoutMillis: 30000 });
    pool.on("error", (err) => { dbError = err.message; console.error("[db]", err.message); });
  }
  return pool;
}

// Creates the tables the first time (safe to run every start).
async function initDb() {
  const p = getPool();
  if (!p) return false;
  if (!ready) {
    ready = (async () => {
      await p.query(`CREATE TABLE IF NOT EXISTS star_sessions (
          id           BIGSERIAL PRIMARY KEY,
          started_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
          player       TEXT,
          difficulty   TEXT,
          timer_on     BOOLEAN,
          length_s     REAL,
          score        INT,
          inhale_match REAL,
          exhale_match REAL,
          tracked      REAL,
          lag_s        REAL,
          coach        TEXT)`);
      // One row per sample: when it happened, the player's breathing (0-1, NULL while we couldn't see them),
      // and the guide star at the same moment (already shifted by the player's delay, so they line up).
      await p.query(`CREATE TABLE IF NOT EXISTS star_samples (
          session_id   BIGINT      NOT NULL,
          time         TIMESTAMPTZ NOT NULL,
          t            REAL        NOT NULL,
          user_level   REAL,
          guide_level  REAL,
          guide_inhale BOOLEAN)`);
      try {
        await p.query(`SELECT create_hypertable('star_samples', by_range('time'), if_not_exists => TRUE)`);
      } catch {
        await p.query(`SELECT create_hypertable('star_samples', 'time', if_not_exists => TRUE)`);   // older TimescaleDB
      }
      await p.query(`ALTER TABLE star_sessions ADD COLUMN IF NOT EXISTS breath_seconds REAL`);
      await p.query(`ALTER TABLE star_sessions ADD COLUMN IF NOT EXISTS star_type TEXT`);
      await p.query(`CREATE INDEX IF NOT EXISTS star_samples_session_time ON star_samples (session_id, time)`);
      await p.query(`CREATE INDEX IF NOT EXISTS star_sessions_player ON star_sessions (player, started_at DESC)`);
      return true;
    })().catch((err) => { ready = null; dbError = err.message; throw err; });
  }
  return ready;
}

// ---------- request checking ----------

function clean(payload) {
  const num = (v, d = 0) => (Number.isFinite(Number(v)) ? Number(v) : d);
  const arr = (a) => (Array.isArray(a) ? a.slice(0, 2000) : []);
  const t = arr(payload?.t).map((v) => num(v));
  const u = arr(payload?.u).map((v) => (num(v, -1) < 0 ? null : Math.min(1, num(v))));   // -1 = couldn't see the player
  const g = arr(payload?.g).map((v) => Math.min(1, Math.max(0, num(v))));
  const inh = arr(payload?.inhale).map((v) => !!v);
  const n = Math.min(t.length, u.length, g.length, inh.length);
  const difficulty = ["Easy", "Medium", "Hard"].includes(payload?.difficulty) ? payload.difficulty : "Easy";
  return {
    player: String(payload?.player || "guest").slice(0, 40),
    difficulty,
    timerOn: payload?.timerOn !== false,
    length: num(payload?.length, 30),
    score: Math.round(num(payload?.score)),
    inhaleMatch: num(payload?.inhaleMatch),
    exhaleMatch: num(payload?.exhaleMatch),
    tracked: num(payload?.tracked, 1),
    lag: num(payload?.lag),
    t: t.slice(0, n), u: u.slice(0, n), g: g.slice(0, n), inhale: inh.slice(0, n),
  };
}

// ---------- the timeline (5-second buckets) ----------

// Without a database: the same calculation in JavaScript.
function bucketsLocal(s) {
  const out = [];
  for (let i = 0; i < s.t.length; i++) {
    const b = Math.floor(s.t[i] / BUCKET_SECONDS);
    out[b] ??= { start: b * BUCKET_SECONDS, sum: 0, seen: 0, total: 0 };
    out[b].total++;
    if (s.u[i] !== null) { out[b].seen++; out[b].sum += 1 - Math.abs(s.u[i] - s.g[i]); }
  }
  return out.filter(Boolean).map((b) => ({ start: b.start, match: b.seen ? b.sum / b.seen : -1, seen: b.total ? b.seen / b.total : 0 }));
}

// With Tiger Data: store the samples, then let time_bucket() build the timeline.
async function storeAndAnalyse(s) {
  const p = getPool();
  await initDb();
  const client = await p.connect();
  try {
    await client.query("BEGIN");
    const { rows } = await client.query(
      `INSERT INTO star_sessions (player, difficulty, timer_on, length_s, score, inhale_match, exhale_match, tracked, lag_s)
       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) RETURNING id, started_at`,
      [s.player, s.difficulty, s.timerOn, s.length, s.score, s.inhaleMatch, s.exhaleMatch, s.tracked, s.lag]);
    const { id, started_at } = rows[0];
    await client.query(
      `INSERT INTO star_samples (session_id, time, t, user_level, guide_level, guide_inhale)
       SELECT $1, $2::timestamptz + make_interval(secs => x.t), x.t, x.u, x.g, x.i
       FROM unnest($3::real[], $4::real[], $5::real[], $6::boolean[]) AS x(t, u, g, i)`,
      [id, started_at, s.t, s.u, s.g, s.inhale]);
    await client.query("COMMIT");

    const buckets = await client.query(
      `SELECT extract(epoch FROM time_bucket(make_interval(secs => $3), time, $2::timestamptz) - $2::timestamptz) AS start,
              avg(1 - abs(user_level - guide_level)) AS match,
              count(user_level)::float / count(*) AS seen
       FROM star_samples
       WHERE session_id = $1
       GROUP BY 1 ORDER BY 1`,
      [id, started_at, BUCKET_SECONDS]);

    const history = await client.query(
      `SELECT score, difficulty FROM star_sessions
       WHERE player = $1 AND id <> $2 ORDER BY started_at DESC LIMIT 5`, [s.player, id]);
    const best = await client.query(
      `SELECT max(score) FILTER (WHERE difficulty = $1) AS best_difficulty,
              max(score) FILTER (WHERE difficulty = $1 AND started_at > now() - interval '1 day') AS best_today
       FROM star_sessions`, [s.difficulty]);
    const mine = await client.query(`SELECT max(score) AS best FROM star_sessions WHERE player = $1`, [s.player]);

    return {
      sessionId: Number(id),
      buckets: buckets.rows.map((r) => ({ start: Number(r.start), match: r.match === null ? -1 : Number(r.match), seen: Number(r.seen) })),
      history: history.rows.map((r) => ({ score: Number(r.score), difficulty: r.difficulty })),
      yourBest: Number(mine.rows[0].best ?? s.score),
      bestToday: Number(best.rows[0].best_today ?? s.score),
      bestForDifficulty: Number(best.rows[0].best_difficulty ?? s.score),
    };
  } catch (err) {
    await client.query("ROLLBACK").catch(() => {});
    throw err;
  } finally {
    client.release();
  }
}

async function saveCoach(sessionId, coach, facts) {
  if (!sessionId) return;
  await getPool().query(`UPDATE star_sessions SET coach = $2, breath_seconds = $3, star_type = $4 WHERE id = $1`,
    [sessionId, coach || null, facts?.breathSeconds || null, facts?.starType || null]).catch(() => {});
}

// ---------- simple facts about the player's rhythm ----------

// Counts full breaths in a 0-1 curve (in = rises above 0.65 after being below 0.35) and the seconds between them.
function cycles(t, v) {
  // Smooth over ~0.5 s first so camera jitter isn't counted as extra breaths.
  v = v.map((x, i) => {
    if (x === null) return null;
    let sum = 0, n = 0;
    for (let j = Math.max(0, i - 2); j <= Math.min(v.length - 1, i + 2); j++) if (v[j] !== null) { sum += v[j]; n++; }
    return sum / n;
  });
  const starts = [];
  let low = null;
  for (let i = 0; i < t.length; i++) {
    if (v[i] === null) continue;
    if (v[i] < 0.35) low = true;
    else if (v[i] > 0.65 && low) {
      if (!starts.length || t[i] - starts[starts.length - 1] >= 1.5) starts.push(t[i]);   // nobody breathes faster than that
      low = false;
    }
    else if (v[i] > 0.65 && low === null) low = false;
  }
  const gaps = [];
  for (let i = 1; i < starts.length; i++) gaps.push(starts[i] - starts[i - 1]);
  return { count: starts.length, gaps };
}

// Leavitt's discovery, simplified: the slower a star pulses, the brighter it really is.
function starTypeFor(seconds) {
  if (seconds >= 9) return "a slow giant, the brightest kind";
  if (seconds >= 6) return "a bright beacon";
  if (seconds >= 4) return "a lively mid-sized star";
  return "a quick little twinkler";
}

// For fun: treat each second of the player's breath as a day and use Leavitt's rule for real pulsing stars
// (Cepheids: a star that pulses every P days shines like ~10^(...) Suns). 4 s -> ~1,500 Suns, 8 s -> ~2,900, 12 s -> ~4,200.
function sunsFor(seconds) {
  const absMag = -2.43 * (Math.log10(Math.max(1, seconds)) - 1) - 4.05;
  const suns = Math.pow(10, (4.83 - absMag) / 2.5);
  return Math.round(suns / 100) * 100;
}

function facts(s, a) {
  const mine = cycles(s.t, s.u), guide = cycles(s.t, s.g);
  const mean = (x) => x.reduce((p, c) => p + c, 0) / x.length;
  const f = { breaths: mine.count, breathSeconds: 0, guideSeconds: guide.gaps.length ? mean(guide.gaps) : 0, steadiness: "", starType: "" };
  if (mine.gaps.length >= 1) {
    f.breathSeconds = mean(mine.gaps);
    const spread = mine.gaps.length >= 2 ? Math.sqrt(mean(mine.gaps.map((g) => (g - f.breathSeconds) ** 2))) / f.breathSeconds : 0;
    // Only Easy has one rhythm; on Medium and Hard the pace is meant to change, so steadiness isn't a fact worth giving.
    if (s.difficulty === "Easy") f.steadiness = spread < 0.15 ? "very steady" : spread < 0.3 ? "fairly steady" : "changing pace";
    f.starType = starTypeFor(f.breathSeconds);
    f.suns = sunsFor(f.breathSeconds);
  }
  const seen = a.buckets.filter((b) => b.match >= 0);
  if (seen.length) {
    const best = seen.reduce((p, c) => (c.match > p.match ? c : p));
    f.bestStretch = `${best.start}-${best.start + BUCKET_SECONDS} s`;
  }
  return f;
}

// Used when Gemini isn't available, so the notebook is never empty.
function builtInNote(f) {
  if (!f.breathSeconds) return "Henrietta Leavitt timed how long stars took to brighten and dim. We couldn't time your star this round: keep your head in the box and try again.";
  const steady = f.steadiness === "very steady" ? " in a very steady rhythm" : f.steadiness === "fairly steady" ? " in a fairly steady rhythm" : "";
  const sec = Math.round(f.breathSeconds);
  return `Your star pulsed once every ${sec} seconds${steady}. ` +
         `Real pulsing stars take days, not seconds, and Henrietta Leavitt discovered that the slower they pulse, the brighter ` +
         `they truly are. A real star pulsing every ${sec} days would shine about ${f.suns.toLocaleString("en-US")} times brighter ` +
         `than our Sun: ${f.starType}.`;
}

// ---------- Gemini ----------

const SYSTEM = `You write a short note for the results screen of a game called "Breathe with the star".
The player breathed along with a pulsing guide star; a webcam turned their breathing into their own star that brightened
as they breathed in and dimmed as they breathed out.

The game honours astronomer Henrietta Leavitt. Background you can draw on (use one or two details per note, vary them):
- She was one of the Harvard "computers", women paid about 30 cents an hour around 1900-1912 to study glass photographic
  plates of the night sky by hand.
- Studying stars in the Small Magellanic Cloud, she found that pulsing stars (now called Cepheids) with slower pulses
  are truly brighter. Their periods range from about 1 day to over 50 days.
- Knowing how bright a star truly is, and how dim it looks from Earth, tells you how far away it is. That made these
  stars "standard candles", the first measuring tape for the universe.
- In 1924 Edwin Hubble used her rule on a Cepheid in Andromeda and proved it was a separate galaxy, so the universe is far
  bigger than the Milky Way. The Hubble Space Telescope still uses Cepheids to measure how fast the universe expands.
- Polaris, the North Star, is a Cepheid.

Write the note:
- 2 or 3 sentences, at most 70 words, for a curious general audience at a demo. Lively, with a sense of wonder; not dry.
- Start with one fact about THEIR star from the facts given (its pulse in seconds, how steady it was, or its best
  stretch). If you use the "Suns" figure, explain it in plain words: real pulsing stars take days, not seconds, so
  picture their star pulsing every N days instead of N seconds; by Leavitt's rule a real star like that shines about
  X times brighter than our Sun. Never just say "if each second were a day" without that explanation.
- Then one real piece of the science or history above, explained in everyday words. You may use ONE technical term
  (Cepheid or standard candle) if you explain it in the same sentence.
- If earlier games are listed, you may mention improvement in a few words.
- No breathing instructions or tips, and never comment on health. No greetings, emojis or markdown. Don't repeat the score.`;

function describe(s, a, f) {
  const hist = a.history?.length
    ? `Earlier games by this player, newest first: ${a.history.map((h) => `${h.score}% (${h.difficulty})`).join(", ")}.`
    : "This is the player's first recorded game.";
  const lines = [
    `Settings: ${s.difficulty}, ${Math.round(s.length)} seconds. Score ${s.score}% (don't repeat it).`,
    `Full breaths counted: ${f.breaths}.`,
  ];
  if (f.breathSeconds) {
    lines.push(`Their star took about ${f.breathSeconds.toFixed(1)} seconds to brighten and dim (the guide star averaged ${f.guideSeconds.toFixed(1)}).`);
    if (f.steadiness) lines.push(`Their rhythm was ${f.steadiness}.`);
    else lines.push("On this difficulty the guide changes pace on purpose, so don't comment on steadiness.");
    lines.push(`What-if: real pulsing stars take days, not seconds. By Leavitt's rule, a real star pulsing every ${Math.round(f.breathSeconds)} days ` +
               `shines about ${f.suns} times brighter than our Sun: ${f.starType}.`);
  } else {
    lines.push("We couldn't time their rhythm this round (too few clear breaths).");
  }
  if (f.bestStretch) lines.push(`Their best-matched stretch: ${f.bestStretch}.`);
  lines.push(hist, "Write the note.");
  return lines.join("\n");
}

async function askGemini(input) {
  if (!API_KEY) throw new Error("GEMINI_API_KEY is not set");
  const body = { model: MODEL, system_instruction: SYSTEM, input, generation_config: { max_output_tokens: 1024 } };
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
  if (!text) throw new Error("Gemini returned no text");
  return text;
}

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

// ---------- the endpoint ----------

// POST /api/shockwave/star/session -> { coach, source, stored, sessionId, buckets, history, yourBest, bestToday, ... }
async function handleSession(payload) {
  const s = clean(payload);
  if (s.t.length < 10) return { coach: "", error: "Not enough samples" };

  let analysis = null, stored = false, error = "";
  if (hasDb()) {
    try { analysis = await storeAndAnalyse(s); stored = true; }
    catch (err) { error = "Database: " + err.message; console.error("[star db]", err.message); }
  }
  if (!analysis) analysis = { sessionId: 0, buckets: bucketsLocal(s), history: [], yourBest: s.score, bestToday: s.score, bestForDifficulty: s.score };

  const f = facts(s, analysis);
  let coach = "", source = "";
  try {
    coach = await askGemini(describe(s, analysis, f));
    source = "Gemini";
  } catch (err) {
    error = (error ? error + "; " : "") + err.message;
    console.error("[star gemini]", err.message);
    coach = builtInNote(f);
    source = "built-in";
  }
  console.log(`[star] ${s.difficulty} ${s.score}% ~${f.breathSeconds.toFixed(1)}s ${f.steadiness} -> ${coach}`);
  if (stored) await saveCoach(analysis.sessionId, coach, f);
  return {
    coach, source, stored, ...analysis, error,
    breaths: f.breaths, suns: f.suns || 0, breathSeconds: Number(f.breathSeconds.toFixed(1)), steadiness: f.steadiness, starType: f.starType,
  };
}

// A made-up 30 s Medium game, for --star-selftest.
function sampleSession() {
  const t = [], u = [], g = [], inhale = [];
  const breaths = [[5, 5], [3, 3], [7, 7], [4, 4], [6, 6]];
  for (let i = 0; i < 300; i++) {
    const time = i / 10;
    let start = 0, level = 0, inh = true;
    for (const [a, b] of breaths) {
      if (time < start + a) { level = (time - start) / a; inh = true; break; }
      if (time < start + a + b) { level = 1 - (time - start - a) / b; inh = false; break; }
      start += a + b;
    }
    t.push(time); g.push(level); inhale.push(inh);
    const drift = time > 15 && time < 22 ? 0.45 : 0.08;                 // loses the rhythm in the middle
    u.push(time > 24 && time < 26 ? -1 : Math.max(0, Math.min(1, level + (Math.random() - 0.5) * drift * 2)));
  }
  return { player: "selftest", difficulty: "Medium", timerOn: true, length: 30, score: 71, inhaleMatch: 0.78, exhaleMatch: 0.61, tracked: 0.93, lag: 1.6, t, u, g, inhale };
}

module.exports = { handleSession, initDb, hasDb, getPool, sampleSession, dbStatus: () => dbError };
