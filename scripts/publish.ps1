#requires -Version 5.1
# Gera (e opcionalmente publica no GitHub Releases) os pacotes assinados THC e STD.
# Roda SOMENTE no PC de desenvolvimento. A chave privada e o login do GitHub nunca saem deste PC.
param([string]$Version = '', [string]$Repo = '', [switch]$OnlyLocal)
$ErrorActionPreference = 'Stop'
$r = Split-Path $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$relRoot = Join-Path $r 'dist\Releases'
New-Item -ItemType Directory -Force $relRoot | Out-Null
$log = Join-Path $relRoot "publicar_$stamp.txt"
function Log([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c; Add-Content -Path $log -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $t) -Encoding UTF8 }
$keyDir = Join-Path $env:USERPROFILE '.veyonsync-release'
$pubFile = Join-Path $r 'assets\release-public-key.txt'
$repoFile = Join-Path $r 'assets\github-repo.txt'
$tool = Join-Path $r 'tools\ReleaseTool\ReleaseTool.csproj'
$code = 0
try {
    Log "Veyon Sync - gerar/publicar atualização ($stamp)" 'Cyan'

    # 1. Ferramentas
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK não encontrado. Instale o SDK 10 (x64).' }
    if (-not ((dotnet --list-sdks) -match '^10\.')) { throw '.NET SDK 10 não encontrado (dotnet --list-sdks).' }
    Log 'Compilando a ferramenta de pacotes...'
    dotnet build $tool -c Release -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar tools\ReleaseTool.' }
    # No PowerShell 5.1, texto em stderr de programas externos vira erro com 'Stop': gh roda com 'Continue'.
    function GhCode { $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'; & gh @args 2>&1 | Out-Null; $c = $LASTEXITCODE; $ErrorActionPreference = $old; return $c }
    function GhRun { $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'; & gh @args 2>&1 | ForEach-Object { Log "$_" }; $c = $LASTEXITCODE; $ErrorActionPreference = $old; return $c }
    function Tool { dotnet run --project $tool -c Release --no-build -- @args; if ($LASTEXITCODE -ne 0) { throw "ReleaseTool falhou: $($args[0])" } }

    # 2. Chave do publicador (privada fica em %USERPROFILE%\.veyonsync-release, protegida por DPAPI)
    if (-not (Test-Path (Join-Path $keyDir 'publisher.key'))) {
        Log 'Nenhuma chave de publicador neste PC.' 'Yellow'
        $a = Read-Host 'Criar uma chave nova agora? (as instalações passarão a confiar nela) [S/n]'
        if ($a -and -not $a.ToUpper().StartsWith('S')) { throw 'Sem chave de publicador. Cancelado.' }
        Tool keygen $keyDir $pubFile
        Log "Chave pública gravada em $pubFile (pode ir para o Git; a privada NÃO)." 'Green'
        Log 'IMPORTANTE: faça backup da pasta %USERPROFILE%\.veyonsync-release. Sem ela você não consegue publicar versões aceitas pelos PCs instalados.' 'Yellow'
    }
    $pub = (dotnet run --project $tool -c Release --no-build -- pubkey $keyDir).Trim()
    if (-not (Test-Path $pubFile)) { Set-Content -Path $pubFile -Value $pub -NoNewline -Encoding ASCII; Log "Chave pública recriada em $pubFile" }
    elseif ((Get-Content $pubFile -Raw).Trim() -ne $pub) { throw "assets\release-public-key.txt não corresponde à chave deste PC. Não publique com a chave errada." }

    # 3. Versão e notas
    $cur = ([xml](Get-Content (Join-Path $r 'Directory.Build.props'))).Project.PropertyGroup.VeyonSyncVersion.'#text'
    if (-not $Version) { $Version = Read-Host "Versão a gerar (atual no projeto: $cur, ex.: 2.1.1 ou 2.2.0-beta.1)" }
    $Version = $Version.Trim().TrimStart('v')
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Versão inválida: $Version" }
    $pre = $Version.Contains('-')
    $outDir = Join-Path $relRoot $Version
    if (Test-Path $outDir) { throw "Já existem pacotes da versão $Version em $outDir. Use outra versão (nada foi sobrescrito)." }
    Write-Host 'Notas da atualização (o que mudou). Termine com uma linha vazia:' -ForegroundColor Cyan
    $lines = @(); while ($true) { $l = Read-Host; if ([string]::IsNullOrWhiteSpace($l)) { break }; $lines += $l }
    $notes = ($lines -join "`r`n"); if (-not $notes) { $notes = "Veyon Sync $Version" }

    # 4. Publicar ou só gerar?
    $publish = $false
    if (-not $OnlyLocal) {
        $m = Read-Host '[1] Só gerar os pacotes neste PC   [2] Gerar e publicar no GitHub Releases   (1/2)'
        $publish = ($m -eq '2')
    }
    if ($publish) {
        if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) não encontrado. Instale em https://cli.github.com e rode "gh auth login".' }
        if ((GhCode auth status) -ne 0) { throw 'GitHub CLI sem login. Rode "gh auth login" neste PC.' }
        if (-not $Repo -and (Test-Path $repoFile)) { $Repo = (Get-Content $repoFile -Raw).Trim() }
        if (-not $Repo) { $Repo = Read-Host 'Repositório do GitHub (dono/repositorio)' }
        if ($Repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "Repositório inválido: $Repo" }
        if ((GhCode release view "v$Version" --repo $Repo) -eq 0) { throw "A Release v$Version já existe em $Repo. Não será sobrescrita; use outra versão." }
        if (-not (Test-Path $repoFile)) { Set-Content -Path $repoFile -Value $Repo -NoNewline -Encoding ASCII; Log "Repositório padrão gravado em $repoFile" }
    }

    # 5. Compilar (testes incluídos; para em qualquer erro)
    Log "Compilando a versão $Version..." 'Cyan'
    & (Join-Path $PSScriptRoot 'build.ps1') -Version $Version
    if (-not $?) { throw 'Build falhou. Veja build-log.txt.' }
    $pend = Join-Path $r 'dist\Pendrive\VeyonSync'
    if (-not (Test-Path (Join-Path $pend 'Teacher\VeyonSync.Teacher.exe'))) { throw 'Compilação incompleta: professor ausente.' }

    # 6. Pacotes assinados separados
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $nf = Join-Path $outDir 'notas.txt'; Set-Content -Path $nf -Value $notes -Encoding UTF8
    $thc = Join-Path $outDir "VeyonSync-THC-$Version-win-x64.vsupd"
    $std = Join-Path $outDir "VeyonSync-STD-$Version-win-x64.vsupd"
    Tool pack --product THC --version $Version --min 2.0.0 --notes-file $nf --service (Join-Path $pend 'Teacher') --app (Join-Path $pend 'Teacher\App') --key-dir $keyDir --out $thc
    Tool pack --product STD --version $Version --min 2.0.0 --notes-file $nf --service (Join-Path $pend 'Student') --app (Join-Path $pend 'Student\App') --key-dir $keyDir --out $std
    Tool verify $thc --product THC --pub $pubFile
    Tool verify $std --product STD --pub $pubFile

    # 6.1 ZIP completo para a primeira instalação: serviços, interfaces, menu e tutoriais offline.
    $installer = Join-Path $outDir 'VeyonSync-Instalacao-win-x64.zip'
    if (-not (Test-Path (Join-Path $pend 'INICIAR.bat'))) { throw 'Instalador incompleto: INICIAR.bat ausente.' }
    if (-not (Test-Path (Join-Path $pend 'LEIA-ME.txt'))) { throw 'Instalador incompleto: tutorial offline ausente.' }
    if (Test-Path $installer) { Remove-Item $installer -Force }
    Compress-Archive -Path $pend -DestinationPath $installer -CompressionLevel Optimal
    if (-not (Test-Path $installer)) { throw 'Falha ao gerar o ZIP completo de instalação.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($installer)
    try {
        $hasMenu = @($archive.Entries | Where-Object { $_.FullName -match '(^|[\\/])INICIAR\.bat$' }).Count -gt 0
        $hasGuide = @($archive.Entries | Where-Object { $_.FullName -match '(^|[\\/])LEIA-ME\.txt$' }).Count -gt 0
        if (-not $hasMenu) { throw 'O ZIP de instalação não contém INICIAR.bat.' }
        if (-not $hasGuide) { throw 'O ZIP de instalação não contém o tutorial LEIA-ME.txt.' }
    } finally {
        $archive.Dispose()
    }

    # 6.2 Hashes de todos os artefatos que serão anexados à Release.
    $releaseAssets = @($installer, $thc, $std)
    $sumLines = foreach ($asset in $releaseAssets) {
        $hash = Get-FileHash -LiteralPath $asset -Algorithm SHA256
        "{0}  {1}" -f $hash.Hash, (Split-Path $asset -Leaf)
    }
    $sumLines | Set-Content (Join-Path $outDir 'SHA256SUMS.txt') -Encoding ASCII
    Log "Instalador completo, THC e STD prontos em $outDir" 'Green'

    # 7. Publicação com confirmação final
    if ($publish) {
        Write-Host ''
        Write-Host "Publicar v$Version em $Repo $(if ($pre) {'(versão de TESTE)'})" -ForegroundColor Yellow
        Write-Host "  $thc`n  $std" -ForegroundColor Yellow
        $ok = Read-Host 'Digite PUBLICAR para confirmar'
        if ($ok -ne 'PUBLICAR') { Log 'Publicação cancelada. Os pacotes locais foram mantidos.' 'Yellow' }
        else {
            $args2 = @('release', 'create', "v$Version", $installer, $thc, $std, (Join-Path $outDir 'SHA256SUMS.txt'), '--repo', $Repo, '--title', "Veyon Sync $Version", '--notes-file', $nf)
            if ($pre) { $args2 += '--prerelease' }
            if ((GhRun @args2) -ne 0) { throw 'gh release create falhou (veja a mensagem acima).' }
            Log "Release v$Version publicada em https://github.com/$Repo/releases/tag/v$Version" 'Green'
        }
    }
    Log 'Lembrete: as instalações só aceitam pacotes desta chave a partir de uma versão compilada com assets\release-public-key.txt.' 'DarkGray'
} catch {
    Log ("ERRO: " + $_.Exception.Message) 'Red'
    $code = 1
}
Write-Host ''
Write-Host "Log: $log" -ForegroundColor Cyan
exit $code
