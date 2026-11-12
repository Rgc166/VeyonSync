@echo off
setlocal EnableExtensions
title Veyon Sync - Pendrive
set "ROOT=%~dp0"
if "%ROOT:~0,2%"=="\\" (
  echo Copie a pasta para um disco local ou pendrive. Nao execute a partir de pasta de rede.
  pause
  exit /b 1
)
net session >nul 2>&1
if errorlevel 1 (
  echo Solicitando permissao de administrador...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%ROOT%"
:menu
cls
echo ================================================
echo              Veyon Sync - Pendrive
echo ================================================
echo  Este PC: %COMPUTERNAME%
echo.
echo  [1] Instalar professor  (Veyon Sync THC)
echo  [2] Instalar aluno      (Veyon Sync STD)
echo  [3] Aplicar atualizacao neste PC (preserva dados)
echo  [4] Abrir instrucoes
echo  [5] Sair
echo.
choice /C 12345 /N /M "Escolha: "
if errorlevel 5 goto :eof
if errorlevel 4 goto docs
if errorlevel 3 goto upd
if errorlevel 2 goto std
if errorlevel 1 goto thc
goto menu
:thc
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\install-teacher.ps1"
pause
goto menu
:std
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\install-student.ps1"
pause
goto menu
:upd
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\update.ps1"
pause
goto menu
:docs
start "" notepad "%ROOT%LEIA-ME.txt"
goto menu
