<#
.SYNOPSIS
    Launch Polaris with the in-app FPS profiler and show a LIVE per-animation
    frame-rate table while you operate it.

.DESCRIPTION
    Sets POLARIS_FPS=1, (optionally) builds the Debug binary, launches Polaris,
    then polls the profiler's summary file every 2s and reprints it. The app
    attributes every rendered frame to the active animation "scene":

        GlassRise    - liquid-glass dock rising from the bottom on summon
        GlassScroll  - wheel / trackpad / scrollbar scrolling of the grid
        RingsExpand  - Saturn ring expand burst on summon
        SaturnIdle   - Saturn continuous orbit + planet spin while shown
        HoverZoom    - icon hover zoom / glow / label
        Idle         - shown, nothing animating

    Exercise each one (Ctrl+4 to summon/hide, hover icons, scroll the glass
    grid, switch themes in settings), watch the table, then Ctrl+C to stop.

    Logs are written next to the exe:
        bin\Debug\net9.0-windows\fps-summary-<stamp>.txt   (live table)
        bin\Debug\net9.0-windows\fps-samples-<stamp>.csv   (0.5s samples)

.PARAMETER NoBuild
    Skip 'dotnet build' and run the existing binary.
#>
param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$outDir = Join-Path $root 'bin\Debug\net9.0-windows'
$exe = Join-Path $outDir 'Polaris.exe'

# Close any running instance so the build can overwrite the exe.
Get-Process Polaris -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

if (-not $NoBuild) {
    Write-Host 'Building Polaris (Debug)...' -ForegroundColor Cyan
    dotnet build -c Debug -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

if (-not (Test-Path $exe)) { throw "Executable not found: $exe" }

# Clear previous logs so we read a fresh session.
Get-ChildItem $outDir -Filter 'fps-*.*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$env:POLARIS_FPS = '1'
Write-Host 'Launching Polaris with FPS profiler enabled...' -ForegroundColor Green
$proc = Start-Process $exe -PassThru
$env:POLARIS_FPS = $null

Write-Host ''
Write-Host 'Now operate the app:' -ForegroundColor Yellow
Write-Host '  - Ctrl+4 to summon / hide the panel (GlassRise / RingsExpand)'
Write-Host '  - Hover icons (HoverZoom)'
Write-Host '  - Scroll the glass grid: wheel, two-finger, or drag the scrollbar (GlassScroll)'
Write-Host '  - Switch themes in Settings to profile Saturn (SaturnIdle)'
Write-Host ''
Write-Host 'Live table refreshes every 2s. Press Ctrl+C to stop.' -ForegroundColor DarkGray
Write-Host ''

try {
    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 2
        $summary = Get-ChildItem $outDir -Filter 'fps-summary-*.txt' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        Clear-Host
        Write-Host "Polaris FPS - live per-animation averages  ($(Get-Date -Format HH:mm:ss))" -ForegroundColor Cyan
        Write-Host ('-' * 70) -ForegroundColor DarkGray
        if ($summary) {
            foreach ($line in Get-Content $summary.FullName) {
                if ($line -match 'LOW') { Write-Host $line -ForegroundColor Red }
                elseif ($line -match '^Scene') { Write-Host $line -ForegroundColor White }
                else { Write-Host $line }
            }
        } else {
            Write-Host 'Waiting for first samples... operate the app to generate animation frames.' -ForegroundColor DarkGray
        }
        Write-Host ('-' * 70) -ForegroundColor DarkGray
        Write-Host 'AvgFPS = average over all frames of that scene; MinFPS = 1000/worst single frame.' -ForegroundColor DarkGray
        Write-Host 'Press Ctrl+C to stop reporting (the app keeps running).' -ForegroundColor DarkGray
    }
} finally {
    if ($summary) {
        Write-Host ''
        Write-Host "Final summary file: $($summary.FullName)" -ForegroundColor Green
    }
}
