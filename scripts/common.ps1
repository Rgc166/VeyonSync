# Funções compartilhadas pelos instaladores do Veyon Sync (Windows PowerShell 5.1+).
# Salvo em UTF-8 com BOM para os acentos aparecerem corretamente no PowerShell 5.1.

$ErrorActionPreference = 'Stop'
$script:VsRoot = Join-Path $env:ProgramFiles 'VeyonSync'
$script:VsData = Join-Path $env:ProgramData 'VeyonSync'

function Start-VsLog([string]$Name) {
    # Log TXT gerado mesmo em caso de falha. Tenta ao lado do instalador (pendrive); se não der, em ProgramData.
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $dirs = @((Join-Path (Split-Path $PSScriptRoot) 'logs'), (Join-Path $script:VsData 'logs'))
    foreach ($d in $dirs) {
        try {
            New-Item -ItemType Directory -Force -Path $d | Out-Null
            $f = Join-Path $d "$Name`_$($env:COMPUTERNAME)_$stamp.txt"
            Set-Content -Path $f -Value "Veyon Sync - $Name - $env:COMPUTERNAME - $(Get-Date)" -Encoding UTF8
            $script:VsLog = $f
            return $f
        } catch { }
    }
    throw 'Não foi possível criar o arquivo de log.'
}

function Write-VsLog([string]$Text, [string]$Color = 'Gray') {
    Write-Host $Text -ForegroundColor $Color
    if ($script:VsLog) { Add-Content -Path $script:VsLog -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $Text) -Encoding UTF8 }
}

function Assert-VsAdmin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Execute como administrador (o INICIAR.bat pede a elevação automaticamente).'
    }
}

function Assert-VsPlatform {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'O Veyon Sync exige Windows 64 bits.' }
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 17763) { throw "Windows build $build não suportado pelo .NET 10. Use Windows 10 1809 ou mais recente (recomendado 22H2) ou Windows 11." }
}

