#requires -Version 5.1
# Gera os DOIS ZIPs de atualização separados (professor e aluno) e o kit de recuperação.
#   VeyonSync-THC-Update-<versão>.zip  -> somente para o PROFESSOR
#   VeyonSync-STD-Update-<versão>.zip  -> somente para o ALUNO (também serve para a distribuição pelo THC)
#   VeyonSync-Recuperacao-<versão>.zip -> verificação antes de atualizar e restauração manual
# Cada ZIP de atualização contém o pacote COMPILADO e ASSINADO (.vsupd, formato do atualizador existente),
# o LEIA-ME específico e o SHA-256. Roda SOMENTE no PC de desenvolvimento (precisa da chave do publicador).
param([string]$Version = '', [string]$Notes = '')
$ErrorActionPreference = 'Stop'
$r = Split-Path $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$entregaRoot = Join-Path $r 'dist\Entrega'
New-Item -ItemType Directory -Force $entregaRoot | Out-Null
$log = Join-Path $entregaRoot "gerar_$stamp.txt"
function Log([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c; Add-Content -Path $log -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $t) -Encoding UTF8 }
$bom = New-Object System.Text.UTF8Encoding($true)
$code = 0
try {
    Log "Veyon Sync - gerar ZIPs de atualização ($stamp)" 'Cyan'
    if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $r 'Directory.Build.props'))).Project.PropertyGroup.VeyonSyncVersion.'#text' }
    $Version = "$Version".Trim().TrimStart('v')
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Versão inválida: $Version" }
    Log "Versão: $Version"

    $rel = Join-Path $r "dist\Releases\$Version"
    $thcPkg = Join-Path $rel "VeyonSync-THC-$Version-win-x64.vsupd"
    $stdPkg = Join-Path $rel "VeyonSync-STD-$Version-win-x64.vsupd"
    if (-not (Test-Path $thcPkg) -or -not (Test-Path $stdPkg)) {
        if (Test-Path $rel) { throw "A pasta $rel existe, mas está incompleta. Apague-a ou use outra versão." }
        Log 'Compilando, testando e assinando (PUBLICAR_UPDATE em modo "só gerar")...' 'Cyan'
        & (Join-Path $PSScriptRoot 'publish.ps1') -Version $Version -Notes $Notes -OnlyLocal
        if ($LASTEXITCODE -ne 0) { throw 'A geração dos pacotes falhou (veja o log indicado acima e o build-log.txt). Nenhum ZIP foi criado.' }
    } else { Log "Usando os pacotes já gerados em $rel" }
    foreach ($f in @($thcPkg, $stdPkg)) { if (-not (Test-Path $f)) { throw "Pacote ausente: $f" } }

    # Confere de novo assinatura e EDIÇÃO de cada pacote antes de embalar.
    $tool = Join-Path $r 'tools\ReleaseTool\ReleaseTool.csproj'
    $pub = Join-Path $r 'assets\release-public-key.txt'
    dotnet build $tool -c Release -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar tools\ReleaseTool.' }
    foreach ($x in @(@{ F = $thcPkg; P = 'THC' }, @{ F = $stdPkg; P = 'STD' })) {
        $out = dotnet run --project $tool -c Release --no-build -- verify $x.F --product $x.P --pub $pub
        if ($LASTEXITCODE -ne 0) { throw "Pacote $($x.P) recusado na conferência: $out" }
        Log "$out" 'Green'
    }

    $dest = Join-Path $entregaRoot $Version
    if (Test-Path $dest) { throw "Já existe $dest. Nada foi sobrescrito (apague a pasta para gerar de novo)." }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    function New-Zip([string]$Zip, [hashtable[]]$Items) {
        $z = [System.IO.Compression.ZipFile]::Open($Zip, 'Create')
        try {
            foreach ($i in $Items) {
                if ($i.Text) {
                    $e = $z.CreateEntry($i.Name)
                    $w = New-Object System.IO.StreamWriter($e.Open(), $bom)
                    $w.Write(($i.Text -replace "`r?`n", "`r`n")); $w.Dispose()
                } else {
                    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $i.File, $i.Name, 'Optimal')
                }
            }
        } finally { $z.Dispose() }
    }
    function Sha([string]$f) { (Get-FileHash $f -Algorithm SHA256).Hash }

    $leiaThc = @"
VEYON SYNC THC $Version — ATUALIZAÇÃO DO PROFESSOR
=================================================
Use SOMENTE no computador do professor (THC). No aluno ele é recusado.

Ordem: atualize primeiro o professor e depois os alunos.
Pré-requisito: professor com Veyon Sync 2.1.0 ou mais novo instalado (tela "Atualizações").

1. (Recomendado) Rode o kit de recuperação: VERIFICAR-ANTES-DE-ATUALIZAR.bat como administrador.
2. Extraia este ZIP (ex.: na Área de Trabalho ou no pendrive).
3. Abra o Veyon Sync THC > Atualizações > aba "Este professor" > Atualizar por arquivo.
4. Escolha o arquivo VeyonSync-THC-$Version-win-x64.vsupd que estava dentro deste ZIP.
   (A partir da 2.2 a tela também aceita o próprio ZIP.)
