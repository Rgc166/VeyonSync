#requires -Version 5.1
param([switch]$RemoveData)
. "$PSScriptRoot\common.ps1"
$log = Start-VsLog 'desinstalar-professor'
Assert-VsAdmin
Stop-VsApps 'Teacher'
$n = 'VeyonSyncTeacher'
if (Get-Service $n -ErrorAction SilentlyContinue) { Stop-VsService $n; & sc.exe delete $n | Out-Null }
Remove-Item (Join-Path $script:VsRoot 'Teacher') -Recurse -Force -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName 'VeyonSync Professor' -ErrorAction SilentlyContinue
Remove-VsShortcuts 'Veyon Sync THC - Professor'
if ($RemoveData) { Remove-Item (Join-Path $script:VsData 'Teacher') -Recurse -Force; Write-VsLog 'Dados removidos.' }
else { Write-VsLog 'Dados preservados (banco, histórico, certificado, backups). Use -RemoveData para apagar.' }
Write-Host "Log: $log"
