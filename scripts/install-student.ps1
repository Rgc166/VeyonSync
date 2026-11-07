#requires -Version 5.1
# Instala ou atualiza o Veyon Sync STD — Aluno. Preserva identidade, configuração, pareamento e pendências.
param(
    [string]$LinkFile = '',
    [ValidateSet('ask', 'yes', 'no')][string]$DesktopShortcut = 'ask',
    [switch]$NoOpenApp,
    [switch]$Update,
    # Parâmetros da Etapa 1 (ainda aceitos): instalação sem arquivo de ligação.
    [string]$TeacherHost = '', [string]$Lab = '', [string]$Cer = '', [string]$Position = '', [int]$Port = 5443
)
. "$PSScriptRoot\common.ps1"
$log = Start-VsLog 'instalar-aluno'
$code = 0
try {
    Assert-VsAdmin
    Assert-VsPlatform
    $root = Split-Path $PSScriptRoot
    $src = Join-Path $root 'Student'
    if (-not (Test-Path (Join-Path $src 'VeyonSync.Student.exe'))) { throw "Arquivos do aluno não encontrados em $src. Rode o BUILD.bat ou use o pacote completo." }
    $target = Join-Path $script:VsRoot 'Student'
    $data = Join-Path $script:VsData 'Student'
    $exe = Join-Path $target 'VeyonSync.Student.exe'
    $existing = (Test-Path $exe) -or (Test-Path (Join-Path $target 'Service\VeyonSync.Student.exe'))
    Write-VsLog "Origem: $src"
    Write-VsLog ("Instalação existente: {0}" -f $(if ($existing) { 'sim (dados e pareamento serão preservados)' } else { 'não' }))

    # Arquivo de ligação: parâmetro, ou escolha entre os .vsync da pasta Ligacoes do pendrive.
    if (-not $LinkFile -and -not $TeacherHost -and -not $Update) {
        $links = @(Get-ChildItem -Path (Join-Path $root 'Ligacoes') -Filter '*.vsync' -ErrorAction SilentlyContinue)
        if ($links.Count -eq 1) {
            if (Read-VsYesNo "Usar o arquivo de ligação $($links[0].Name)?" $true) { $LinkFile = $links[0].FullName }
        } elseif ($links.Count -gt 1) {
            Write-Host 'Arquivos de ligação disponíveis:'
            for ($i = 0; $i -lt $links.Count; $i++) { Write-Host "  [$($i + 1)] $($links[$i].Name)" }
            $n = Read-Host 'Número do arquivo (Enter para pular e importar depois pela interface)'
            if ($n -match '^\d+$' -and [int]$n -ge 1 -and [int]$n -le $links.Count) { $LinkFile = $links[[int]$n - 1].FullName }
        } else {
            Write-VsLog 'Nenhum .vsync na pasta Ligacoes; a ligação poderá ser importada pela interface STD.' 'Yellow'
        }
    }
    $create = if ($DesktopShortcut -eq 'ask') { Read-VsYesNo 'Criar atalho na Área de Trabalho?' $true } else { $DesktopShortcut -eq 'yes' }

    Stop-VsApps 'Student'
    Stop-VsService 'VeyonSyncStudent'
    $backup = Backup-VsInstall 'Student'
    try {
        Write-VsLog 'Copiando arquivos do programa...'
        Clear-VsAppFolder 'Student'
        Copy-VsTree $src $target
        Reset-VsProgramAcl 'Student'
        Protect-VsData 'Student'

        $cfg = Join-Path $data 'config.json'
        if ($TeacherHost -and -not (Test-Path $cfg)) {
            # Compatibilidade com a Etapa 1: endereço/laboratório informados manualmente.
            if ($TeacherHost -match '^\d+\.\d+\.\d+\.\d+$') { throw 'Informe o NOME do PC do professor (o certificado é emitido para o nome, não para o IP).' }
            @{ TeacherUrl = "https://${TeacherHost}:$Port"; Lab = $Lab; Position = $Position } | ConvertTo-Json | Set-Content $cfg -Encoding UTF8
            if ($Cer) { Import-Certificate -FilePath $Cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null }
        }
        Install-VsService 'Student' 'Veyon Sync STD - Aluno'
        if ($LinkFile) {
            Write-VsLog "Importando arquivo de ligação: $LinkFile"
            $out = & $exe --import-link $LinkFile 2>&1
            $out | ForEach-Object { Write-VsLog "  $_" }
            if ($LASTEXITCODE -ne 0) { Write-VsLog 'A ligação não foi importada; faça pela interface STD > Credenciamento.' 'Yellow' }
        }
        Start-VsService 'VeyonSyncStudent'
        $ver = & $exe --version
        Write-VsLog "Versão instalada: $ver"
    } catch {
        if ($backup) {
            Write-VsLog "Falha: $($_.Exception.Message)" 'Red'
            Stop-VsService 'VeyonSyncStudent'
            Restore-VsProgram 'Student' $backup
            try { Start-VsService 'VeyonSyncStudent' } catch { }
        }
        throw
    }
    Remove-VsLegacyLayout 'Student'
    Remove-VsShortcuts 'VeyonSync Student'
    New-VsShortcuts 'Student' 'Veyon Sync STD - Aluno' $create

    $state = Join-Path $data 'config.json'
    $paired = $false
    try { $paired = [bool]((Get-Content $state -Raw | ConvertFrom-Json).TokenProtected) } catch { }
    Write-VsLog ''
    Write-VsLog '================================================' 'Green'
    if ($paired) {
        Write-VsLog ' Veyon Sync STD atualizado. Pareamento existente preservado.' 'Green'
    } else {
        Write-VsLog ' Veyon Sync STD instalado. Falta o credenciamento:' 'Green'
        Write-VsLog '  1. No THC do professor: Credenciamento > Gerar código.' 'Green'
        Write-VsLog '  2. No STD (abrirá agora): informe o número do PC e o código.' 'Green'
    }
    Write-VsLog '================================================' 'Green'
    if (-not $paired -and -not $NoOpenApp -and -not $Update) { Start-Process (Join-Path $target 'App\VeyonSync.Student.App.exe') }
} catch {
    Write-VsLog ("ERRO: " + $_.Exception.Message) 'Red'
    Write-VsLog ($_ | Out-String)
    $code = 1
}
Write-Host ''
Write-Host "Log desta instalação: $log" -ForegroundColor Cyan
exit $code
