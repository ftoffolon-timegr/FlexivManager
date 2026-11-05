# Shared helpers for install.ps1 / update.ps1 / uninstall.ps1 (Windows PowerShell 5.1 compatible).
# Dot-sourced: . "$PSScriptRoot\_common.ps1"

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:ExeName        = 'FlexivManager.exe'
$script:SiteConfigName = 'appsettings.Production.json'
$script:FirewallGroup  = 'FlexivManager'
$script:OperatorUrl    = 'http://127.0.0.1:8081'
$script:ShortcutName   = 'Postazione operatore robot.url'

function Write-Step([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "    OK  $Text" -ForegroundColor Green }
function Write-Note([string]$Text) { Write-Host "    $Text" }
function Write-Warn([string]$Text) { Write-Host "    ATTENZIONE: $Text" -ForegroundColor Yellow }

function Assert-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Esegui PowerShell come amministratore (tasto destro > Esegui come amministratore).'
    }
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'Serve Windows a 64 bit (x64).'
    }
}

function Get-PackageAppFolder([string]$ScriptRoot) {
    $app = Join-Path $ScriptRoot 'app'
    if (-not (Test-Path (Join-Path $app $script:ExeName))) {
        throw "Cartella del programma non trovata: $app\$script:ExeName. Lancia lo script dalla cartella del pacchetto estratto dallo zip."
    }
    return $app
}

# Files extracted from a downloaded zip carry the "downloaded from Internet" mark: remove it.
function Unblock-Package([string]$ScriptRoot) {
    Get-ChildItem -Path $ScriptRoot -Recurse -File | Unblock-File
}

# Microsoft Visual C++ 2015-2022 x64 runtime: needed by the Flexiv RDK native wrapper (not by the simulator).
function Test-VcRuntime {
    $key = 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
    if (-not (Test-Path $key)) { return $false }
    $installed = (Get-ItemProperty $key -ErrorAction SilentlyContinue).Installed
    return ($installed -eq 1)
}

function Install-VcRuntimeIfNeeded([string]$ScriptRoot) {
    if (Test-VcRuntime) { Write-Ok 'Runtime Visual C++ x64 presente.'; return }
    $redist = Join-Path $ScriptRoot 'vc_redist.x64.exe'
    if (Test-Path $redist) {
        Write-Note 'Installo il runtime Visual C++ x64...'
        $p = Start-Process -FilePath $redist -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        # 0 = ok, 3010 = ok, restart required, 1638 = newer version already installed
        if ($p.ExitCode -in 0, 3010, 1638) { Write-Ok "Runtime Visual C++ installato (codice $($p.ExitCode))." }
        else { Write-Warn "vc_redist.x64.exe ha restituito $($p.ExitCode): il collegamento al robot reale potrebbe non funzionare." }
    } else {
        Write-Warn 'Runtime Visual C++ 2015-2022 x64 non trovato. Serve per il robot reale (non per il simulatore).'
        Write-Note 'Scaricalo da https://aka.ms/vs/17/release/vc_redist.x64.exe oppure mettilo accanto a install.ps1 e rilancia.'
    }
}

function Get-SiteSettings([string]$InstallDir) {
    $path = Join-Path $InstallDir $script:SiteConfigName
    if (-not (Test-Path $path)) { return $null }
    return (Get-Content -Raw -Encoding UTF8 $path | ConvertFrom-Json)
}

# Monitoring endpoint from appsettings.Production.json (written by install.ps1 without comments).
function Get-MonitoringEndpoint([string]$InstallDir) {
    $address = '127.0.0.1'; $port = 8080
    $site = Get-SiteSettings $InstallDir
    if ($site -and $site.PSObject.Properties['Hosts'] -and $site.Hosts.PSObject.Properties['Monitoring']) {
        $m = $site.Hosts.Monitoring
        if ($m.PSObject.Properties['Address'] -and $m.Address) { $address = [string]$m.Address }
        if ($m.PSObject.Properties['Port'] -and $m.Port) { $port = [int]$m.Port }
    }
    return [pscustomobject]@{ Address = $address; Port = $port }
}

function Get-LocalTestUrl($Endpoint) {
    $host_ = $Endpoint.Address
    if ($host_ -eq '0.0.0.0') { $host_ = '127.0.0.1' }
    return "http://$($host_):$($Endpoint.Port)"
}

function Test-Url([string]$Url, [int]$TimeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 400) { return $true }
        } catch { }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return $false
}

# Robot state from the local read-only API. $null when the service does not answer.
function Get-RobotStatus {
    try { return Invoke-RestMethod -Uri "$script:OperatorUrl/api/status" -UseBasicParsing -TimeoutSec 5 }
    catch { return $null }
}

function Assert-RobotIdle([switch]$Force) {
    $status = Get-RobotStatus
    if ($null -eq $status) { Write-Note 'Servizio non raggiungibile: nessun ciclo in corso da controllare.'; return }
    $busy = ($status.state -in 'Running', 'Paused') -or ($null -ne $status.activeCycle)
    if (-not $busy) { Write-Ok "Robot non in lavorazione (stato: $($status.state))."; return }
    if ($Force) { Write-Warn "Robot in stato $($status.state): procedo comunque (-Force). L'arresto del servizio ferma il robot."; return }
    throw "Il robot sta lavorando (stato: $($status.state)). Aspetta la fine del ciclo o premi STOP, poi rilancia. (-Force per procedere comunque: l'arresto del servizio ferma il robot.)"
}

function Stop-GatewayService([string]$ServiceName) {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc -or $svc.Status -eq 'Stopped') { return }
    Write-Note "Arresto il servizio $ServiceName..."
    Stop-Service -Name $ServiceName -Force
    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    Write-Ok 'Servizio fermo.'
}

function Start-GatewayService([string]$ServiceName) {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    Write-Ok "Servizio $ServiceName avviato."
}

# Inbound rule for the monitoring host only. The operator host listens on 127.0.0.1 and needs no rule.
function Set-MonitoringFirewallRule([string]$InstallDir, $Endpoint, [string]$RemoteAddress) {
    Get-NetFirewallRule -Group $script:FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if ($Endpoint.Address -like '127.*') {
        Write-Note 'Monitoraggio solo in locale (127.0.0.1): nessuna regola firewall.'
        return
    }
    New-NetFirewallRule -DisplayName "FlexivManager - monitoraggio (TCP $($Endpoint.Port))" `
        -Group $script:FirewallGroup -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort $Endpoint.Port -Program (Join-Path $InstallDir $script:ExeName) `
        -Profile Domain, Private -RemoteAddress $RemoteAddress | Out-Null
    Write-Ok "Regola firewall in ingresso TCP $($Endpoint.Port) (profili Dominio e Privato, da: $RemoteAddress)."
}

# appsettings.Production.json holds the API key and the password hash: readable only by SYSTEM and Administrators.
function Protect-SiteConfig([string]$InstallDir) {
    $path = Join-Path $InstallDir $script:SiteConfigName
    & icacls.exe $path /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warn "icacls non ha protetto $path (codice $LASTEXITCODE)." }
}

function Write-JsonFile([string]$Path, $Object) {
    $json = $Object | ConvertTo-Json -Depth 10
    # UTF-8 without BOM
    [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding($false)))
}
