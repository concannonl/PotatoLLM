@echo off
title PotatoLLM context benchmark
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0context-benchmark.ps1"
echo.
pause
