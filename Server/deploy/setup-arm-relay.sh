#!/usr/bin/env bash
# One-time setup on a fresh Vultr Ubuntu server. Run as root:
#   bash /opt/arm-relay/deploy/setup-arm-relay.sh yourdomain.com
# Installs Node + Caddy, runs server.js as a service, and gets an HTTPS cert.
set -euo pipefail

DOMAIN="${1:-}"
if [ -z "$DOMAIN" ]; then
  echo "Usage: bash setup-arm-relay.sh <domain>   e.g. bash setup-arm-relay.sh arm.example.com"
  exit 1
fi
APP_DIR=/opt/arm-relay

echo "==> Installing Node.js 20"
if ! command -v node >/dev/null || [ "$(node -v | cut -d. -f1 | tr -d v)" -lt 18 ]; then
  apt-get update -y
  apt-get install -y ca-certificates curl gnupg
  curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
  apt-get install -y nodejs
fi

echo "==> Installing Caddy (automatic HTTPS)"
if ! command -v caddy >/dev/null; then
  apt-get install -y debian-keyring debian-archive-keyring apt-transport-https curl
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' > /etc/apt/sources.list.d/caddy-stable.list
  apt-get update -y
  apt-get install -y caddy
fi

echo "==> Installing app dependencies"
cd "$APP_DIR"
npm install --omit=dev

echo "==> Creating the arm-relay service"
id -u armrelay >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin armrelay
chown -R armrelay:armrelay "$APP_DIR"
cat > /etc/systemd/system/arm-relay.service <<UNIT
[Unit]
Description=Robotic arm phone relay (server.js)
After=network.target

[Service]
User=armrelay
WorkingDirectory=$APP_DIR
Environment=PORT=8080
Environment=HOST=127.0.0.1
ExecStart=/usr/bin/node $APP_DIR/server.js
Restart=always
RestartSec=2

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now arm-relay
systemctl restart arm-relay

echo "==> Configuring Caddy for $DOMAIN"
mkdir -p /etc/caddy/routes
cat > /etc/caddy/Caddyfile <<CADDY
$DOMAIN {
    # Other games add their routes here (e.g. Shockwave -> /api/shockwave/*)
    import /etc/caddy/routes/*.caddy
    reverse_proxy 127.0.0.1:8080
}
CADDY
systemctl enable caddy
systemctl reload caddy || systemctl restart caddy

echo "==> Opening firewall ports 22, 80, 443"
if command -v ufw >/dev/null; then
  ufw allow 22/tcp >/dev/null
  ufw allow 80/tcp >/dev/null
  ufw allow 443/tcp >/dev/null
  ufw --force enable >/dev/null
fi

echo
echo "Done."
echo "  Phone:  https://$DOMAIN/controller"
echo "  Unity:  wss://$DOMAIN/?role=game"
echo "  Check:  https://$DOMAIN/health"
echo "  Logs:   journalctl -u arm-relay -f"
