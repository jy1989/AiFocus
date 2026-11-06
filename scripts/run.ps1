$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'src\AiFocus\bin\Debug\net9.0-windows10.0.17763.0\AiFocus.exe'
if (-not (Test-Path $app)) {
    dotnet build (Join-Path $root 'src\AiFocus\AiFocus.csproj') -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'AI Focus build failed' }
}
Start-Process -FilePath $app -WorkingDirectory (Split-Path $app)
Write-Host 'AI Focus is running in the system tray. Press Alt + Q.'
