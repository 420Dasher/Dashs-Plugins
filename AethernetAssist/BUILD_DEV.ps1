param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root "AethernetAssist.csproj"

Write-Host "== AethernetAssist v0.3.9 DEV9 TEST5 FIX1 ENDPOINT SEAM ==" -ForegroundColor Cyan

dotnet restore $Project
if ($LASTEXITCODE -ne 0) {
    throw "Restore failed."
}

dotnet build $Project -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

Write-Host "BUILD SUCCESS" -ForegroundColor Green