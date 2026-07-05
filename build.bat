@echo off
setlocal

set GAMEDIR=I:\SteamLibrary\steamapps\common\Flipping is Hard Demo
set PLUGINDIR=%GAMEDIR%\BepInEx\plugins\FIHMapEditor

echo Building FIH Custom Map Editor...
dotnet build -c Debug
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

if not exist "%PLUGINDIR%" mkdir "%PLUGINDIR%"
if not exist "%PLUGINDIR%\Maps" mkdir "%PLUGINDIR%\Maps"

copy /Y "bin\Debug\net6.0\FIHMapEditor.dll" "%PLUGINDIR%\"
if errorlevel 1 (
    echo DEPLOY FAILED - is the game path correct?
    exit /b 1
)

echo.
echo Deployed to %PLUGINDIR%
endlocal
