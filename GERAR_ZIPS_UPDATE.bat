@echo off
setlocal
title Veyon Sync - Gerar os ZIPs de atualizacao (THC e STD)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\gerar-zips-update.ps1" %*
echo.
if errorlevel 1 (echo Terminou com ERRO. Veja o log indicado acima.) else (echo Terminou. Os ZIPs estao em dist\Entrega\)
pause
