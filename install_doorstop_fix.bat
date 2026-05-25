@echo off
echo Installing DoorstopFix...

set GAME_DIR=C:\Program Files\StretchSense\XRGame
set FIX_DLL=D:\code\xrgame-memory\DoorstopFix\bin\Release\netstandard2.1\DoorstopFix.dll

REM Copy the fix DLL to the game directory
copy /Y "%FIX_DLL%" "%GAME_DIR%\DoorstopFix.dll"
if errorlevel 1 (
    echo ERROR: Failed to copy DoorstopFix.dll - run as admin?
    pause
    exit /b 1
)

REM Update doorstop_config.ini to point to our DLL instead of BepInEx
powershell -Command "(Get-Content '%GAME_DIR%\doorstop_config.ini') -replace 'target_assembly=.*', 'target_assembly=DoorstopFix.dll' | Set-Content '%GAME_DIR%\doorstop_config.ini'"
if errorlevel 1 (
    echo ERROR: Failed to update doorstop_config.ini
    pause
    exit /b 1
)

echo.
echo Done! DoorstopFix.dll installed.
echo doorstop_config.ini updated to target DoorstopFix.dll
echo.
echo Log output will be at: %GAME_DIR%\doorstop_fix.log
pause
