<#
.SYNOPSIS
  Builds the native Flexiv RDK C# wrapper DLL for one RDK version (x64 Release) and copies it to
  src/RobotGateway.Flexiv/native/rdk-<major.minor>/.

.DESCRIPTION
  1. Clones flexiv_rdk at the given tag (the static RDK library is downloaded by its CMake with SHA256 check).
  2. Builds its dependencies (Eigen, spdlog, tinyxml2, foonathan_memory, Fast-CDR, Fast-DDS) as static libs.
  3. Installs flexiv_rdk, then builds third_party/flexiv_rdk_csharp against it.
  Needs Visual Studio with the C++ desktop workload (MSVC + CMake) and Git for Windows (bash).
  Robot software 3.10 -> RDK v1.8, 3.11 -> RDK v1.9 (see the Flexiv compatibility table).

.EXAMPLE
  ./third_party/build-native.ps1 -RdkTag v1.8
#>
param(
    [string]$RdkTag = "v1.8",
    # Kept short and outside the repo: Fast-DDS builds produce very long paths.
    [string]$WorkDir = (Join-Path $env:USERPROFILE "fxrdk"),
    [int]$Jobs = 8,
    # Network failures while cloning the dependencies are retried.
    [int]$Retries = 3,
    [int]$RetryDelaySeconds = 120,
    # Eigen's home (gitlab.com) often refuses clones under load: Eigen is pre-cloned from this mirror instead.
    # The checkout is accepted only if it is the official 3.4.0 commit, so the mirror does not need to be trusted.
    [string]$EigenUrl = "https://github.com/eigen-mirror/eigen.git"
)
$EigenTag = "3.4.0"                                          # version pinned by flexiv_rdk's install_eigen.sh
$EigenCommit = "3147391d946bb4b6c68edd901f2add6ac1f31f8c"    # official Eigen 3.4.0 commit
$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot -Parent
$version = ($RdkTag.TrimStart("v") -split "\.")[0..1] -join "."
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -prerelease -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio with the C++ desktop workload (MSVC x64) not found." }
$cmakeBin = Join-Path $vs "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin"
$bash = "C:\Program Files\Git\bin\bash.exe"
if (-not (Test-Path "$cmakeBin\cmake.exe")) { throw "CMake from Visual Studio not found in $cmakeBin." }
if (-not (Test-Path $bash)) { throw "Git for Windows bash not found: $bash" }

$env:PATH = "$cmakeBin;$env:PATH"
# Long paths for the clones made by the dependency scripts, without touching the user's git config.
$env:GIT_CONFIG_COUNT = "1"; $env:GIT_CONFIG_KEY_0 = "core.longpaths"; $env:GIT_CONFIG_VALUE_0 = "true"

$rdkSrc = Join-Path $WorkDir "flexiv_rdk-$version"
$install = Join-Path $WorkDir "install-$version"
$wrapperBuild = Join-Path $WorkDir "wrapper-build-$version"
New-Item -ItemType Directory -Force $WorkDir | Out-Null

function Step($message) { Write-Host ">>> $message" -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path $rdkSrc)) {
    Step "Cloning flexiv_rdk $RdkTag"
    Run git @("clone", "--branch", $RdkTag, "--depth", "1", "https://github.com/flexivrobotics/flexiv_rdk.git", $rdkSrc)
}

