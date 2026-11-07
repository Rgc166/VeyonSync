#requires -Version 5.1
# Atualizador de um clique: detecta THC e/ou STD instalados neste PC e atualiza preservando todos os dados.
# Cada instalador faz backup (programa + dados), para os componentes, aplica, reinicia, confere e reverte se falhar.
. "$PSScriptRoot\common.ps1"
$log = Start-VsLog 'atualizar'
$code = 0
try {
    Assert-VsAdmin
    $did = $false
    $desk = [Environment]::GetFolderPath('CommonDesktopDirectory')

    $tCfg = Join-Path $script:VsRoot 'Teacher\appsettings.json'
    $tLegacy = Join-Path $script:VsData 'Teacher\config.json'
    if ((Get-Service VeyonSyncTeacher -ErrorAction SilentlyContinue) -or (Test-Path $tCfg)) {
        $lab = $null
        if (Test-Path $tCfg) { $lab = (Get-Content $tCfg -Raw | ConvertFrom-Json).Lab }
        elseif (Test-Path $tLegacy) { $lab = (Get-Content $tLegacy -Raw | ConvertFrom-Json).lab }
        if (-not $lab) { throw 'Professor instalado, mas o laboratório não foi encontrado na configuração. Use Instalar professor.' }
        $ds = if (Test-Path (Join-Path $desk 'Veyon Sync THC - Professor.lnk')) { 'yes' } else { 'no' }
        Write-VsLog "Atualizando Veyon Sync THC (laboratório $lab)..." 'Cyan'
        & "$PSScriptRoot\install-teacher.ps1" -Lab $lab -DesktopShortcut $ds
        if ($LASTEXITCODE -ne 0) { $code = 1; Write-VsLog 'Atualização do THC falhou (versão anterior restaurada quando possível). Veja o log do instalador.' 'Red' }
        $did = $true
    }
    if (Get-Service VeyonSyncStudent -ErrorAction SilentlyContinue) {
        $ds = if (Test-Path (Join-Path $desk 'Veyon Sync STD - Aluno.lnk')) { 'yes' } else { 'no' }
        Write-VsLog 'Atualizando Veyon Sync STD...' 'Cyan'
        & "$PSScriptRoot\install-student.ps1" -Update -DesktopShortcut $ds
        if ($LASTEXITCODE -ne 0) { $code = 1; Write-VsLog 'Atualização do STD falhou (versão anterior restaurada quando possível). Veja o log do instalador.' 'Red' }
        $did = $true
    }
    if (-not $did) { Write-VsLog 'Nenhuma instalação do Veyon Sync encontrada neste PC. Use Instalar professor ou Instalar aluno.' 'Yellow' }
} catch {
    Write-VsLog ("ERRO: " + $_.Exception.Message) 'Red'
    $code = 1
}
Write-Host ''
Write-Host "Log da atualização: $log" -ForegroundColor Cyan
exit $code
