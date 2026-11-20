@echo off
title Installing PotatoLLM
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
  echo.
  echo Installation did not finish. Take a photo of this window if you need help.
  pause
)
