<#
.SYNOPSIS
    Sul PC di sviluppo: test, publish self-contained win-x64, controlli e zip del pacchetto di installazione.

.DESCRIPTION
    Risultato: bin\package\FlexivManager-<versione>-win-x64.zip con
        app\                 programma pubblicato (exe, DLL, wwwroot, native\rdk-x.y\)
        install.ps1, update.ps1, uninstall.ps1, _common.ps1, INSTALLAZIONE.md
        vc_redist.x64.exe    solo se passato con -VcRedist

.EXAMPLE
    cd C:\Users\fabio\time\4_0\asp\FlexivManager
    powershell -ExecutionPolicy Bypass -File .\installer\build-package.ps1 -VcRedist C:\setup\vc_redist.x64.exe
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$VcRedist = '',
    [switch]$SkipTests
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'FlexivManager.csproj'
$publishDir = Join-Path $root 'bin\publish\win-x64'
$packageRoot = Join-Path $root 'bin\package'

if (-not $Version) {
    $Version = Get-Date -Format 'yyyy.MM.dd'
    $commit = $null
    try { $commit = (& git -C $root rev-parse --short HEAD 2>$null) } catch { }
    if ($commit) { $Version = "$Version-$commit" }
}
$name = "FlexivManager-$Version-win-x64"
$stage = Join-Path $packageRoot $name

if (-not $SkipTests) {
    Write-Host '==> Test' -ForegroundColor Cyan
    & dotnet test (Join-Path $root 'FlexivManager.slnx') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Test falliti: pacchetto non creato.' }
}

Write-Host '==> Publish self-contained win-x64' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
# Same settings as Properties\PublishProfiles\win-x64.pubxml (Visual Studio > Publish).
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:InformationalVersion=$Version
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish fallito.' }

Write-Host '==> Controlli' -ForegroundColor Cyan
$required = @('FlexivManager.exe', 'FlexivManager.dll', 'FlexivRdk.dll', 'appsettings.json', 'e_sqlite3.dll', 'wwwroot')
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $publishDir $f))) { throw "Manca $f nell'output di publish." }
}
$native = @(Get-ChildItem -Path (Join-Path $publishDir 'native') -Recurse -Filter 'flexiv_rdk.win64-vs2019.dll' -ErrorAction SilentlyContinue)
if ($native.Count -eq 0) { throw 'Manca native\rdk-x.y\flexiv_rdk.win64-vs2019.dll: compila il wrapper con third_party\build-native.ps1.' }
foreach ($n in $native) { Write-Host "    RDK nativo: $($n.Directory.Name)" }
foreach ($f in 'appsettings.Development.json', 'appsettings.Production.json') {
    if (Test-Path (Join-Path $publishDir $f)) { throw "$f non deve essere nel pacchetto." }
}
if (Test-Path (Join-Path $publishDir 'data')) { throw 'La cartella data\ non deve essere nel pacchetto (database di sviluppo).' }
Get-ChildItem -Path $publishDir -Filter '*.pdb' -Recurse | Remove-Item -Force
Write-Host '    OK'

Write-Host "==> Pacchetto $name" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -Path $publishDir -Destination (Join-Path $stage 'app') -Recurse
foreach ($f in 'install.ps1', 'update.ps1', 'uninstall.ps1', '_common.ps1', 'INSTALLAZIONE.md') {
    Copy-Item -Path (Join-Path $PSScriptRoot $f) -Destination $stage
}
if ($VcRedist) {
    if (-not (Test-Path $VcRedist)) { throw "VcRedist non trovato: $VcRedist" }
    Copy-Item -Path $VcRedist -Destination (Join-Path $stage 'vc_redist.x64.exe')
}
Set-Content -Path (Join-Path $stage 'VERSION.txt') -Encoding ASCII -Value "FlexivManager $Version`r`nbuilt $(Get-Date -Format 'yyyy-MM-dd HH:mm') on $env:COMPUTERNAME"

$zip = Join-Path $packageRoot "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host ''
Write-Host "Pacchetto pronto: $zip" -ForegroundColor Green
