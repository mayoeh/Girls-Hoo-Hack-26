# Hosting the arm relay on Vultr

Phone -> https://YOUR-DOMAIN/controller -> server.js (Vultr, behind Caddy HTTPS) -> Unity (wss://YOUR-DOMAIN/?role=game)

## 1. Create the server (vultr.com)
- Deploy New Server -> Cloud Compute (Shared CPU) -> Regular/Intel, cheapest plan with an IPv4 address.
- Location: New York (NJ) or Atlanta.
- Image: Ubuntu 24.04 LTS.
- Copy the server's IPv4 address and root password from its Overview page.

## 2. Point a domain at it
- Real domain: in your registrar's DNS, add an A record (e.g. host `arm` or `@`) -> the server IP. Wait a few minutes.
- No domain? Use the free hostname `<ip-with-dashes>.sslip.io`, e.g. 45.76.12.34 -> `45-76-12-34.sslip.io`. No DNS setup needed.

## 3. Deploy (PowerShell, in the project folder)
    .\deploy\deploy.ps1 -Server 45.76.12.34 -Domain arm.example.com
Type `yes` to trust the server the first time, then the root password when asked (a few times).
If PowerShell blocks the script: `powershell -ExecutionPolicy Bypass -File .\deploy\deploy.ps1 -Server ... -Domain ...`

Re-run the same command whenever you change server.js or controller.html.

## 4. Point Unity at it
Select the object with ArmTest in the RobotArmGame scene and set **Server Url** in the Inspector to:
    wss://arm.example.com/?role=game
(The Inspector value wins over the default in code.) Switch back to `ws://localhost:8080/?role=game` for local testing.

## Check / debug
- https://YOUR-DOMAIN/health -> shows how many phones and games are connected.
- Logs: `ssh root@IP journalctl -u arm-relay -f`
- Restart: `ssh root@IP systemctl restart arm-relay`

---

# Hosting the Shockwave server on the same Vultr box

Unity (FeedbackCoach) -> https://149-28-227-46.sslip.io/api/shockwave/feedback -> Caddy -> Shockwave server.js (port 3000, always on)

The arm relay keeps everything else on the domain (/controller, /health, the websocket). Both run as services, so they start on boot and restart if they crash. You never need to run `node server.js` on your laptop.

## Before the first deploy
- The server lives in `Server/shockwave-feedback` (server.js + package.json, no npm packages needed).
- Its secrets (Gemini key now, Tiger Data connection string later) go in `Server/shockwave-feedback/.env`. The deploy copies it to the server as `/etc/shockwave.env`. It's ignored by git.

## Deploy (PowerShell, in the project folder)
    .\deploy\deploy-shockwave.ps1
Enter the root password when asked. Re-run it whenever you change the Shockwave server.

## Unity
FeedbackCoach already points at `https://149-28-227-46.sslip.io/api/shockwave/feedback`. For local testing, change its Endpoint to `http://localhost:3000/api/shockwave/feedback`.

## Check / debug
- https://149-28-227-46.sslip.io/api/shockwave/health -> `hasKey: true` means the Gemini key made it.
- Logs (every tip is printed): `ssh root@149.28.227.46 journalctl -u shockwave -f`
- Restart: `ssh root@149.28.227.46 systemctl restart shockwave`
