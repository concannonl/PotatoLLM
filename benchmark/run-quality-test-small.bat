@echo off
title PotatoLLM quality test (4 GB models)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0quality-test.ps1" -Small
echo.
pause
