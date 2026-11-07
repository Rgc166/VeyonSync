#requires -Version 5.1
# Compila e monta a pasta do pendrive. Requer .NET SDK 10 (somente na máquina que compila).
param([switch]$SkipTests, [string]$Version = '')
$ErrorActionPreference = 'Stop'
$r = Split-Path $PSScriptRoot
$out = Join-Path $r 'dist\Pendrive\VeyonSync'
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $r 'Directory.Build.props'))).Project.PropertyGroup.VeyonSyncVersion.'#text' }
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $r 'Directory.Build.props'))).Project.PropertyGroup.VeyonSyncVersion }
$ver = "$Version".Trim()
if ($ver -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Versão inválida: '$ver'" }
$vp = "-p:VeyonSyncVersion=$ver"
$log = Join-Path $r 'build-log.txt'
Set-Content $log "Veyon Sync $ver - build $(Get-Date)" -Encoding UTF8
Write-Host "Veyon Sync $ver - build" -ForegroundColor Cyan

if (-not $SkipTests -and (Test-Path "$r\tests\VeyonSync.Tests\VeyonSync.Tests.csproj")) {
    Write-Host 'Executando testes automatizados...' -ForegroundColor Cyan
    dotnet test "$r\tests\VeyonSync.Tests\VeyonSync.Tests.csproj" -c Release $vp | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) { throw 'Testes falharam. Pacote não gerado.' }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$pub = @(
    @{ P = 'Teacher\Teacher.csproj';       O = 'Teacher' },
    @{ P = 'TeacherApp\TeacherApp.csproj'; O = 'Teacher\App' },
    @{ P = 'Student\Student.csproj';       O = 'Student' },
    @{ P = 'StudentApp\StudentApp.csproj'; O = 'Student\App' }
)
foreach ($x in $pub) {
    Write-Host "Publicando $($x.P)..." -ForegroundColor Cyan
    dotnet publish (Join-Path $r $x.P) -c Release -r win-x64 --self-contained true -p:DebugType=none $vp -o (Join-Path $out $x.O) | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar $($x.P)" }
}

# Ícones definitivos: se existirem em assets\icons, vão junto (icon.ico ao lado de cada interface).
foreach ($i in @(@{ F = 'thc.ico'; D = 'Teacher\App' }, @{ F = 'std.ico'; D = 'Student\App' })) {
    $ico = Join-Path $r "assets\icons\$($i.F)"
    if (Test-Path $ico) { Copy-Item $ico (Join-Path $out "$($i.D)\icon.ico") -Force }
}

# Scripts (UTF-8 com BOM para o PowerShell 5.1), menu e documentação.
New-Item -ItemType Directory -Force "$out\scripts", "$out\Ligacoes", "$out\Atualizacoes", "$out\logs" | Out-Null
$bom = New-Object System.Text.UTF8Encoding($true)
Get-ChildItem "$r\scripts\*.ps1" | Where-Object { $_.Name -ne 'build.ps1' } | ForEach-Object {
    [IO.File]::WriteAllText((Join-Path "$out\scripts" $_.Name), [IO.File]::ReadAllText($_.FullName), $bom)
}
Copy-Item "$r\pendrive\INICIAR.bat" $out -Force
Copy-Item "$r\docs\TUTORIAL.txt" (Join-Path $out 'LEIA-ME.txt') -Force
Copy-Item "$r\docs\ATUALIZACOES.txt" (Join-Path $out 'ATUALIZACOES.txt') -Force
Copy-Item "$r\docs\GUIA-INSTALACAO.md" (Join-Path $out 'GUIA-INSTALACAO.md') -Force
Copy-Item "$r\docs\GUIA-ATUALIZACAO.md" (Join-Path $out 'GUIA-ATUALIZACAO.md') -Force
Copy-Item "$r\pendrive\Ligacoes-LEIA-ME.txt" (Join-Path $out 'Ligacoes\LEIA-ME.txt') -Force
Copy-Item "$r\pendrive\Atualizacoes-LEIA-ME.txt" (Join-Path $out 'Atualizacoes\LEIA-ME.txt') -Force
# Kit de prevenção/recuperação da atualização por arquivo (somente scripts).
if (Test-Path "$r\recuperacao") {
    New-Item -ItemType Directory -Force "$out\Recuperacao" | Out-Null
    Get-ChildItem "$r\recuperacao" -File | ForEach-Object {
        if ($_.Extension -eq '.ps1') { [IO.File]::WriteAllText((Join-Path "$out\Recuperacao" $_.Name), [IO.File]::ReadAllText($_.FullName), $bom) }
        else { Copy-Item $_.FullName (Join-Path "$out\Recuperacao" $_.Name) -Force }
    }
}

# Pacote de atualização do STD pela rede (assinado depois pelo THC ao distribuir).
$stage = Join-Path $r 'dist\stage-std'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $out 'Student') (Join-Path $stage 'Service') -Recurse
Remove-Item (Join-Path $stage 'Service\App') -Recurse -Force
Copy-Item (Join-Path $out 'Student\App') (Join-Path $stage 'App') -Recurse
(@{ product = 'STD'; version = $ver; minVersion = '2.0.0' } | ConvertTo-Json) | Set-Content (Join-Path $stage 'update.json') -Encoding UTF8
$zip = Join-Path $out "Atualizacoes\STD-$ver.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force

Get-ChildItem $out -Recurse -File | Unblock-File
$hash = Get-ChildItem $out -Recurse -File -Include *.exe, *.zip | Get-FileHash -Algorithm SHA256
$hash | ForEach-Object { "{0}  {1}" -f $_.Hash, $_.Path.Substring($out.Length + 1) } | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding UTF8
Write-Host ''
Write-Host "Pronto: $out" -ForegroundColor Green
Write-Host 'Copie a pasta VeyonSync inteira para o pendrive.' -ForegroundColor Green
