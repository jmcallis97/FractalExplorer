<#
.SYNOPSIS
  Builds the one-click installer: artifacts\FractalExplorer-Setup-<version>.exe

.DESCRIPTION
  1. Publishes the app self-contained for 64-bit Windows (no .NET install needed).
  2. Copies your places history into it as StarterPlaces: a new user's gallery starts with
     these places (the app copies them on first run; an existing history is never touched).
  3. Compiles installer\Fractal.iss with Inno Setup 6 (winget install JRSoftware.InnoSetup).

  The version comes from <Version> in Fractal\Fractal.csproj.

.PARAMETER Places
  The places folder to ship. Default: your own, %LocalAppData%\Fractal\Places.
#>
param([string]$Places = (Join-Path $env:LOCALAPPDATA 'Fractal\Places'))

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$root\Fractal\Fractal.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$publish = Join-Path $root 'artifacts\publish'

Write-Host "Fractal Explorer $version"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish "$root\Fractal\Fractal.csproj" -c Release -r win-x64 --self-contained true -o $publish -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE) { throw "dotnet publish failed" }

# Starter places: the list plus the thumbnails it refers to (not stray files).
$list = Join-Path $Places 'places.json'
if (Test-Path $list) {
    $starter = Join-Path $publish 'StarterPlaces'
    New-Item -ItemType Directory -Force $starter | Out-Null
    $entries = @(Get-Content $list -Raw | ConvertFrom-Json)
    Copy-Item $list $starter
    $missing = 0
    foreach ($p in $entries) {
        $png = Join-Path $Places "$($p.Id).png"
        if (Test-Path $png) { Copy-Item $png $starter } else { $missing++ }
    }
    Write-Host "Starter places: $($entries.Count) from $Places$(if ($missing) { " ($missing without a picture)" })"
} else {
    Write-Warning "No places at ${Places}: the installer will ship an empty gallery."
}

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$publish" "$root\installer\Fractal.iss"
if ($LASTEXITCODE) { throw "Inno Setup failed" }

$setup = Join-Path $root "artifacts\FractalExplorer-Setup-$version.exe"
Write-Host ("Built {0} ({1:0.0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
