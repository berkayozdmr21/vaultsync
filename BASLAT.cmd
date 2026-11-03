@echo off
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
 echo .NET 10 SDK gerekli. README kurulum adimlarini izleyin.
 pause
 exit /b 1
)
if not exist node_modules call npm ci
start "VaultSync API" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\api.ps1"
call npm run dev
