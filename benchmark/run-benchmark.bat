@echo off
title X250 LLM benchmark
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0benchmark.ps1"
echo.
pause
