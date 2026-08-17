@echo off
setlocal
rem ── MusicEngine v2 launcher ─────────────────────────────────────────────
rem Uses the local .NET 8 SDK if present (D:\dotnet-sdk), else falls back to PATH.
set DOTNET_ROOT=D:\dotnet-sdk
if exist "D:\dotnet-sdk\dotnet.exe" set PATH=D:\dotnet-sdk;%PATH%

cd /d "%~dp0"

echo Building...
dotnet build MusicEngine.sln -c Release || exit /b 1

echo Starting MusicEngine...
start "" dotnet run --project src\MusicEngine.App -c Release --no-build
endlocal