5. Confirme. O serviço para alguns segundos, a janela fecha e o professor volta na nova versão.
6. Abra o THC de novo e confira a versão $Version na barra lateral e em Atualizações.

Preservados: configurações, laboratório, certificados, arquivo de ligação, credenciamento dos alunos,
banco de dados, histórico, mapa e backups do Veyon. Se algo falhar, a versão anterior volta sozinha.
O Windows não é reiniciado.

SHA-256 do pacote: $(Sha $thcPkg)
(O SHA-256 confere a integridade; quem garante a origem é a assinatura conferida pelo programa.)
"@
    $leiaStd = @"
VEYON SYNC STD $Version — ATUALIZAÇÃO DO ALUNO
=============================================
Use SOMENTE nos computadores dos alunos (STD). No professor ele é recusado.

Atualize primeiro o professor (pacote THC). Funciona com professor 2.1 ou 2.2; com o professor
na 2.2 o andamento aparece separado (transferindo, instalando, confirmada).

JEITO A — pela rede, a partir do professor (recomendado):
1. No THC: Atualizações > aba "Computadores do laboratório" > Selecionar arquivo.
2. Escolha este ZIP (professor 2.2) ou o arquivo VeyonSync-STD-$Version-win-x64.vsupd de dentro dele.
3. Comece por um PC de teste ("Enviar só para o teste"). Quando ele aparecer como Confirmada,
   marque os demais e clique em "Distribuir para os selecionados".

JEITO B — por arquivo, em cada aluno (aluno com 2.1.0 ou mais novo):
1. Extraia este ZIP no pendrive.
2. No aluno: Veyon Sync STD > Atualizações > Atualizar por arquivo > escolha
   VeyonSync-STD-$Version-win-x64.vsupd > confirme.

Preservados: credenciamento, número do PC, vínculo com o professor, configurações e erros registrados.
Se algo falhar, a versão anterior volta sozinha. O Windows não é reiniciado.

SHA-256 do pacote: $(Sha $stdPkg)
(O SHA-256 confere a integridade; quem garante a origem é a assinatura conferida pelo programa.)
"@
    $thcZip = Join-Path $dest "VeyonSync-THC-Update-$Version.zip"
    $stdZip = Join-Path $dest "VeyonSync-STD-Update-$Version.zip"
    New-Zip $thcZip @(@{ File = $thcPkg; Name = (Split-Path $thcPkg -Leaf) }, @{ Name = 'LEIA-ME-PROFESSOR.txt'; Text = $leiaThc })
    New-Zip $stdZip @(@{ File = $stdPkg; Name = (Split-Path $stdPkg -Leaf) }, @{ Name = 'LEIA-ME-ALUNO.txt'; Text = $leiaStd })
    Log "Criado: $thcZip" 'Green'
    Log "Criado: $stdZip" 'Green'

    # Kit de recuperação (scripts, sem nada compilado nem segredo).
    $rec = Join-Path $r 'recuperacao'
    $items = @(Get-ChildItem $rec -File | ForEach-Object { @{ File = $_.FullName; Name = $_.Name } })
    $recZip = Join-Path $dest "VeyonSync-Recuperacao-$Version.zip"
    New-Zip $recZip $items
    Log "Criado: $recZip" 'Green'

    # Autoconferência: cada ZIP tem exatamente um .vsupd, da edição certa.
    foreach ($x in @(@{ Z = $thcZip; P = 'THC' }, @{ Z = $stdZip; P = 'STD' })) {
        $z = [System.IO.Compression.ZipFile]::OpenRead($x.Z)
        try {
            $pk = @($z.Entries | Where-Object { $_.FullName -like '*.vsupd' })
            if ($pk.Count -ne 1 -or $pk[0].Name -notlike "VeyonSync-$($x.P)-*") { throw "ZIP $($x.Z) com conteúdo inesperado." }
            if (@($z.Entries | Where-Object { $_.FullName -match '\.(key|pfx|pem|db|vsync)$' }).Count -gt 0) { throw "ZIP $($x.Z) contém arquivo proibido." }
        } finally { $z.Dispose() }
    }
    Get-ChildItem $dest -Filter *.zip | Get-FileHash -Algorithm SHA256 | ForEach-Object { "{0}  {1}" -f $_.Hash, (Split-Path $_.Path -Leaf) } |
        Set-Content (Join-Path $dest 'SHA256SUMS.txt') -Encoding ASCII
    Log ''
    Log "Pronto: $dest" 'Green'
    Log '  1) VeyonSync-THC-Update  -> professor   2) VeyonSync-STD-Update -> alunos   3) VeyonSync-Recuperacao -> pendrive' 'Green'
} catch {
    Log ("ERRO: " + $_.Exception.Message) 'Red'
    $code = 1
}
Write-Host ''
Write-Host "Log: $log" -ForegroundColor Cyan
exit $code
