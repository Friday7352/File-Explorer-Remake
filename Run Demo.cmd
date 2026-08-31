@echo off
setlocal
cd /d "%~dp0"
echo Preparing Clearspace Demo Mode...
dotnet build ".\Clearspace\Clearspace.csproj" -c Debug --nologo
if errorlevel 1 (
    echo.
    echo The demo could not be prepared. See the message above.
    pause
    exit /b 1
)
start "" ".\Clearspace\bin\Debug\net10.0-windows\win-x64\Clearspace.exe" --demo
endlocal
