# Game API (wind tunnel coach + star game coach)

Used by two games. Originally built for the wind tunnel game ("Shockwave"), which is why the server-side names
and URLs still say `shockwave` (`/api/shockwave/...`). Changing them would mean redeploying and changing Unity.

**Wind tunnel:** gives the player a short, plain-language tip after each wind tunnel run, written by Gemini.
The game sends its recent runs here; this server adds the prompt, calls Gemini, and sends back the tip.
Your Gemini key stays in this folder and never goes into the game build.

## Run it

1. Get a key at https://aistudio.google.com/app/apikey
2. Copy `.env.example` to `.env` and paste the key after `GEMINI_API_KEY=`
3. In this folder:

   ```
   node server.js --selftest     # one sample request; prints Gemini's tip
   node server.js                # starts the server on http://localhost:3000
   ```

Needs Node 18 or newer (`node -v`). Run `npm install` once in this folder (installs `pg`, used by the star game).

Leave it running while you play. Every tip is also printed in this window.

## On Vultr (always on, no terminal needed)

This server runs on the same Vultr box as the arm relay, as the `shockwave` service:
`https://149-28-227-46.sslip.io/api/shockwave/feedback`. To push changes (including a new `.env`), run from the
project folder in PowerShell:

```
.\Server\deploy\deploy-game-api.ps1
```

Check it: https://149-28-227-46.sslip.io/api/shockwave/health. Details are in `Server/README.md`.

## In Unity

- The Test Bench adds a **Feedback Coach** automatically. Its default endpoint is the Vultr one above.
- For local testing, set the endpoint to `http://localhost:3000/api/shockwave/feedback`, run `node server.js`, and set
  **Edit → Project Settings → Player → Other Settings → Allow downloads over HTTP** to **Always allowed**.
- If the server is off or slow, the game shows a built-in tip instead, labelled "Built-in tip".

## Changing the tone

Edit `SYSTEM` near the top of `server.js` and restart the server. No Unity rebuild needed.

## Star game: Tiger Data + Gemini coach (star.js)

After each "Breathe with the star" game, Unity posts the game to `POST /api/shockwave/star/session`:
settings, score, and the breathing samples (10 per second: the player's breathing and the guide star, lined up).

- **Tiger Data** stores every game: `star_sessions` (one row per game) and `star_samples` (a hypertable, one row per
  sample). `time_bucket()` turns the samples into a 5-second timeline, and the player's earlier games give history
  and bests. Tables are created automatically the first time.
- The server works out simple facts about the player's rhythm (seconds per breath, how steady, best stretch) and,
  using Leavitt's idea that slower-pulsing stars are brighter, what kind of star theirs would be. These are saved too.
- **Gemini** turns those facts into a 2-3 sentence "Leavitt's notebook" note in plain words (no breathing advice).
  If Gemini is unavailable a built-in note is used.
- The reply goes back to the game's results screen (notebook note, bests, timeline bars).

Setup: put `TIGER_DATABASE_URL=...` (your Tiger Cloud connection string) in `.env`, then

```
npm install
node server.js --star-selftest     # stores a made-up game in Tiger Data and prints Gemini's tip
```

Without `TIGER_DATABASE_URL` the endpoint still works (timeline worked out in JavaScript, nothing saved).
Look at the data in the Tiger Cloud console's SQL editor, e.g.
`SELECT * FROM star_sessions ORDER BY started_at DESC;`

## Leaderboards (all three games, leaderboard.js)

Each game has its own board, stored in Tiger Data (`game_scores` hypertable, one row per finished game; created
automatically). A player's best score per name counts.

| Game id | Score | Better |
|---|---|---|
| `star` | sync % | higher |
| `windtunnel` | best design's smooth flow % | higher |
| `satellite` | total catch time (s) | lower |

- `POST /api/shockwave/scores` `{ game, name, playerId, score, detail }` -> `{ ok, rank, players, newBest, top }`
- `GET /api/shockwave/leaderboard?game=star&limit=10` -> `{ players, top }`

Unity side: `Assets/Shared/Leaderboard.cs`, drawn on each game's results screen (type a name, Enter or "Add").
Names are trimmed to 16 characters and a short word list blocks rude ones.
Look at the data: `SELECT game, player_name, score, time FROM game_scores ORDER BY time DESC;`
