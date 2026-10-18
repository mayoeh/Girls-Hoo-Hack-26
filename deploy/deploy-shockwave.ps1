# Push the Shockwave server (Gemini feedback + Tiger Data) to the same Vultr server as the arm relay.
# Run from the project folder in PowerShell:
#   .\deploy\deploy-shockwave.ps1
# Safe to re-run any time you change the Shockwave server.
# Secrets: put them in a .env file next to the Shockwave server.js (it's uploaded to the server, never to git).
param(
  [string]$Server = "149.28.227.46",
  [string]$Domain = "149-28-227-46.sslip.io",
  [string]$ServerDir = "Server/shockwave-feedback",
  [string]$User = "root"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$target = "$User@$Server"

if (-not (Test-Path "$ServerDir/server.js")) { throw "Can't find $ServerDir/server.js. Pass -ServerDir <folder with the Shockwave server.js>." }
if (-not (Test-Path "$ServerDir/package.json")) { throw "$ServerDir has no package.json. Run 'npm init -y' and 'npm install' for its packages there first." }

Write-Host "==> Packing $ServerDir (without node_modules)"
$bundle = Join-Path $env:TEMP "shockwave.tgz"
if (Test-Path $bundle) { Remove-Item $bundle }
tar -czf $bundle --exclude=node_modules --exclude=.env --exclude=.git -C $ServerDir .
if ($LASTEXITCODE -ne 0) { throw "Packing failed." }

Write-Host "==> Uploading to $Server (enter the root password when asked)"
ssh $target "mkdir -p /opt/shockwave/deploy"
if ($LASTEXITCODE -ne 0) { throw "Could not connect to $Server - check the IP and password." }
scp $bundle deploy/setup-shockwave.sh "${target}:/tmp/"
if ($LASTEXITCODE -ne 0) { throw "Upload failed - check the password and try again." }
if (Test-Path "$ServerDir/.env") {
  scp "$ServerDir/.env" "${target}:/opt/shockwave/.env.upload"
  if ($LASTEXITCODE -ne 0) { throw "Upload of .env failed." }
} else {
  Write-Host "    (no $ServerDir/.env found - keeping the secrets already on the server)"
}

Write-Host "==> Installing and (re)starting the service"
ssh $target "tar -xzf /tmp/shockwave.tgz -C /opt/shockwave && mv /tmp/setup-shockwave.sh /opt/shockwave/deploy/ && sed -i 's/\r$//' /opt/shockwave/deploy/setup-shockwave.sh && bash /opt/shockwave/deploy/setup-shockwave.sh $Domain"
if ($LASTEXITCODE -ne 0) { throw "Setup on the server failed - see the messages above." }

Write-Host ""
Write-Host "Unity FeedbackCoach endpoint: https://$Domain/api/shockwave/feedback"
Write-Host "Health check: https://$Domain/api/shockwave/health"
Write-Host "Logs: ssh $target journalctl -u shockwave -f"
