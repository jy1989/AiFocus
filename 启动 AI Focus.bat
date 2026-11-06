@echo off
setlocal
cd /d "%~dp0"
if not exist "src\AiFocus\bin\Debug\net9.0-windows10.0.17763.0\AiFocus.exe" (
    dotnet build "src\AiFocus\AiFocus.csproj" -c Debug
    if errorlevel 1 (
        echo AI Focus build failed.
        pause
        exit /b 1
    )
)
start "" "src\AiFocus\bin\Debug\net9.0-windows10.0.17763.0\AiFocus.exe"
exit /b 0
