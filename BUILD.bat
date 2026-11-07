@echo off
setlocal
title Veyon Sync - Compilar
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 goto nosdk
dotnet --list-sdks | findstr /B "10." >nul
if errorlevel 1 goto nosdk
echo Compilando Veyon Sync (pode levar alguns minutos na primeira vez)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1"
if errorlevel 1 (
  echo.
  echo ERRO na compilacao. Veja build-log.txt nesta pasta.
  pause
  exit /b 1
)
echo.
echo Pronto. Pasta para o pendrive: %~dp0dist\Pendrive\VeyonSync
echo Log: %~dp0build-log.txt
pause
exit /b 0
:nosdk
echo O .NET SDK 10 nao foi encontrado neste computador.
echo Instale "SDK 10.0 (x64)" em https://dotnet.microsoft.com/download/dotnet/10.0
echo e execute este BUILD.bat novamente. Somente o PC que compila precisa do SDK.
pause
exit /b 1
