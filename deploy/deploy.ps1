# Push the relay to your Vultr server and (re)start it.
# Run from the project folder in PowerShell:
#   .\deploy\deploy.ps1 -Server 45.76.12.34 -Domain arm.example.com
# Safe to re-run any time you change server.js or controller.html.
param(
  [Parameter(Mandatory = $true)][string]$Server,
  [Parameter(Mandatory = $true)][string]$Domain,
  [string]$User = "root"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$target = "$User@$Server"

Write-Host "==> Creating /opt/arm-relay on $Server"
ssh $target "mkdir -p /opt/arm-relay/deploy"
if ($LASTEXITCODE -ne 0) { throw "Could not connect to $Server - check the IP and password." }

Write-Host "==> Uploading files"
scp server.js controller.html package.json package-lock.json "${target}:/opt/arm-relay/"
if ($LASTEXITCODE -ne 0) { throw "Upload of app files failed - check the password and try again." }
scp deploy/setup.sh "${target}:/opt/arm-relay/deploy/"
if ($LASTEXITCODE -ne 0) { throw "Upload of setup.sh failed - check the password and try again." }

Write-Host "==> Running setup on the server"
ssh $target "sed -i 's/\r$//' /opt/arm-relay/deploy/setup.sh && bash /opt/arm-relay/deploy/setup.sh $Domain"

Write-Host ""
Write-Host "Phone controller: https://$Domain/controller"
Write-Host "Unity serverUrl:  wss://$Domain/?role=game"
