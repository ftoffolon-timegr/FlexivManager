<#
.SYNOPSIS
    Prima installazione di FlexivManager come servizio Windows sul PC del cliente.

.DESCRIPTION
    - copia il programma in InstallDir (default C:\Program Files\FlexivManager);
    - crea data\, data\backup\ e la cartella di import del gestionale;
    - scrive appsettings.Production.json con le impostazioni del sito (mai sovrascritto dagli aggiornamenti);
    - registra il servizio "FlexivManager" con avvio automatico (ritardato) e riavvio in caso di errore;
    - apre nel firewall solo la porta del monitoraggio (la postazione operatore resta su 127.0.0.1:8081);
    - crea sul desktop pubblico il collegamento alla postazione operatore.
    Nessun movimento del robot parte all'avvio del servizio.

.EXAMPLE
    # Simulatore (prova del pacchetto), chiede indirizzo e password
    .\install.ps1

.EXAMPLE
    # Robot reale
    .\install.ps1 -MonitoringAddress 10.0.0.25 -RobotSerial "Rizon10-012345" -RdkVersion 1.8 `
                  -RobotNetworkInterface 192.168.2.150 -ErpImportFolder "\\server\gestionale\flexiv"
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'FlexivManager'),
    [string]$ServiceName = 'FlexivManager',

    # IP della scheda di rete aziendale su cui ascolta il monitoraggio; 0.0.0.0 = tutte. Se omesso viene chiesto.
    [string]$MonitoringAddress,
    [int]$MonitoringPort = 8090,
    # Chi puo' raggiungere il monitoraggio: Any, LocalSubnet o elenco di IP/subnet (es. "10.0.0.0/24,10.0.5.12").
    [string]$FirewallRemoteAddress = 'Any',

    # Cartella in cui il gestionale scrive i file dei codici. Vuoto = data\erp-import (cartella locale).
    [string]$ErpImportFolder = '',

    # Numero di serie del robot (es. "Rizon10-012345"). Vuoto = simulatore (Robot:Backend = Mock).
    [string]$RobotSerial = '',
    [ValidateSet('1.8', '1.9.4.1')]
    [string]$RdkVersion = '1.8',
    # IP di questo PC sulla rete del robot (facoltativo).
    [string]$RobotNetworkInterface = '',

    # Chiave per l'API REST di scrittura (header X-Api-Key). Vuoto = generata a caso e mostrata alla fine.
    [string]$ApiKey = '',
    # Nessuna password per il pianificatore: pianificazione in sola lettura finche' non la imposti.
    [switch]$NoEditorPassword,

    [switch]$NoShortcut,
    [switch]$NoStart
)

. "$PSScriptRoot\_common.ps1"

Write-Step 'Controlli preliminari'
Assert-Admin
$appSource = Get-PackageAppFolder $PSScriptRoot
Unblock-Package $PSScriptRoot
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Il servizio $ServiceName esiste gia': per aggiornare usa update.ps1, per reinstallare prima uninstall.ps1."
}
Write-Ok "Pacchetto: $appSource"
Install-VcRuntimeIfNeeded $PSScriptRoot

# ---- Monitoring address -----------------------------------------------------------------------
if (-not $MonitoringAddress) {
    Write-Step 'Indirizzo del monitoraggio (rete aziendale)'
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        ForEach-Object { Write-Note ("{0,-16} {1}" -f $_.IPAddress, $_.InterfaceAlias) }
    Write-Note "Scrivi l'IP della scheda collegata alla rete aziendale (NON quella del robot)."
    $MonitoringAddress = Read-Host '    IP [invio = 0.0.0.0, tutte le schede]'
    if (-not $MonitoringAddress) { $MonitoringAddress = '0.0.0.0' }
}
$parsed = $null
if (-not [System.Net.IPAddress]::TryParse($MonitoringAddress, [ref]$parsed)) {
    throw "MonitoringAddress '$MonitoringAddress' non e' un indirizzo IP valido."
}

# ---- Copy program -----------------------------------------------------------------------------
Write-Step "Copia del programma in $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
# /E: subfolders; data\ and appsettings.Production.json are never in the package, so existing ones are kept.
& robocopy.exe $appSource $InstallDir /E /NFL /NDL /NJH /NJS /NP /R:2 /W:2 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copia fallita (robocopy codice $LASTEXITCODE)." }
Write-Ok 'Programma copiato.'

# ---- Data folders -----------------------------------------------------------------------------
Write-Step 'Cartelle dati'
foreach ($d in 'data', 'data\backup') {
    New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir $d) | Out-Null
}
Write-Ok "$InstallDir\data (database SQLite) e data\backup"
if (-not $ErpImportFolder) {
    New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir 'data\erp-import') | Out-Null
    Write-Ok 'Import gestionale: data\erp-import (cartella locale, da sostituire con la condivisione del gestionale).'
} elseif ($ErpImportFolder.StartsWith('\\')) {
    if (Test-Path $ErpImportFolder) { Write-Ok "Import gestionale: $ErpImportFolder raggiungibile da questo utente." }
    else { Write-Warn "$ErpImportFolder non raggiungibile ora. Il servizio gira come LocalSystem: sulla condivisione serve il permesso per l'account computer (DOMINIO\$env:COMPUTERNAME`$) oppure un account di servizio dedicato (vedi INSTALLAZIONE.md)." }
} else {
    New-Item -ItemType Directory -Force -Path $ErpImportFolder | Out-Null
    Write-Ok "Import gestionale: $ErpImportFolder"
}

