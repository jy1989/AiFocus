@echo off
setlocal
cd /d "%~dp0"
if not exist "src\AiCockpit\bin\Debug\net9.0-windows10.0.17763.0\AiCockpit.exe" (
    dotnet build "src\AiCockpit\AiCockpit.csproj" -c Debug
    if errorlevel 1 (
        echo AI Cockpit build failed.
        pause
        exit /b 1
    )
)
start "" "src\AiCockpit\bin\Debug\net9.0-windows10.0.17763.0\AiCockpit.exe"
exit /b 0
