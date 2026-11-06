$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build 'AiFocus.sln' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }
Write-Host "Built executable: $root\src\AiFocus\bin\Release\net9.0-windows10.0.17763.0\AiFocus.exe"
