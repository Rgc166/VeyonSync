#requires -Version 5.1
param([switch]$RemoveData)
. "$PSScriptRoot\common.ps1"
$log = Start-VsLog 'desinstalar-aluno'
Assert-VsAdmin
Stop-VsApps 'Student'
$n = 'VeyonSyncStudent'
if (Get-Service $n -ErrorAction SilentlyContinue) { Stop-VsService $n; & sc.exe delete $n | Out-Null }
Remove-Item (Join-Path $script:VsRoot 'Student') -Recurse -Force -ErrorAction SilentlyContinue
Remove-VsShortcuts 'Veyon Sync STD - Aluno'
if ($RemoveData) { Remove-Item (Join-Path $script:VsData 'Student') -Recurse -Force; Write-VsLog 'Dados removidos.' }
else { Write-VsLog 'Dados preservados (identidade, configuração, pareamento). Use -RemoveData para apagar.' }
Write-Host "Log: $log"
