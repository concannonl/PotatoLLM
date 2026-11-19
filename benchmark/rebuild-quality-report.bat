@echo off
title PotatoLLM quality report
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0quality-test.ps1" -ReportOnly
echo.
pause
