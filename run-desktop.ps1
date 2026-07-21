#Requires -Version 5.1
<#
.SYNOPSIS
  Build (if needed) and run the JODU desktop app.

.PARAMETER Dev
  Start the Vite React UI on http://localhost:5173 and run the desktop host in Debug
  so WebView2 loads the live UI.

.PARAMETER NoBuild
  Skip `dotnet build` and run the existing output directly when possible.

.EXAMPLE
  .\run-desktop.ps1

.EXAMPLE
  .\run-desktop.ps1 -Dev
#>
[CmdletBinding()]
param(
    [switch]$Dev,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$DesktopDir = Join-Path $Root "desktop"
$UiDir = Join-Path $DesktopDir "ui"
$Project = Join-Path $DesktopDir "Jodu.Desktop.csproj"

if (-not (Test-Path $Project)) {
    Write-Error "Desktop project not found: $Project"
}

function Assert-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Write-Error "'$Name' is required but was not found on PATH."
    }
}

Assert-Command "dotnet"

$viteJob = $null
try {
    if ($Dev) {
        Assert-Command "npm"

        if (-not (Test-Path (Join-Path $UiDir "node_modules"))) {
            Write-Host "Installing UI dependencies..." -ForegroundColor Cyan
            Push-Location $UiDir
            try { npm install } finally { Pop-Location }
        }

        Write-Host "Starting Vite dev server on http://localhost:5173 ..." -ForegroundColor Cyan
        $viteJob = Start-Job -ScriptBlock {
            param($Dir)
            Set-Location $Dir
            npm run dev
        } -ArgumentList $UiDir

        $ready = $false
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 500
            try {
                $resp = Invoke-WebRequest -Uri "http://localhost:5173" -UseBasicParsing -TimeoutSec 1
                if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 500) {
                    $ready = $true
                    break
                }
            } catch {
                # wait for Vite
            }
        }

        if (-not $ready) {
            Write-Warning "Vite did not become ready in time; desktop may fall back to packaged UI."
        } else {
            Write-Host "Vite is ready." -ForegroundColor Green
        }
    }

    Push-Location $DesktopDir
    try {
        if (-not $NoBuild) {
            Write-Host "Building Jodu.Desktop..." -ForegroundColor Cyan
            if ($Dev) {
                # Dev UI comes from Vite; skip production UI rebuild to avoid stale hashed assets.
                dotnet build $Project -c Debug -p:SkipViteBuild=true
            } else {
                # Release build also runs the Vite production build via MSBuild when node_modules exist.
                if (-not (Test-Path (Join-Path $UiDir "node_modules"))) {
                    Assert-Command "npm"
                    Write-Host "Installing UI dependencies..." -ForegroundColor Cyan
                    Push-Location $UiDir
                    try { npm install } finally { Pop-Location }
                }
                dotnet build $Project -c Release
            }
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet build failed with exit code $LASTEXITCODE"
            }
        }

        Write-Host "Launching JODU desktop..." -ForegroundColor Green
        if ($Dev) {
            dotnet run --project $Project -c Debug --no-build
        } else {
            dotnet run --project $Project -c Release --no-build
        }
    } finally {
        Pop-Location
    }
} finally {
    if ($viteJob) {
        Write-Host "Stopping Vite dev server..." -ForegroundColor DarkGray
        Stop-Job $viteJob -ErrorAction SilentlyContinue
        Remove-Job $viteJob -Force -ErrorAction SilentlyContinue
        Get-NetTCPConnection -LocalPort 5173 -ErrorAction SilentlyContinue |
            ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    }
}
