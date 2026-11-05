<#
.SYNOPSIS
    Aggiorna FlexivManager gia' installato con la versione di questo pacchetto.

.DESCRIPTION
    - rifiuta se il robot sta lavorando (stato Running/Paused o ciclo attivo), salvo -Force;
    - ferma il servizio (all'arresto il gateway ferma il robot e chiude il ciclo come "Fermato");
    - copia i database in data\backup\pre-update-<data-ora>\;
    - salva il programma attuale in <InstallDir>.previous (per tornare indietro);
    - sostituisce i file del programma. NON tocca data\ ne' appsettings.Production.json;
    - riavvia il servizio e verifica che risponda.
    Il firewall viene riallineato alla porta del monitoraggio in appsettings.Production.json.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'FlexivManager'),
    [string]$ServiceName = 'FlexivManager',
    [string]$FirewallRemoteAddress = '',
    # Procede anche se il robot sta lavorando: l'arresto del servizio ferma il robot.
    [switch]$Force
)

. "$PSScriptRoot\_common.ps1"

Write-Step 'Controlli preliminari'
Assert-Admin
$appSource = Get-PackageAppFolder $PSScriptRoot
Unblock-Package $PSScriptRoot
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    throw "Servizio $ServiceName non trovato: per la prima installazione usa install.ps1."
}
if (-not (Test-Path (Join-Path $InstallDir $script:ExeName))) {
    throw "$InstallDir\$script:ExeName non trovato: indica la cartella giusta con -InstallDir."
}
Install-VcRuntimeIfNeeded $PSScriptRoot
Assert-RobotIdle -Force:$Force

$stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'

Write-Step 'Arresto del servizio'
Stop-GatewayService $ServiceName

# ---- Database backup (service stopped: files are consistent) ----------------------------------
Write-Step 'Backup dei database'
$dataDir = Join-Path $InstallDir 'data'
$dbFiles = @(Get-ChildItem -Path $dataDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '*.db' -or $_.Name -like '*.db-wal' -or $_.Name -like '*.db-shm' })
if ($dbFiles.Count -gt 0) {
    $backupDir = Join-Path $dataDir "backup\pre-update-$stamp"
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $dbFiles | Copy-Item -Destination $backupDir
    Write-Ok "Database copiati in $backupDir"
} else {
    Write-Note 'Nessun database da copiare.'
}

# ---- Keep the current program for a rollback --------------------------------------------------
Write-Step 'Copia della versione attuale'
$previous = "$InstallDir.previous"
& robocopy.exe $InstallDir $previous /MIR /XD (Join-Path $InstallDir 'data') /NFL /NDL /NJH /NJS /NP /R:2 /W:2 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copia della versione attuale fallita (robocopy codice $LASTEXITCODE). Nulla e' stato modificato: riavvia il servizio." }
Write-Ok "Versione attuale salvata in $previous (senza data\)."

# appsettings.json is replaced (new defaults); site settings belong in appsettings.Production.json.
$installedDefaults = Join-Path $InstallDir 'appsettings.json'
$newDefaults = Join-Path $appSource 'appsettings.json'
if ((Test-Path $installedDefaults) -and
    ((Get-FileHash $installedDefaults).Hash -ne (Get-FileHash $newDefaults).Hash)) {
    Write-Note "appsettings.json cambia con questa versione (vecchio in $previous). Le impostazioni del sito restano in $script:SiteConfigName."
}

# ---- Replace program files --------------------------------------------------------------------
Write-Step 'Aggiornamento del programma'
# /MIR removes files dropped by the new version; excluded items (data\, site config) are neither copied nor deleted.
& robocopy.exe $appSource $InstallDir /MIR /XD (Join-Path $InstallDir 'data') /XF $script:SiteConfigName /NFL /NDL /NJH /NJS /NP /R:3 /W:5 | Out-Null
if ($LASTEXITCODE -ge 8) {
    throw "Aggiornamento fallito (robocopy codice $LASTEXITCODE). Per tornare indietro: copia $previous su $InstallDir e avvia il servizio."
}
Write-Ok 'Programma aggiornato.'

# ---- Firewall (port may have changed in the site config) --------------------------------------
Write-Step 'Firewall'
$endpoint = Get-MonitoringEndpoint $InstallDir
if (-not $FirewallRemoteAddress) {
    $existing = Get-NetFirewallRule -Group $script:FirewallGroup -ErrorAction SilentlyContinue | Select-Object -First 1
    $FirewallRemoteAddress = 'Any'
    if ($existing) { $FirewallRemoteAddress = ((($existing | Get-NetFirewallAddressFilter).RemoteAddress) -join ',') }
}
Set-MonitoringFirewallRule $InstallDir $endpoint $FirewallRemoteAddress

# ---- Start and check --------------------------------------------------------------------------
Write-Step 'Avvio e verifica'
Start-GatewayService $ServiceName
if (Test-Url "$script:OperatorUrl/operator") { Write-Ok "Postazione operatore: $script:OperatorUrl/operator" }
else { Write-Warn "La postazione operatore non risponde. Visualizzatore eventi > Applicazione (origine $ServiceName). Per tornare indietro vedi INSTALLAZIONE.md." }

Write-Host ''
Write-Host 'Aggiornamento completato.' -ForegroundColor Green
