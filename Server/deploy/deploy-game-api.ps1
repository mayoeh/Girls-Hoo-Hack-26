# Push the game API (Server/game-api: wind tunnel Gemini feedback + star game Tiger Data/Gemini)
# to the same Vultr server as the arm relay.
# Run from the project folder in PowerShell:
#   .\Server\deploy\deploy-game-api.ps1
# Safe to re-run any time you change the game API.
# Secrets: put them in Server/game-api/.env (it's uploaded to the server, never to git).
# On the server it still lives at /opt/shockwave (service "shockwave", URLs /api/shockwave/...),
# so the Unity URLs don't change.
param(
  [string]$Server = "149.28.227.46",
  [string]$Domain = "149-28-227-46.sslip.io",
  [string]$ServerDir = "game-api",
  [string]$User = "root"
)
$ErrorActionPreference = "Stop"
$deployDir = $PSScriptRoot
Set-Location (Split-Path -Parent $PSScriptRoot)   # the Server folder
$target = "$User@$Server"

if (-not (Test-Path "$ServerDir/server.js")) { throw "Can't find $ServerDir/server.js. Pass -ServerDir <folder with the game API server.js>." }
if (-not (Test-Path "$ServerDir/package.json")) { throw "$ServerDir has no package.json. Run 'npm init -y' and 'npm install' for its packages there first." }

Write-Host "==> Packing $ServerDir (without node_modules)"
$bundle = Join-Path $env:TEMP "shockwave.tgz"
if (Test-Path $bundle) { Remove-Item $bundle }
tar -czf $bundle --exclude=node_modules --exclude=.env --exclude=.git -C $ServerDir .
if ($LASTEXITCODE -ne 0) { throw "Packing failed." }

Write-Host "==> Uploading to $Server (enter the root password when asked)"
ssh $target "mkdir -p /opt/shockwave/deploy"
if ($LASTEXITCODE -ne 0) { throw "Could not connect to $Server - check the IP and password." }
scp $bundle "$deployDir/setup-game-api.sh" "${target}:/tmp/"
if ($LASTEXITCODE -ne 0) { throw "Upload failed - check the password and try again." }
if (Test-Path "$ServerDir/.env") {
  scp "$ServerDir/.env" "${target}:/opt/shockwave/.env.upload"
  if ($LASTEXITCODE -ne 0) { throw "Upload of .env failed." }
} else {
  Write-Host "    (no $ServerDir/.env found - keeping the secrets already on the server)"
}

Write-Host "==> Installing and (re)starting the service"
ssh $target "tar -xzf /tmp/shockwave.tgz -C /opt/shockwave && mv /tmp/setup-game-api.sh /opt/shockwave/deploy/ && sed -i 's/\r$//' /opt/shockwave/deploy/setup-game-api.sh && bash /opt/shockwave/deploy/setup-game-api.sh $Domain"
if ($LASTEXITCODE -ne 0) { throw "Setup on the server failed - see the messages above." }

Write-Host ""
Write-Host "Unity FeedbackCoach endpoint: https://$Domain/api/shockwave/feedback"
Write-Host "Health check: https://$Domain/api/shockwave/health"
Write-Host "Logs: ssh $target journalctl -u shockwave -f"
