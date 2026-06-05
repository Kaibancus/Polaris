#requires -Version 5.1
# Scheme 2: framework-dependent publish (target PC needs .NET 9 Desktop Runtime; tiny size)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Stop any running instance so the output file is not locked
Get-Process DesktopPanel -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

dotnet publish -c Release -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true `
  -o publish-fd

$exe = Join-Path $PSScriptRoot 'publish-fd\DesktopPanel.exe'
if (Test-Path $exe) {
    $sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 2)
    Write-Host "Published: $exe ($sizeMB MB)" -ForegroundColor Green

    $hasRuntime = (dotnet --list-runtimes) -match 'Microsoft\.WindowsDesktop\.App 9\.'
    if ($hasRuntime) {
        Write-Host "This PC has .NET 9 Desktop Runtime; ready to run." -ForegroundColor Green
    } else {
        Write-Host "WARNING: .NET 9 Desktop Runtime not found. Target PC must install it:" -ForegroundColor Yellow
        Write-Host "  winget install Microsoft.DotNet.DesktopRuntime.9" -ForegroundColor Yellow
    }
} else {
    Write-Error "Publish failed: $exe not found"
}