if (-not (Test-Path (Join-Path $install "lib\cmake\fastrtps"))) {
    Step "Building RDK dependencies into $install (several minutes)"
    $cloned = Join-Path $rdkSrc "thirdparty\cloned"
    for ($attempt = 1; ; $attempt++) {
        # A clone interrupted by a network error leaves a repository without commits, which makes the
        # dependency scripts fail on the next run: remove it so it is cloned again.
        if (Test-Path $cloned) {
            foreach ($dir in Get-ChildItem $cloned -Directory) {
                git -C $dir.FullName rev-parse --verify --quiet HEAD *> $null
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "Removing incomplete clone $($dir.FullName)" -ForegroundColor Yellow
                    Remove-Item $dir.FullName -Recurse -Force
                }
            }
        }
        # install_eigen.sh clones from gitlab.com only if cloned/eigen does not exist: provide it from the mirror.
        $eigenDir = Join-Path $cloned "eigen"
        $eigenHead = if (Test-Path $eigenDir) { git -C $eigenDir rev-parse --verify --quiet "refs/tags/$EigenTag^{commit}" 2>$null } else { $null }
        if ($eigenHead -ne $EigenCommit) {
            if (Test-Path $eigenDir) { Remove-Item $eigenDir -Recurse -Force }
            Step "Cloning Eigen $EigenTag from $EigenUrl"
            New-Item -ItemType Directory -Force $cloned | Out-Null
            Run git @("clone", "--branch", $EigenTag, "--depth", "1", $EigenUrl, $eigenDir)
            $eigenHead = (git -C $eigenDir rev-parse HEAD).Trim()
            if ($eigenHead -ne $EigenCommit) {
                Remove-Item $eigenDir -Recurse -Force
                throw "Eigen from $EigenUrl is at $eigenHead, expected the official $EigenTag commit $EigenCommit. Use another -EigenUrl."
            }
        }
        Write-Host "Eigen $EigenTag verified ($EigenCommit)"

        Push-Location (Join-Path $rdkSrc "thirdparty")
        try {
            & $bash build_and_install_dependencies.sh ($install -replace "\\", "/") "$Jobs"
            $exit = $LASTEXITCODE
        }
        finally { Pop-Location }
        if ($exit -eq 0) { break }
        # Dependencies are cloned from GitHub/GitLab, which sometimes refuse requests under load.
        if ($attempt -ge $Retries) { throw "Dependency build failed $attempt times (exit code $exit). Retry later." }
        Write-Host "Dependency build failed (exit code $exit), retrying in $RetryDelaySeconds s ($attempt/$Retries)..." -ForegroundColor Yellow
        Start-Sleep -Seconds $RetryDelaySeconds
    }
}

Step "Installing flexiv_rdk $RdkTag"
Run cmake @("-S", $rdkSrc, "-B", (Join-Path $rdkSrc "build"), "-DCMAKE_PREFIX_PATH=$install", "-DCMAKE_INSTALL_PREFIX=$install")
Run cmake @("--build", (Join-Path $rdkSrc "build"), "--target", "install", "--config", "Release", "-j", "$Jobs")

Step "Building the C# wrapper DLL against RDK $version"
Run cmake @("-S", (Join-Path $PSScriptRoot "flexiv_rdk_csharp"), "-B", $wrapperBuild, "-DFLEXIV_RDK_VERSION=$version", "-DCMAKE_PREFIX_PATH=$install")
Run cmake @("--build", $wrapperBuild, "--config", "Release", "-j", "$Jobs")

$dll = Get-ChildItem $wrapperBuild -Recurse -Filter "flexiv_rdk.win64-vs2019.dll" | Where-Object { $_.FullName -match "Release" } | Select-Object -First 1
if (-not $dll) { throw "Wrapper DLL not found under $wrapperBuild" }
$target = Join-Path $repo "src\RobotGateway.Flexiv\native\rdk-$version"
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $dll.FullName $target -Force

$rdkCommit = (git -C $rdkSrc rev-parse HEAD).Trim()
$wrapperCommit = "37a09c2700d668365324dcde85fac23e6ba08ddf (+ local changes, see third_party/flexiv_rdk_csharp/LOCAL_CHANGES.md)"
$sha = (Get-FileHash (Join-Path $target $dll.Name) -Algorithm SHA256).Hash
@"
flexiv_rdk        : $RdkTag ($rdkCommit)
flexiv_rdk_csharp : $wrapperCommit
built             : $(Get-Date -Format "yyyy-MM-dd HH:mm") on $env:COMPUTERNAME, MSVC from $vs
dll sha256        : $sha
"@ | Set-Content (Join-Path $target "VERSION.txt") -Encoding utf8

Step "Done: $target"
