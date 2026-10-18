# Shockwave feedback server

Gives the player a short, plain-language tip after each wind tunnel run, written by Gemini.
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

Needs Node 18 or newer (`node -v`). No `npm install`.

Leave it running while you play. Every tip is also printed in this window.

## On Vultr (always on, no terminal needed)

This server runs on the same Vultr box as the arm relay, as the `shockwave` service:
`https://149-28-227-46.sslip.io/api/shockwave/feedback`. To push changes (including a new `.env`), run from the
project folder in PowerShell:

```
.\deploy\deploy-shockwave.ps1
```

Check it: https://149-28-227-46.sslip.io/api/shockwave/health. Details are in `deploy/README.md`.

## In Unity

- The Test Bench adds a **Feedback Coach** automatically. Its default endpoint is the Vultr one above.
- For local testing, set the endpoint to `http://localhost:3000/api/shockwave/feedback`, run `node server.js`, and set
  **Edit → Project Settings → Player → Other Settings → Allow downloads over HTTP** to **Always allowed**.
- If the server is off or slow, the game shows a built-in tip instead, labelled "Built-in tip".

## Changing the tone

Edit `SYSTEM` near the top of `server.js` and restart the server. No Unity rebuild needed.
