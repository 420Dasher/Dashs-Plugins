param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root "AethernetAssist.csproj"

Write-Host "== AethernetAssist v3.0.12 DEV10 FIX2 CALLBACK AND WINDOW ==" -ForegroundColor Cyan

dotnet restore $Project
if ($LASTEXITCODE -ne 0) {
    throw "Restore failed."
}

dotnet build $Project -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

Write-Host "BUILD SUCCESS" -ForegroundColor Green