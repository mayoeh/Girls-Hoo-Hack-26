#!/usr/bin/env bash
# Runs on the Vultr server (the same one as the arm relay). Called by deploy-game-api.ps1:
#   bash /opt/shockwave/deploy/setup-game-api.sh yourdomain.com
# Runs the game API (Server/game-api: Gemini feedback + Tiger Data) as an always-on service on port 3000.
# Server-side names stay "shockwave" (/opt/shockwave, service shockwave, /etc/shockwave.env, /api/shockwave/*)
# so existing Unity URLs keep working.
# Routes https://DOMAIN/api/shockwave/* to it. The arm relay keeps everything else.
set -euo pipefail

DOMAIN="${1:-}"
if [ -z "$DOMAIN" ]; then
  echo "Usage: bash setup-game-api.sh <domain>"
  exit 1
fi
APP_DIR=/opt/shockwave
SERVICE=shockwave
PORT=3000

command -v node >/dev/null || { echo "Node isn't installed. Run the arm relay deploy (deploy-arm-relay.ps1) first."; exit 1; }
command -v caddy >/dev/null || { echo "Caddy isn't installed. Run the arm relay deploy (deploy-arm-relay.ps1) first."; exit 1; }

echo "==> Installing Shockwave dependencies"
cd "$APP_DIR"
npm install --omit=dev

# Secrets (GEMINI_API_KEY, Tiger Data connection string, ...) live outside the app folder.
if [ -f "$APP_DIR/.env.upload" ]; then
  mv "$APP_DIR/.env.upload" /etc/shockwave.env
  sed -i 's/\r$//' /etc/shockwave.env
fi
touch /etc/shockwave.env
chmod 600 /etc/shockwave.env

echo "==> Creating the $SERVICE service"
id -u shockwave >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin shockwave
chown -R shockwave:shockwave "$APP_DIR"
chown shockwave:shockwave /etc/shockwave.env
cat > /etc/systemd/system/$SERVICE.service <<UNIT
[Unit]
Description=Shockwave feedback + Tiger Data server (server.js)
After=network.target

[Service]
User=shockwave
WorkingDirectory=$APP_DIR
EnvironmentFile=/etc/shockwave.env
Environment=PORT=$PORT
Environment=HOST=127.0.0.1
ExecStart=/usr/bin/node $APP_DIR/server.js
Restart=always
RestartSec=2

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now $SERVICE
systemctl restart $SERVICE

echo "==> Routing https://$DOMAIN/api/shockwave/* to Shockwave"
mkdir -p /etc/caddy/routes
cat > /etc/caddy/routes/shockwave.caddy <<CADDY
reverse_proxy /api/shockwave/* 127.0.0.1:$PORT
CADDY
# Older arm setups wrote a Caddyfile without the routes import: add it (keeps the arm relay as the default).
if ! grep -q "import /etc/caddy/routes" /etc/caddy/Caddyfile; then
  cp /etc/caddy/Caddyfile /etc/caddy/Caddyfile.bak
  cat > /etc/caddy/Caddyfile <<CADDY
$DOMAIN {
    # Other games add their routes here (e.g. Shockwave -> /api/shockwave/*)
    import /etc/caddy/routes/*.caddy
    reverse_proxy 127.0.0.1:8080
}
CADDY
fi
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
systemctl reload caddy || systemctl restart caddy

sleep 2
echo
systemctl --no-pager --lines=0 status $SERVICE | head -3 || true
echo
echo "Done."
echo "  Unity endpoint: https://$DOMAIN/api/shockwave/feedback"
echo "  Logs:           journalctl -u $SERVICE -f"
echo "  Arm relay:      https://$DOMAIN/health (unchanged)"
