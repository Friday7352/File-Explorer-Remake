@echo off
setlocal
cd /d "%~dp0"
echo Preparing Clearspace Demo Mode...

REM A still-running Clearspace holds a lock on its own exe, and the build fails at
REM the copy step rather than at compile. Closing it first turns a confusing
REM "Build failed" into a normal rebuild. Returns 128 when nothing was running,
REM which is not an error here.
taskkill /IM Clearspace.exe /F >nul 2>&1
if not errorlevel 1 timeout /t 1 /nobreak >nul

dotnet build ".\Clearspace\Clearspace.csproj" -c Debug --nologo
if errorlevel 1 (
    echo.
    echo The demo could not be prepared. See the message above.
    pause
    exit /b 1
)
start "" ".\Clearspace\bin\Debug\net10.0-windows\win-x64\Clearspace.exe" --demo
endlocal
