$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'src\AiCockpit\bin\Debug\net9.0-windows10.0.17763.0\AiCockpit.exe'
if (-not (Test-Path $app)) {
    dotnet build (Join-Path $root 'src\AiCockpit\AiCockpit.csproj') -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'AI Cockpit build failed' }
}
Start-Process -FilePath $app -WorkingDirectory (Split-Path $app)
Write-Host 'AI Cockpit is running in the system tray. Press Alt + Q.'
