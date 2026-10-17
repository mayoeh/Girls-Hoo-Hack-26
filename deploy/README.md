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
