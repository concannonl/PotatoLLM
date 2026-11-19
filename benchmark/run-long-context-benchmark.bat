@echo off
title PotatoLLM long-context benchmark
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0long-context-benchmark.ps1"
echo.
pause
