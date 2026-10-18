# Build the Stellar website (Website/) and put it on the Vultr server at https://DOMAIN/stellar/
# Run from the project folder in PowerShell:
#   .\Server\deploy\deploy-website.ps1
# Safe to re-run any time you change the site or copy in new Unity game builds
# (Website/public/games/turbulent-trials, orbital-catch, star).
# The site lives under /stellar/ because "/" is the arm relay (phone controller + Unity websocket).
param(
  [string]$Server = "149.28.227.46",
  [string]$Domain = "149-28-227-46.sslip.io",
  [string]$User = "root",
  [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
$site = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "Website"
$target = "$User@$Server"

if (-not $SkipBuild) {
  Write-Host "==> Building the website (npm run build)"
  Push-Location $site
  if (-not (Test-Path node_modules)) { npm install; if ($LASTEXITCODE -ne 0) { Pop-Location; throw "npm install failed." } }
  npm run build
  if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Build failed - see the errors above." }
  Pop-Location
}
if (-not (Test-Path "$site/dist/index.html")) { throw "No Website/dist/index.html - the build didn't produce the site." }

Write-Host "==> Packing Website/dist"
$bundle = Join-Path $env:TEMP "stellar-site.tgz"
if (Test-Path $bundle) { Remove-Item $bundle }
tar -czf $bundle -C "$site/dist" .
if ($LASTEXITCODE -ne 0) { throw "Packing failed." }

Write-Host "==> Uploading to $Server (enter the root password when asked)"
scp $bundle "${target}:/tmp/stellar-site.tgz"
if ($LASTEXITCODE -ne 0) { throw "Upload failed - check the password and try again." }

Write-Host "==> Installing on the server (replaces the old copy, so renamed/removed folders disappear)"
$remote = @'
set -e
rm -rf /var/www/stellar.new
mkdir -p /var/www/stellar.new
tar -xzf /tmp/stellar-site.tgz -C /var/www/stellar.new
chmod -R a+rX /var/www/stellar.new
rm -rf /var/www/stellar.old
if [ -d /var/www/stellar ]; then mv /var/www/stellar /var/www/stellar.old; fi
mv /var/www/stellar.new /var/www/stellar
rm -rf /var/www/stellar.old /tmp/stellar-site.tgz
mkdir -p /etc/caddy/routes
cat > /etc/caddy/routes/stellar.caddy <<'CADDY'
redir /stellar /stellar/
handle_path /stellar/* {
    root * /var/www/stellar
    file_server
}
CADDY
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
systemctl reload caddy || systemctl restart caddy
'@
$remote = $remote -replace "`r", ""
$remote | ssh $target "bash -s"
if ($LASTEXITCODE -ne 0) { throw "Install on the server failed - see the errors above." }

Write-Host ""
Write-Host "Website: https://$Domain/stellar/"
Write-Host "Games:   https://$Domain/stellar/games/turbulent-trials/   https://$Domain/stellar/games/orbital-catch/"
