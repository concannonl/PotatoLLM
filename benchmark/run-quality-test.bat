@echo off
title PotatoLLM quality test
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0quality-test.ps1"
echo.
pause
