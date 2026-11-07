#requires -Version 5.1
# Veyon Sync - verificação ANTES (ou depois) de atualizar por arquivo. Não altera nada sem perguntar.
$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$log = $null
foreach ($d in @($PSScriptRoot, (Join-Path $env:ProgramData 'VeyonSync\logs'))) {
    try { New-Item -ItemType Directory -Force $d | Out-Null; $log = Join-Path $d "verificacao_$($env:COMPUTERNAME)_$stamp.txt"; Set-Content $log "Veyon Sync - verificação - $env:COMPUTERNAME - $(Get-Date)" -Encoding UTF8; break } catch { $log = $null }
}
function Say([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c; if ($log) { Add-Content $log $t -Encoding UTF8 } }
$problems = 0

$p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Say 'Execute como administrador (clique com o botão direito > Executar como administrador).' 'Red'; exit 1 }

$roles = @(
    @{ Role = 'Teacher'; Name = 'Professor (THC)'; Service = 'VeyonSyncTeacher' },
    @{ Role = 'Student'; Name = 'Aluno (STD)'; Service = 'VeyonSyncStudent' }
)
$found = $false
foreach ($r in $roles) {
    $svc = Get-Service $r.Service -ErrorAction SilentlyContinue
    $data = Join-Path $env:ProgramData "VeyonSync\$($r.Role)"
    $prog = Join-Path $env:ProgramFiles "VeyonSync\$($r.Role)"
    if (-not $svc -and -not (Test-Path $prog)) { continue }
    $found = $true
    Say ''
    Say "== $($r.Name) ==" 'Cyan'
    if ($svc) { Say "Serviço $($r.Service): $($svc.Status)" $(if ($svc.Status -eq 'Running') { 'Green' } else { 'Yellow' }) }
    else { Say "Serviço $($r.Service) não encontrado." 'Red'; $problems++ }
    $mark = Join-Path $data 'service.json'
    if (Test-Path $mark) {
        try { $m = Get-Content $mark -Raw | ConvertFrom-Json; Say "Versão em execução: $($m.Version) (iniciada em $($m.StartedAt))" 'Green' } catch { Say 'service.json ilegível.' 'Yellow' }
    } else { Say 'Versão: não informada (versões anteriores à 2.1 não gravam service.json).' 'Yellow' }
    $exe = Join-Path $prog "VeyonSync.$($r.Role).exe"
    if (Test-Path $exe) { Say "Programa: $exe ($((Get-Item $exe).VersionInfo.ProductVersion))" } else { Say "Programa não encontrado em $prog" 'Red'; $problems++ }

    # Trava de atualização esquecida (o atualizador foi interrompido)
    $lock = Join-Path $data 'update.lock'
    $running = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match '--apply-release|--apply-update' })
    if (Test-Path $lock) {
        if ($running.Count -gt 0) { Say 'Uma atualização está em andamento agora. Aguarde terminar antes de qualquer ação.' 'Yellow'; $problems++ }
        else {
            Say 'Encontrada trava de atualização antiga (update.lock) sem atualizador rodando: ela bloqueia novas atualizações.' 'Yellow'
            $a = Read-Host 'Remover a trava agora? [S/n]'
            if (-not $a -or $a.ToUpper().StartsWith('S')) { Remove-Item $lock -Force; Say 'Trava removida.' 'Green' } else { $problems++ }
        }
    } else { Say 'Nenhuma atualização travada.' 'Green' }

    # Resultado da última atualização (gravado pelo atualizador)
    foreach ($rf in @('release-result.json', 'update-result.json')) {
        $f = Join-Path $data $rf
        if (Test-Path $f) { try { $x = Get-Content $f -Raw | ConvertFrom-Json; Say "Resultado pendente ($rf): ok=$($x.Ok) versão=$($x.ToVersion)$($x.Version) $($x.Detail)" } catch { } }
    }
    $bk = Join-Path $data 'update-backup'
    if (Test-Path $bk) {
        $list = @(Get-ChildItem $bk -Directory | Sort-Object CreationTime -Descending)
        if ($list.Count -gt 0) { Say "Backups de atualização disponíveis: $($list.Count) (mais recente: $($list[0].Name))" 'Green' }
    } else { Say 'Ainda não há backups de atualização (normal antes da primeira atualização por arquivo).' }
    $logs = Join-Path $data 'logs'
    if (Test-Path $logs) {
        $last = Get-ChildItem $logs -Filter 'atualizacao-*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($last) { Say "Último log de atualização: $($last.FullName)" }
    }
}
if (-not $found) { Say 'Nenhuma instalação do Veyon Sync encontrada neste computador.' 'Yellow'; $problems++ }

# Espaço livre: o atualizador guarda backup do programa e dos dados.
$drive = Get-PSDrive -Name ($env:ProgramData.Substring(0, 1)) -ErrorAction SilentlyContinue
if ($drive) {
    $gb = [math]::Round($drive.Free / 1GB, 1)
    if ($gb -lt 2) { Say "Pouco espaço livre em $($drive.Name): ($gb GB). Libere ao menos 2 GB antes de atualizar." 'Red'; $problems++ }
    else { Say ''; Say "Espaço livre em $($drive.Name): $gb GB" 'Green' }
}
$veyon = Get-Service VeyonService -ErrorAction SilentlyContinue
if ($veyon) { Say "Serviço do Veyon: $($veyon.Status)" } else { Say 'Serviço do Veyon não encontrado neste computador.' 'Yellow' }

Say ''
if ($problems -eq 0) { Say 'Tudo pronto. Pode atualizar pela tela Atualizações > Atualizar por arquivo.' 'Green' }
else { Say "Atenção: $problems item(ns) acima precisam de cuidado antes de atualizar." 'Yellow' }
if ($log) { Say "Relatório salvo em: $log" 'Cyan' }
exit 0