function Stop-VsService([string]$Name) {
    $s = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($s -and $s.Status -ne 'Stopped') {
        Write-VsLog "Parando serviço $Name..."
        Stop-Service -Name $Name -Force
        $s.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
}

function Stop-VsApps([string]$Role) {
    Get-Process -Name "VeyonSync.$Role.App" -ErrorAction SilentlyContinue | ForEach-Object {
        Write-VsLog "Fechando interface $($_.ProcessName) (PID $($_.Id))."
        $_ | Stop-Process -Force
    }
}

function Copy-VsTree([string]$From, [string]$To, [string[]]$Exclude = @()) {
    New-Item -ItemType Directory -Force -Path $To | Out-Null
    Get-ChildItem -Path $From -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($From.TrimEnd('\').Length + 1)
        if ($Exclude -contains $_.Name) { return }
        $dest = Join-Path $To $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
    }
}

function Reset-VsProgramAcl([string]$Role) {
    # Programas em Program Files usam as permissões padrão (leitura para usuários), senão os atalhos falham.
    # Uma versão de teste anterior restringia esta pasta; os dados em ProgramData continuam protegidos.
    $p = Join-Path $script:VsRoot $Role
    if (Test-Path $p) { & icacls.exe $p /reset /T /C /Q | Out-Null }
}

function Clear-VsAppFolder([string]$Role) {
    # A pasta App só contém a interface (nenhum dado): é recriada para não misturar arquivos de versões antigas.
    $app = Join-Path (Join-Path $script:VsRoot $Role) 'App'
    if (Test-Path $app) { Remove-Item -Recurse -Force $app }
    foreach ($f in @('VeyonSync.Core.dll', 'VeyonSync.Core.pdb')) {
        $x = Join-Path (Join-Path $script:VsRoot $Role) $f
        if (Test-Path $x) { Remove-Item -Force $x }
    }
}

function Backup-VsInstall([string]$Role) {
    $target = Join-Path $script:VsRoot $Role
    if (-not (Test-Path $target)) { return $null }
    $b = Join-Path $script:VsData ("backups\{0}-{1}" -f $Role, (Get-Date -Format 'yyyyMMdd_HHmmss'))
    Write-VsLog "Backup dos programas: $b"
    Copy-VsTree $target (Join-Path $b 'programa')
    $data = Join-Path $script:VsData $Role
    if (Test-Path $data) {
        # Cópia dos dados (banco, configuração, estado). O original nunca é apagado.
        Copy-VsTree $data (Join-Path $b 'dados')
    }
    return $b
}

function Restore-VsProgram([string]$Role, [string]$Backup) {
    $target = Join-Path $script:VsRoot $Role
    $src = Join-Path $Backup 'programa'
    if (-not (Test-Path $src)) { throw "Backup ausente: $src" }
    Write-VsLog "Restaurando versão anterior a partir de $Backup" 'Yellow'
    Get-ChildItem -Path $target -Recurse -File | Where-Object { $_.Name -ne 'appsettings.json' } | Remove-Item -Force -ErrorAction SilentlyContinue
    Copy-VsTree $src $target
}

function Protect-VsData([string]$Role) {
    $data = Join-Path $script:VsData $Role
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    # Somente SYSTEM e Administradores (SIDs: independente do idioma do Windows).
    & icacls.exe $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
}

function Install-VsService([string]$Role, [string]$DisplayName) {
    $name = "VeyonSync$Role"
    $exe = Join-Path (Join-Path $script:VsRoot $Role) "VeyonSync.$Role.exe"
    if (-not (Get-Service -Name $name -ErrorAction SilentlyContinue)) {
        Write-VsLog "Registrando serviço $name"
        New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName $DisplayName -StartupType Automatic | Out-Null
    } else {
        # Também corrige o caminho de versões anteriores que usavam a subpasta Service\.
        & sc.exe config $name binPath= "`"$exe`"" start= auto DisplayName= $DisplayName | Out-Null
    }
    & sc.exe description $name "$DisplayName - funciona antes do login e com a interface fechada." | Out-Null
    & sc.exe failure $name reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
}

function Start-VsService([string]$Name) {
    Write-VsLog "Iniciando serviço $Name..."
    Start-Service -Name $Name
    $s = Get-Service -Name $Name
    $s.WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
    Start-Sleep -Seconds 3
    $s.Refresh()
    if ($s.Status -ne 'Running') { throw "Serviço $Name não permaneceu em execução. Veja o Visualizador de Eventos (Aplicativo)." }
}

function New-VsShortcuts([string]$Role, [string]$Title, [bool]$Desktop) {
    $app = Join-Path (Join-Path $script:VsRoot $Role) "App\VeyonSync.$Role.App.exe"
    $icon = Join-Path (Join-Path $script:VsRoot $Role) 'App\icon.ico'
    $shell = New-Object -ComObject WScript.Shell
    $places = @([Environment]::GetFolderPath('CommonPrograms'))
    if ($Desktop) { $places += [Environment]::GetFolderPath('CommonDesktopDirectory') }
    foreach ($p in $places) {
        $lnk = Join-Path $p "$Title.lnk"
        $s = $shell.CreateShortcut($lnk)
        $s.TargetPath = $app   # sempre a instalação local, nunca o pendrive
        $s.WorkingDirectory = Split-Path $app
        $s.Description = $Title
        if (Test-Path $icon) { $s.IconLocation = "$icon,0" } else { $s.IconLocation = "$app,0" }
        $s.Save()
        Write-VsLog "Atalho: $lnk"
    }
}

function Remove-VsShortcuts([string]$Title) {
    foreach ($p in @([Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('CommonDesktopDirectory'))) {
        $lnk = Join-Path $p "$Title.lnk"
        if (Test-Path $lnk) { Remove-Item $lnk -Force }
    }
}

function Read-VsYesNo([string]$Question, [bool]$Default = $true) {
    $hint = if ($Default) { '[S/n]' } else { '[s/N]' }
    $a = Read-Host "$Question $hint"
    if ([string]::IsNullOrWhiteSpace($a)) { return $Default }
    return $a.Trim().ToUpper().StartsWith('S')
}

function Get-VsVersion([string]$Exe) {
    if (Test-Path $Exe) { return (Get-Item $Exe).VersionInfo.ProductVersion }
    return $null
}

function Remove-VsLegacyLayout([string]$Role) {
    # Versões de teste anteriores instalavam o serviço em <Role>\Service. Depois de migrar, a pasta antiga sai.
    $legacy = Join-Path (Join-Path $script:VsRoot $Role) 'Service'
    if (Test-Path (Join-Path $legacy "VeyonSync.$Role.exe")) {
        Write-VsLog "Removendo layout antigo: $legacy"
        Remove-Item -Recurse -Force $legacy -ErrorAction SilentlyContinue
    }
}
