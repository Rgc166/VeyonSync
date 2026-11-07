@echo off
setlocal
title Veyon Sync - Gerar e publicar atualizacao
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish.ps1" %*
echo.
if errorlevel 1 (echo Terminou com ERRO. Veja o log indicado acima.) else (echo Terminou.)
pause
