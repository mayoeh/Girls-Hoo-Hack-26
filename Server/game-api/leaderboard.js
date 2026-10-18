// Leaderboards for all three games, stored in Tiger Data (one hypertable, one row per finished game).
//
//   POST /api/shockwave/scores       { game, name, playerId, score, detail }  -> { ok, rank, players, top, ... }
//   GET  /api/shockwave/leaderboard?game=star&limit=10                         -> { game, players, top }
//
// Each game has its own board. A player's best score counts (one line per name), ties go to whoever got there first.
// Without TIGER_DATABASE_URL the boards live in memory until the server restarts (fine for local testing).

const star = require("./star");   // shares its Tiger Data connection

// lowerIsBetter: the satellite game is a race (total seconds).
const GAMES = {
  star:       { title: "Breathe with the star", lowerIsBetter: false, min: 0, max: 100 },
  windtunnel: { title: "Shockwave",             lowerIsBetter: false, min: 0, max: 100 },
  satellite:  { title: "Satellite Catch",       lowerIsBetter: true,  min: 0, max: 3600 },
};

// A short, demo-safe word list. Names containing these are refused (the game asks for another name).
const BLOCKED = ["fuck", "shit", "bitch", "cunt", "dick", "cock", "pussy", "nigg", "fag", "slut", "whore", "rape", "nazi", "hitler", "porn"];

let ready = null;
async function initBoards() {
  const p = star.getPool();
  if (!p) return false;
  if (!ready) {
    ready = (async () => {
      await p.query(`CREATE TABLE IF NOT EXISTS game_scores (
          time        TIMESTAMPTZ NOT NULL DEFAULT now(),
          game        TEXT        NOT NULL,
          player_name TEXT        NOT NULL,
          player_id   TEXT,
          score       REAL        NOT NULL,
          detail      JSONB)`);
      try {
        await p.query(`SELECT create_hypertable('game_scores', by_range('time'), if_not_exists => TRUE)`);
      } catch {
        await p.query(`SELECT create_hypertable('game_scores', 'time', if_not_exists => TRUE)`);
      }
      await p.query(`CREATE INDEX IF NOT EXISTS game_scores_game_score ON game_scores (game, score)`);
      return true;
    })().catch((err) => { ready = null; throw err; });
  }
  return ready;
}

function cleanName(raw) {
  const name = String(raw ?? "").normalize("NFKC").replace(/[^\p{L}\p{N} ._'-]/gu, "").replace(/\s+/g, " ").trim().slice(0, 16);
  if (!name) return { error: "Type a name first." };
  const squashed = name.toLowerCase().replace(/0/g, "o").replace(/[1!|]/g, "i").replace(/3/g, "e").replace(/4/g, "a").replace(/5/g, "s").replace(/[^a-z]/g, "");
  if (BLOCKED.some((w) => squashed.includes(w))) return { error: "Please pick a different name." };
  return { name };
}

// ---------- in-memory fallback ----------
const memory = [];

function bestPerPlayer(rows, cfg) {
  const better = (a, b) => (cfg.lowerIsBetter ? a.score < b.score : a.score > b.score) || (a.score === b.score && a.time < b.time);
  const best = new Map();
  for (const r of rows) {
    const k = r.player_name.toLowerCase();
    if (!best.has(k) || better(r, best.get(k))) best.set(k, r);
  }
  return [...best.values()].sort((a, b) => (better(a, b) ? -1 : better(b, a) ? 1 : 0));
}

// ---------- queries ----------

async function board(game, limit) {
  const cfg = GAMES[game];
  const p = star.getPool();
  if (!p) {
    const list = bestPerPlayer(memory.filter((r) => r.game === game), cfg);
    return { players: list.length, top: list.slice(0, limit).map(row), stored: false };
  }
  await initBoards();
  const dir = cfg.lowerIsBetter ? "ASC" : "DESC";
  const { rows } = await p.query(
    `WITH best AS (
       SELECT DISTINCT ON (lower(player_name)) player_name, score, time
       FROM game_scores WHERE game = $1
       ORDER BY lower(player_name), score ${dir}, time ASC)
     SELECT player_name, score, time, (SELECT count(*) FROM best)::int AS players
     FROM best ORDER BY score ${dir}, time ASC LIMIT $2`, [game, limit]);
  return { players: rows[0]?.players ?? 0, top: rows.map(row), stored: true };
}

function row(r) {
  return { name: r.player_name, score: Number(r.score), when: new Date(r.time).toISOString() };
}

// Where this name's best score sits on the board (1 = top).
function rankOf(name, list) {
  const i = list.findIndex((r) => r.name.toLowerCase() === name.toLowerCase());
  return i < 0 ? 0 : i + 1;
}

async function submit(payload) {
  const game = String(payload?.game || "");
  const cfg = GAMES[game];
  if (!cfg) return { ok: false, error: "Unknown game" };
  const n = cleanName(payload?.name);
  if (n.error) return { ok: false, error: n.error };
  const score = Number(payload?.score);
  if (!Number.isFinite(score) || score < cfg.min || score > cfg.max) return { ok: false, error: "Score out of range" };
  const detail = payload?.detail && typeof payload.detail === "object" ? payload.detail : null;
  const playerId = String(payload?.playerId || "").slice(0, 40) || null;

  const p = star.getPool();
  if (p) {
    await initBoards();
    await p.query(`INSERT INTO game_scores (game, player_name, player_id, score, detail) VALUES ($1,$2,$3,$4,$5)`,
      [game, n.name, playerId, score, detail ? JSON.stringify(detail).slice(0, 4000) : null]);
  } else {
    memory.push({ game, player_name: n.name, player_id: playerId, score, detail, time: new Date() });
  }

  // Rank: compare against every player's best (needs the full list, which is small for a demo).
  const all = await board(game, 10000);
  const rank = rankOf(n.name, all.top);
  const mine = all.top[rank - 1];
  console.log(`[board] ${game}: ${n.name} ${score} -> #${rank} of ${all.players}`);
  return {
    ok: true, game, name: n.name, rank, players: all.players,
    personalBest: mine ? mine.score : score,
    newBest: mine ? mine.score === score : true,
    top: all.top.slice(0, Math.max(1, Math.min(25, Number(payload?.limit) || 10))),
    stored: all.stored,
  };
}

async function leaderboard(game, limit) {
  if (!GAMES[game]) return { error: "Unknown game" };
  const b = await board(game, Math.max(1, Math.min(25, Number(limit) || 10)));
  return { game, title: GAMES[game].title, lowerIsBetter: GAMES[game].lowerIsBetter, ...b };
}

module.exports = { submit, leaderboard, initBoards, GAMES };
