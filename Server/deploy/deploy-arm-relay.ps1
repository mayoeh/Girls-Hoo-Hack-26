# Push the arm relay (Server/arm-relay) to the Vultr server and (re)start it.
# Run from the project folder in PowerShell:
#   .\Server\deploy\deploy-arm-relay.ps1
# Safe to re-run any time you change Server/arm-relay/server.js or controller.html.
param(
  [string]$Server = "149.28.227.46",
  [string]$Domain = "149-28-227-46.sslip.io",
  [string]$User = "root"
)
$ErrorActionPreference = "Stop"
$deployDir = $PSScriptRoot
Set-Location (Join-Path (Split-Path -Parent $PSScriptRoot) "arm-relay")
$target = "$User@$Server"

Write-Host "==> Creating /opt/arm-relay on $Server"
ssh $target "mkdir -p /opt/arm-relay/deploy"
if ($LASTEXITCODE -ne 0) { throw "Could not connect to $Server - check the IP and password." }

Write-Host "==> Uploading files"
scp server.js controller.html AstroSpace.ttf stars.jpg package.json package-lock.json "${target}:/opt/arm-relay/"
if ($LASTEXITCODE -ne 0) { throw "Upload of app files failed - check the password and try again." }
scp "$deployDir/setup-arm-relay.sh" "${target}:/opt/arm-relay/deploy/"
if ($LASTEXITCODE -ne 0) { throw "Upload of setup-arm-relay.sh failed - check the password and try again." }

Write-Host "==> Running setup on the server"
ssh $target "sed -i 's/\r$//' /opt/arm-relay/deploy/setup-arm-relay.sh && bash /opt/arm-relay/deploy/setup-arm-relay.sh $Domain"

Write-Host ""
Write-Host "Phone controller: https://$Domain/controller"
Write-Host "Unity serverUrl:  wss://$Domain/?role=game"
