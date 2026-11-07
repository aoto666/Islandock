@echo off
rem Build and start Islandock (double-click to run)
cd /d "%~dp0"
taskkill /im Islandock.exe /f >nul 2>&1
"C:\Program Files\dotnet\dotnet.exe" build -nologo -v q
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
start "" "%~dp0bin\Debug\net10.0-windows10.0.19041.0\Islandock.exe"
