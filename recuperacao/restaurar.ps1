#requires -Version 5.1
# Veyon Sync - restauração MANUAL do backup feito pelo atualizador (programa + dados).
# Use só se a atualização deu errado e a restauração automática não resolveu. Nunca reinicia o Windows.
param([ValidateSet('', 'Teacher', 'Student')][string]$Role = '')
$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$log = $null
foreach ($d in @($PSScriptRoot, (Join-Path $env:ProgramData 'VeyonSync\logs'))) {
    try { New-Item -ItemType Directory -Force $d | Out-Null; $log = Join-Path $d "restauracao_$($env:COMPUTERNAME)_$stamp.txt"; Set-Content $log "Veyon Sync - restauração - $env:COMPUTERNAME - $(Get-Date)" -Encoding UTF8; break } catch { $log = $null }
}
function Say([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c; if ($log) { Add-Content $log ("{0:HH:mm:ss} {1}" -f (Get-Date), $t) -Encoding UTF8 } }
$code = 0
try {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Execute como administrador.' }

    if (-not $Role) {
        $has = @()
        if (Test-Path (Join-Path $env:ProgramData 'VeyonSync\Teacher\update-backup')) { $has += 'Teacher' }
        if (Test-Path (Join-Path $env:ProgramData 'VeyonSync\Student\update-backup')) { $has += 'Student' }
        if ($has.Count -eq 0) { throw 'Nenhum backup de atualização encontrado neste computador. Nada a restaurar.' }
        if ($has.Count -eq 1) { $Role = $has[0] }
        else {
            do { $c = (Read-Host 'Restaurar qual? 1 = Professor (THC)   2 = Aluno (STD)').Trim() } while ($c -ne '1' -and $c -ne '2')
            $Role = if ($c -eq '1') { 'Teacher' } else { 'Student' }
        }
    }
    $svcName = if ($Role -eq 'Teacher') { 'VeyonSyncTeacher' } else { 'VeyonSyncStudent' }
    $data = Join-Path $env:ProgramData "VeyonSync\$Role"
    $prog = Join-Path $env:ProgramFiles "VeyonSync\$Role"
    $root = Join-Path $data 'update-backup'
    $list = @(Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'programa') } | Sort-Object CreationTime -Descending)
    if ($list.Count -eq 0) { throw "Nenhum backup completo em $root." }
    Say "Backups disponíveis ($Role):" 'Cyan'
    for ($i = 0; $i -lt $list.Count; $i++) { Say ("  [{0}] {1}  (criado em {2})" -f ($i + 1), $list[$i].Name, $list[$i].CreationTime) }
    $n = Read-Host "Número do backup (Enter = 1, o mais recente)"
    if (-not $n) { $n = '1' }
    $idx = [int]$n - 1
    if ($idx -lt 0 -or $idx -ge $list.Count) { throw 'Número inválido.' }
    $bk = $list[$idx].FullName
    Say "Backup escolhido: $bk" 'Yellow'
    Say 'O programa e os dados (banco, configuração, credenciamento, certificados) voltam ao estado do backup.' 'Yellow'
    if ((Read-Host 'Digite RESTAURAR para confirmar') -ne 'RESTAURAR') { Say 'Cancelado. Nada foi alterado.'; exit 0 }

    # Cópia de segurança do estado atual antes de restaurar (permite desfazer).
    # Fora da pasta de dados (não entra em cópias nem na limpeza automática de backups).
    $now = Join-Path $env:ProgramData "VeyonSync\restauracoes\$Role-antes-$stamp"
    Say "Guardando o estado atual em $now ..."
    $running = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match '--apply-release|--apply-update' })
    if ($running.Count -gt 0) { throw 'Uma atualização está em andamento. Aguarde terminar.' }

    $s = Get-Service $svcName -ErrorAction SilentlyContinue
    if ($s -and $s.Status -ne 'Stopped') { Say "Parando $svcName..."; Stop-Service $svcName -Force; $s.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
    Get-Process -Name "VeyonSync.$Role.App" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2

    function Robo([string]$From, [string]$To, [string[]]$Opts) {
        # robocopy: códigos 0 a 7 = sucesso; 8 ou mais = falha.
        & robocopy.exe $From $To @Opts /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Falha ao copiar de $From para $To (robocopy $LASTEXITCODE)." }
    }
    $skip = @('logs', 'update-tmp', 'update-runner', 'update-backup', 'backups', 'VeyonBackups', 'Updates')
    if (Test-Path $prog) { Robo $prog (Join-Path $now 'programa') @('/E') }
    $xd = @($skip | ForEach-Object { Join-Path $data $_ })
    Robo $data (Join-Path $now 'dados') (@('/E', '/XD') + $xd)

    # Programa: espelho exato do backup (remove arquivos que a versão nova criou).
    Robo (Join-Path $bk 'programa') $prog @('/MIR')
    Say 'Programa restaurado.' 'Green'

    # Dados: o banco volta ao estado anterior; arquivos -wal/-shm da versão nova não podem sobrar.
    $dsrc = Join-Path $bk 'dados'
    if (Test-Path $dsrc) {
        Get-ChildItem $data -File | Where-Object { ($_.Name -like '*-wal' -or $_.Name -like '*-shm') -and -not (Test-Path (Join-Path $dsrc $_.Name)) } | Remove-Item -Force
        Robo $dsrc $data @('/E')
        Say 'Dados restaurados.' 'Green'
    }
    $lock = Join-Path $data 'update.lock'
    if (Test-Path $lock) { Remove-Item $lock -Force }

    Start-Service $svcName
    (Get-Service $svcName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    Say "Serviço $svcName em execução." 'Green'
    Say "Se precisar desfazer esta restauração, o estado anterior está em: $now" 'Cyan'
} catch {
    Say ("ERRO: " + $_.Exception.Message) 'Red'
    $code = 1
}
if ($log) { Say "Log: $log" 'Cyan' }
exit $code
