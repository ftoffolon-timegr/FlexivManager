<#
.SYNOPSIS
    Rimuove il servizio FlexivManager, la regola firewall, il collegamento e i file del programma.

.DESCRIPTION
    Per default TIENE data\ (database, backup, import gestionale) e appsettings.Production.json, cosi'
    una nuova installazione riparte con gli stessi dati e la stessa configurazione.
    -RemoveData cancella anche quelli (chiede conferma).
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'FlexivManager'),
    [string]$ServiceName = 'FlexivManager',
    [switch]$RemoveData,
    [switch]$Force
)

. "$PSScriptRoot\_common.ps1"

Write-Step 'Controlli preliminari'
Assert-Admin
Assert-RobotIdle -Force:$Force

Write-Step 'Servizio'
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-GatewayService $ServiceName
    & sc.exe delete $ServiceName | Out-Null
    Write-Ok "Servizio $ServiceName rimosso."
} else {
    Write-Note "Servizio $ServiceName non presente."
}

Write-Step 'Firewall e collegamento'
Get-NetFirewallRule -Group $script:FirewallGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Ok 'Regole firewall rimosse.'
$link = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) $script:ShortcutName
if (Test-Path $link) { Remove-Item $link -Force; Write-Ok 'Collegamento rimosso.' }

Write-Step "File in $InstallDir"
if (-not (Test-Path $InstallDir)) {
    Write-Note 'Cartella non presente.'
} elseif ($RemoveData) {
    $answer = Read-Host "    Cancello DEFINITIVAMENTE anche database e configurazione in $InstallDir? Scrivi SI per confermare"
    if ($answer -ne 'SI') { throw 'Annullato: nessun file cancellato.' }
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Ok "$InstallDir cancellata."
} else {
    Get-ChildItem -Path $InstallDir -Force |
        Where-Object { $_.Name -ne 'data' -and $_.Name -ne $script:SiteConfigName } |
        Remove-Item -Recurse -Force
    Write-Ok "Programma rimosso. Tenuti: $InstallDir\data e $InstallDir\$script:SiteConfigName"
}
if (Test-Path "$InstallDir.previous") {
    Remove-Item -Path "$InstallDir.previous" -Recurse -Force
    Write-Ok "Rimossa la copia $InstallDir.previous"
}

Write-Host ''
Write-Host 'Disinstallazione completata.' -ForegroundColor Green
