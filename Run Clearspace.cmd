@echo off
REM Development launcher: rebuilds first, so it is slower and shows this console.
REM For daily use run "Build Clearspace.cmd" once and use the desktop shortcut.
setlocal
cd /d "%~dp0Clearspace"

REM A still-running Clearspace holds a lock on its own exe, and the build fails at
REM the copy step rather than at compile. Closing it first turns a confusing
REM "Build failed" into a normal rebuild. Returns 128 when nothing was running,
REM which is not an error here.
taskkill /IM Clearspace.exe /F >nul 2>&1
if not errorlevel 1 timeout /t 1 /nobreak >nul

dotnet build -c Debug -v:quiet --nologo
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
start "" "bin\Debug\net10.0-windows\win-x64\Clearspace.exe"
endlocal
