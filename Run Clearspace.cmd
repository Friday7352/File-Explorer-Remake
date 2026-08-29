@echo off
REM Development launcher: rebuilds first, so it is slower and shows this console.
REM For daily use run "Build Clearspace.cmd" once and use the desktop shortcut.
setlocal
cd /d "%~dp0Clearspace"
dotnet build -c Debug -v:quiet --nologo
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
start "" "bin\Debug\net10.0-windows\win-x64\Clearspace.exe"
endlocal
