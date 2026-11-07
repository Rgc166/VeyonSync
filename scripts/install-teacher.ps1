#requires -Version 5.1
# Instala ou atualiza o Veyon Sync THC — Professor. Preserva banco, histórico, certificado, pareamentos e mapas.
param(
    [string]$Lab = '',
    [int]$Port = 5443,
    [ValidateSet('ask', 'yes', 'no')][string]$DesktopShortcut = 'ask',
    [switch]$RotateCertificate,
    [switch]$ChangeLab
)
. "$PSScriptRoot\common.ps1"
$log = Start-VsLog 'instalar-professor'
$code = 0
try {
    Assert-VsAdmin
    Assert-VsPlatform
    $src = Join-Path (Split-Path $PSScriptRoot) 'Teacher'
    if (-not (Test-Path (Join-Path $src 'VeyonSync.Teacher.exe'))) { throw "Arquivos do professor não encontrados em $src. Rode o BUILD.bat ou use o pacote completo." }
    $target = Join-Path $script:VsRoot 'Teacher'
    $cfgPath = Join-Path $target 'appsettings.json'
    $data = Join-Path $script:VsData 'Teacher'
    $existing = Test-Path $cfgPath
    $old = $null
    if ($existing) { $old = Get-Content $cfgPath -Raw | ConvertFrom-Json }
    $legacyCfg = Join-Path $data 'config.json'
    if (-not $existing -and (Test-Path $legacyCfg)) {
        # Migração de versão de teste que guardava lab/porta/certificado em ProgramData\VeyonSync\Teacher\config.json.
        $l = Get-Content $legacyCfg -Raw | ConvertFrom-Json
        $old = [pscustomobject]@{ Lab = $l.lab; Port = $l.port; Tls = [pscustomobject]@{ Thumbprint = $l.thumbprint } }
        Write-VsLog "Configuração anterior encontrada em $legacyCfg (lab $($l.lab)); será migrada." 'Cyan'
    }

    Write-VsLog "Origem: $src"
    Write-VsLog ("Instalação existente: {0}" -f $(if ($existing) { "sim (versão $(Get-VsVersion (Join-Path $target 'VeyonSync.Teacher.exe')))" } else { 'não' }))

    if (-not $Lab) {
        $default = if ($old) { $old.Lab } else { 'B' }
        $Lab = Read-Host "Laboratório deste professor [$default]"
        if (-not $Lab) { $Lab = $default }
    }
    if ($Lab -notmatch '^[A-Za-z0-9_-]{1,32}$') { throw 'Laboratório inválido (use letras/números, ex.: A, B, C).' }
    if ($old -and $old.Lab -ne $Lab -and -not $ChangeLab) {
        throw "Este professor está configurado para o laboratório $($old.Lab). Para trocar para $Lab, rode novamente com -ChangeLab (ação administrativa explícita; os alunos precisarão de novo arquivo de ligação)."
    }
    if ($old -and $old.Port) { $Port = [int]$old.Port }

    $create = if ($DesktopShortcut -eq 'ask') { Read-VsYesNo 'Criar atalho na Área de Trabalho?' $true } else { $DesktopShortcut -eq 'yes' }

    Stop-VsApps 'Teacher'
    Stop-VsService 'VeyonSyncTeacher'
    $backup = Backup-VsInstall 'Teacher'

    try {
        Write-VsLog 'Copiando arquivos do programa (appsettings.json existente é preservado)...'
        Clear-VsAppFolder 'Teacher'
        Copy-VsTree $src $target -Exclude @('appsettings.json')
        Reset-VsProgramAcl 'Teacher'
        Protect-VsData 'Teacher'

        # Certificado TLS: emitido com o nome atual (SAN DNS). Renomear o PC não atualiza o certificado.
        $cert = $null
        if ($old -and $old.Tls.Thumbprint) { $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq $old.Tls.Thumbprint } | Select-Object -First 1 }
        $needNew = (-not $cert) -or $RotateCertificate
        if ($cert -and -not $RotateCertificate) {
            $names = @($cert.DnsNameList | ForEach-Object { $_.Unicode })
            if ($names -notcontains $env:COMPUTERNAME) {
                Write-VsLog "ATENÇÃO: o certificado atual vale para [$($names -join ', ')], mas este PC se chama $env:COMPUTERNAME." 'Yellow'
                if (Read-VsYesNo 'Gerar um novo certificado agora? (os alunos precisarão reimportar o novo arquivo de ligação; o pareamento é mantido)' $true) { $needNew = $true }
            }
            if ($cert.NotAfter -lt (Get-Date).AddDays(30)) {
                Write-VsLog "Certificado vence em $($cert.NotAfter); será renovado." 'Yellow'
                $needNew = $true
            }
        }
        if ($needNew) {
            $dns = @($env:COMPUTERNAME)
            try { $fqdn = [System.Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName; if ($fqdn -and $dns -notcontains $fqdn) { $dns += $fqdn } } catch { }
            $cert = New-SelfSignedCertificate -DnsName $dns -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(5) `
                -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -Type SSLServerAuthentication `
                -FriendlyName "Veyon Sync THC $env:COMPUTERNAME"
            Write-VsLog "Novo certificado: $($cert.Thumbprint) para $($dns -join ', ')" 'Cyan'
            Write-VsLog 'Gere um novo arquivo de ligação no THC (Credenciamento) e reimporte nos alunos.' 'Yellow'
        }
        $cfg = [ordered]@{ Lab = $Lab; Port = $Port; Tls = [ordered]@{ Thumbprint = $cert.Thumbprint }; Logging = @{ LogLevel = @{ Default = 'Warning' } } }
        ($cfg | ConvertTo-Json -Depth 5) | Set-Content -Path $cfgPath -Encoding UTF8
        Export-Certificate -Cert $cert -FilePath (Join-Path $data 'professor.cer') -Force | Out-Null

        # Chave privada do certificado: leitura pelo serviço (SYSTEM) já garantida em LocalMachine.
        Install-VsService 'Teacher' 'Veyon Sync THC - Professor'
        Remove-NetFirewallRule -DisplayName 'VeyonSync Professor' -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName 'VeyonSync Professor' -Direction Inbound -Protocol TCP -LocalPort $Port -RemoteAddress LocalSubnet -Profile Domain, Private -Action Allow | Out-Null
        Start-VsService 'VeyonSyncTeacher'
        & (Join-Path $target 'VeyonSync.Teacher.exe') --health-check | ForEach-Object { Write-VsLog $_ }
        $ok = $false
        for ($i = 0; $i -lt 10; $i++) { if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { $ok = $true; break }; Start-Sleep 2 }
        if (-not $ok) { throw "O serviço iniciou, mas não está escutando na porta $Port." }
    } catch {
        if ($backup) {
            Write-VsLog "Falha: $($_.Exception.Message)" 'Red'
            Stop-VsService 'VeyonSyncTeacher'
            Restore-VsProgram 'Teacher' $backup
            try { Start-VsService 'VeyonSyncTeacher' } catch { }
        }
        throw
    }

    Remove-VsLegacyLayout 'Teacher'
    Remove-VsShortcuts 'VeyonSync Teacher'
    New-VsShortcuts 'Teacher' 'Veyon Sync THC - Professor' $create
    Write-VsLog ''
    Write-VsLog '================================================' 'Green'
    Write-VsLog " Veyon Sync THC instalado. Laboratório $Lab, porta $Port." 'Green'
    Write-VsLog " Abra 'Veyon Sync THC - Professor' > Credenciamento > Gerar arquivo de ligação" 'Green'
    Write-VsLog ' e salve o .vsync na pasta Ligacoes do pendrive.' 'Green'
    Write-VsLog '================================================' 'Green'
} catch {
    Write-VsLog ("ERRO: " + $_.Exception.Message) 'Red'
    Write-VsLog ($_ | Out-String)
    $code = 1
}
Write-Host ''
Write-Host "Log desta instalação: $log" -ForegroundColor Cyan
exit $code
