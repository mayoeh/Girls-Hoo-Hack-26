# Servers

Everything that runs outside Unity lives here. Both servers run on the same Vultr box
(149.28.227.46, https://149-28-227-46.sslip.io) as always-on services, so nothing has to run on a laptop.

| Folder | What it does | Used by | On Vultr |
|---|---|---|---|
| `arm-relay/` | Serves the phone controller page and relays phone tilt to Unity over a websocket | Satellite Catch | `/opt/arm-relay`, service `arm-relay`, port 8080 |
| `game-api/` | Gemini coach tips + Tiger Data storage | Wind tunnel (Shockwave), star game | `/opt/shockwave`, service `shockwave`, port 3000 |
| `deploy/` | PowerShell scripts that push each server to Vultr | | |

Caddy (HTTPS) sends `https://DOMAIN/api/shockwave/*` to the game API and everything else to the arm relay.

Why "shockwave"? The game API was first written for the wind tunnel game, called "Shockwave". The star game
endpoints were added to it later to reuse its Gemini key and hosting. The folder is now `game-api`, but the server-side
names and URLs (`/api/shockwave/...`) are unchanged so the Unity builds keep working.

## Run locally

    cd Server\arm-relay
    npm install          # first time only
    node server.js       # http://localhost:8080/controller

    cd Server\game-api
    npm install          # first time only
    node server.js       # http://localhost:3000

Secrets for the game API go in `Server/game-api/.env` (copy `.env.example`). It's ignored by git.

## Deploy to Vultr (PowerShell, from the project folder)

    .\Server\deploy\deploy-arm-relay.ps1     # after changing arm-relay
    .\Server\deploy\deploy-game-api.ps1      # after changing game-api or its .env
    .\Server\deploy\deploy-website.ps1       # after changing the website or copying in new game builds
                                             # -> https://149-28-227-46.sslip.io/stellar/

Enter the root password when asked. If PowerShell blocks the script:
`powershell -ExecutionPolicy Bypass -File .\Server\deploy\deploy-game-api.ps1`

Both default to the current Vultr server. For a different one, pass `-Server <ip> -Domain <domain>`
(no domain? use `<ip-with-dashes>.sslip.io`).

The first-ever deploy to a fresh Ubuntu server must be `deploy-arm-relay.ps1`: it installs Node and Caddy.

## Unity settings

- Satellite Catch (ArmTest, Server Url): `wss://149-28-227-46.sslip.io/?role=game`, or `ws://localhost:8080/?role=game` locally.
- Wind tunnel (FeedbackCoach endpoint): `https://149-28-227-46.sslip.io/api/shockwave/feedback`, or `http://localhost:3000/api/shockwave/feedback` locally.
- Star game (Session Url): `https://149-28-227-46.sslip.io/api/shockwave/star/session`.

## Check / debug

- https://149-28-227-46.sslip.io/health shows the phones and games connected to the arm relay.
- https://149-28-227-46.sslip.io/api/shockwave/health shows `hasKey` (Gemini) and `hasDb` (Tiger Data).
- Logs: `ssh root@149.28.227.46 journalctl -u arm-relay -f` or `... journalctl -u shockwave -f`
- Restart: `ssh root@149.28.227.46 systemctl restart arm-relay` (or `shockwave`)
