@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0pack.ps1" (
    echo ERROR: pack.ps1 was not found. Put both files in the overlay source folder.
    pause
    exit /b 1
)
echo Building Playnite Overlay 3000...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack.ps1" -NoPause
if errorlevel 1 (
    echo.
    echo BUILD FAILED. Do not install an older file from the release folder.
    pause
    exit /b 1
)
if not exist "%~dp0release" (
    echo ERROR: release folder not found.
    pause
    exit /b 1
)
echo.
echo Build succeeded. Opening release folder...
echo Drag the newly built .pext onto Playnite and restart Playnite.
start "" explorer.exe "%~dp0release"
exit /b 0