# ---- Site configuration -----------------------------------------------------------------------
Write-Step 'Configurazione del sito (appsettings.Production.json)'
$sitePath = Join-Path $InstallDir $script:SiteConfigName
$generatedKey = $null
if (Test-Path $sitePath) {
    Write-Warn "$sitePath esiste gia' (installazione precedente): lo tengo cosi' com'e'. I parametri passati a questo script non lo modificano."
} else {
    $passwordHash = ''
    if (-not $NoEditorPassword) {
        $secure = Read-Host '    Password del pianificatore (utente "planner") per il monitoraggio' -AsSecureString
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
        if ($plain) {
            $passwordHash = (& (Join-Path $InstallDir $script:ExeName) hash-password $plain | Select-Object -Last 1).Trim()
            if ($LASTEXITCODE -ne 0 -or -not $passwordHash.StartsWith('PBKDF2')) { throw "Calcolo dell'hash della password fallito." }
        } else {
            Write-Warn 'Password vuota: pianificazione in sola lettura finche'' non imposti Hosts:Monitoring:Editor:PasswordHash.'
        }
        $plain = $null
    }

    if (-not $ApiKey) {
        $bytes = New-Object byte[] 32
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $ApiKey = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $generatedKey = $ApiKey
    }

    $flexiv = [ordered]@{ RdkVersion = $RdkVersion }
    if ($RobotSerial) { $flexiv.SerialNumber = $RobotSerial }
    if ($RobotNetworkInterface) { $flexiv.NetworkInterfaces = @($RobotNetworkInterface) }

    $erp = [ordered]@{}
    if ($ErpImportFolder) { $erp.ImportFolder = $ErpImportFolder }

    $site = [ordered]@{
        Hosts = [ordered]@{
            Monitoring = [ordered]@{
                Address = $MonitoringAddress
                Port    = $MonitoringPort
                Editor  = [ordered]@{ PasswordHash = $passwordHash }
                Api     = [ordered]@{ Key = $ApiKey }
            }
        }
        Erp   = $erp
        Robot = [ordered]@{
            Backend = $(if ($RobotSerial) { 'Flexiv' } else { 'Mock' })
            Flexiv  = $flexiv
        }
    }
    Write-JsonFile $sitePath $site
    Protect-SiteConfig $InstallDir
    Write-Ok "Scritto $sitePath (leggibile solo da SYSTEM e Administrators)."
    if (-not $RobotSerial) { Write-Warn 'Nessun -RobotSerial: il servizio usa il SIMULATORE. Per il robot reale vedi INSTALLAZIONE.md.' }
}

# ---- Windows service --------------------------------------------------------------------------
Write-Step "Servizio Windows $ServiceName"
$exe = Join-Path $InstallDir $script:ExeName
New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'Flexiv Manager (gateway robot)' `
    -Description 'Gateway del cobot Flexiv: postazione operatore (127.0.0.1:8081) e monitoraggio/pianificazione.' `
    -StartupType Automatic | Out-Null
# Delayed start: network and shares are ready. Restart after a crash (no motion is ever started automatically).
& sc.exe config $ServiceName start= delayed-auto | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
Write-Ok 'Servizio registrato: avvio automatico ritardato, riavvio automatico in caso di errore.'

# ---- Firewall ---------------------------------------------------------------------------------
Write-Step 'Firewall'
$endpoint = Get-MonitoringEndpoint $InstallDir
Set-MonitoringFirewallRule $InstallDir $endpoint $FirewallRemoteAddress

# ---- Shortcut ---------------------------------------------------------------------------------
if (-not $NoShortcut) {
    $desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
    $link = Join-Path $desktop $script:ShortcutName
    Set-Content -Path $link -Encoding ASCII -Value "[InternetShortcut]`r`nURL=$script:OperatorUrl/operator`r`n"
    Write-Ok "Collegamento sul desktop: $link"
}

# ---- Start and check --------------------------------------------------------------------------
if ($NoStart) {
    Write-Step 'Fine (servizio non avviato: -NoStart)'
} else {
    Write-Step 'Avvio e verifica'
    Start-GatewayService $ServiceName
    if (Test-Url "$script:OperatorUrl/operator") { Write-Ok "Postazione operatore: $script:OperatorUrl/operator" }
    else { Write-Warn "La postazione operatore non risponde. Guarda il Visualizzatore eventi > Registri di Windows > Applicazione (origine $ServiceName)." }
    $monitoringUrl = Get-LocalTestUrl $endpoint
    if (Test-Url "$monitoringUrl/api/status" 20) { Write-Ok "Monitoraggio: http://$($endpoint.Address):$($endpoint.Port)/" }
    else { Write-Warn "Il monitoraggio non risponde su $monitoringUrl (porta occupata? indirizzo sbagliato?)." }
}

Write-Host ''
Write-Host 'Installazione completata.' -ForegroundColor Green
if ($generatedKey) {
    Write-Host ''
    Write-Host 'Chiave API REST (header X-Api-Key) generata, da consegnare a chi integra il gestionale:' -ForegroundColor Yellow
    Write-Host "    $generatedKey"
    Write-Host "    E' salvata solo in $sitePath."
}
