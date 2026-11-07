@echo off
rem SPDX-License-Identifier: GPL-3.0-or-later
rem SteamPoweredPlayer launcher: builds on first run (or with --rebuild), then starts the app. Other arguments go to the app.
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%src\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\SteamPoweredPlayer.exe"

if not exist "%EXE%" goto build
if "%~1"=="--rebuild" goto build
goto run

:build
where dotnet >nul 2>&1 || (echo .NET SDK not found. Install it with: winget install Microsoft.DotNet.SDK.10 & pause & exit /b 1)
echo Building SteamPoweredPlayer...
dotnet build "%ROOT%src\SteamPoweredPlayer.csproj" -c Release -nologo -v q
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
if "%~1"=="--rebuild" shift

:run
start "" "%EXE%" %*
endlocal
