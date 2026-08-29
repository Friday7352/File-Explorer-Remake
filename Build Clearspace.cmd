@echo off
setlocal
cd /d "%~dp0Clearspace"

echo Publishing Clearspace...
echo.

dotnet publish -c Release -o "%~dp0dist"
if errorlevel 1 (
    echo.
    echo Publish failed. See the errors above.
    pause
    exit /b 1
)

echo.
echo Creating desktop shortcut...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop') + '\Clearspace.lnk');" ^
  "$s.TargetPath = '%~dp0dist\Clearspace.exe';" ^
  "$s.WorkingDirectory = '%~dp0dist';" ^
  "$s.Description = 'Clearspace file manager';" ^
  "$s.Save()"

echo.
echo Done.
echo   Executable:  %~dp0dist\Clearspace.exe
echo   Shortcut:    Desktop\Clearspace
echo.
echo Rerun this script only after changing the code.
pause
endlocal
