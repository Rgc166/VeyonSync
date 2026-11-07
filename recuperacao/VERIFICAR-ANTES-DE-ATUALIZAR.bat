@echo off
setlocal
title Veyon Sync - Verificar antes de atualizar
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verificar.ps1"
echo.
pause
