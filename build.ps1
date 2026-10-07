# Builds and publishes WinTabs as a self-contained x64 app into .\dist\WinTabs
# Usage (Windows PowerShell):  .\build.ps1 [-Configuration Release] [-Run]
param([string]$Configuration = "Release", [switch]$Run)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "src\WinTabs\WinTabs.csproj"
$out = Join-Path $root "dist\WinTabs"

$dotnet = "dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $local = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (Test-Path $local) { $dotnet = $local } else { throw ".NET 8 SDK not found. Install from https://dot.net or run: winget install Microsoft.DotNet.SDK.8" }
}

& $dotnet publish $proj -c $Configuration -r win-x64 -p:Platform=x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Host "`nPublished to $out\WinTabs.exe"
if ($Run) { Start-Process (Join-Path $out "WinTabs.exe") }
