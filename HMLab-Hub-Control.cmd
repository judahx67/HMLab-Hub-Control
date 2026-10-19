@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1"
if errorlevel 1 (
  pause
  exit /b 1
)
start "" "%~dp0HMLab-Hub-Control.exe" %*
