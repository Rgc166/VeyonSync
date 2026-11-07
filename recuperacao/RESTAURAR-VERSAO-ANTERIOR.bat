@echo off
setlocal
title Veyon Sync - Restaurar versao anterior
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0restaurar.ps1"
echo.
pause
