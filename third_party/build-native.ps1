<#
.SYNOPSIS
  Builds the native Flexiv RDK C# wrapper DLL for one RDK version (x64 Release) and copies it to
  src/RobotGateway.Flexiv/native/rdk-<tag>/ (rdk-1.8, rdk-1.9.4.1, ...): <tag> is the value of Robot:Flexiv:RdkVersion.

.DESCRIPTION
  1. Clones flexiv_rdk at the given tag (the static RDK library is downloaded by its CMake with SHA256 check).
  2. Builds its dependencies (v1.8: Eigen, spdlog, tinyxml2, foonathan_memory, Fast-CDR, Fast-DDS, static;
     v1.9.0-1.9.3: the same plus yaml-cpp, Boost, SpaceVecAlg, RBDyn, as DLLs; v1.9.4+: Eigen only).
  3. Installs flexiv_rdk, then builds third_party/flexiv_rdk_csharp against it.
  Needs Visual Studio with the C++ desktop workload (MSVC + CMake) and Git for Windows (bash).
  Robot software 3.10 -> RDK v1.8, 3.11.3 -> RDK v1.9.4.1 (see the Flexiv compatibility table and release notes).
  From v1.9.4 RDK is a prebuilt DLL: it is copied next to the wrapper and only Eigen is built. Tags v1.9.0-v1.9.3 are
  not tested beyond v1.9.2 (robot 3.11.1): they build their dependencies (also Boost, RBDyn, yaml-cpp) as DLLs, which
  are copied next to the wrapper. The wrapper needs at least v1.9.2 within 1.9 (actions()).

.EXAMPLE
  ./third_party/build-native.ps1 -RdkTag v1.8
  ./third_party/build-native.ps1 -RdkTag v1.9.2      # robot software 3.11.1
  ./third_party/build-native.ps1 -RdkTag v1.9.4.1    # robot software 3.11.3
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
# CMake 4 (shipped with VS 2026) rejects projects declaring cmake_minimum_required < 3.5, as some pinned RDK
# dependencies do. This environment variable applies to every cmake call, including those of the RDK scripts.
if (-not $env:CMAKE_POLICY_VERSION_MINIMUM) { $env:CMAKE_POLICY_VERSION_MINIMUM = "3.5" }
# Long paths for the clones made by the dependency scripts, without touching the user's git config.
$env:GIT_CONFIG_COUNT = "1"; $env:GIT_CONFIG_KEY_0 = "core.longpaths"; $env:GIT_CONFIG_VALUE_0 = "true"

# Work folders use the full tag (v1.8 -> 1.8, v1.9.4.1 -> 1.9.4.1): a different patch release must not reuse them.
$tagName = $RdkTag.TrimStart("v")
$rdkSrc = Join-Path $WorkDir "flexiv_rdk-$tagName"
$install = Join-Path $WorkDir "install-$tagName"
$wrapperBuild = Join-Path $WorkDir "wrapper-build-$tagName"
New-Item -ItemType Directory -Force $WorkDir | Out-Null

function Step($message) { Write-Host ">>> $message" -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

# Runs a git check whose failure is an answer, not an error (PowerShell 5 would turn its stderr into a
# terminating error under ErrorActionPreference=Stop). Returns true on exit code 0.
function Test-Git {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & git @args *> $null; return $LASTEXITCODE -eq 0 }
    finally { $ErrorActionPreference = $previous }
}

# Provides <cloned>/<name> at an exact commit (and with an optional patch) before the RDK scripts run.
# The checkout is accepted only if HEAD is the expected commit, so the source URL does not need to be trusted.
function Use-PinnedDependency([string]$cloned, [string]$name, [string]$url, [string]$tag, [string]$commit, [string]$patch = "") {
    $dir = Join-Path $cloned $name
    $head = if ((Test-Path $dir) -and (Test-Git -C $dir rev-parse --verify --quiet HEAD)) { (git -C $dir rev-parse HEAD).Trim() } else { $null }
    if ($head -ne $commit) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        Step "Cloning $name $tag from $url"
        New-Item -ItemType Directory -Force $cloned | Out-Null
        Run git @("clone", "--branch", $tag, "--depth", "1", $url, $dir)
        $head = (git -C $dir rev-parse HEAD).Trim()
        if ($head -ne $commit) {
            Remove-Item $dir -Recurse -Force
            throw "$name from $url is at $head, expected the official $tag commit $commit."
        }
    }
    if ($patch) {
        if (-not (Test-Git -C $dir apply --reverse --check $patch)) {
            Step "Patching $name with $(Split-Path $patch -Leaf)"
            Run git @("-C", $dir, "apply", $patch)
            # A configure that failed before the patch leaves a stale cache.
            $build = Join-Path $dir "build"
            if (Test-Path $build) { Remove-Item $build -Recurse -Force }
        }
    }
    Write-Host "$name $tag verified ($commit)$(if ($patch) { ', patched' })"
}

if (-not (Test-Path $rdkSrc)) {
    Step "Cloning flexiv_rdk $RdkTag"
    Run git @("clone", "--branch", $RdkTag, "--depth", "1", "https://github.com/flexivrobotics/flexiv_rdk.git", $rdkSrc)
}

# Dependencies are built once. The marker covers versions with only Eigen; fastrtps is an RDK 1.8 install made
# before the marker existed.
$depsMarker = Join-Path $install "deps-installed.txt"
if (-not (Test-Path $depsMarker) -and -not (Test-Path (Join-Path $install "lib\cmake\fastrtps"))) {
    Step "Building RDK dependencies into $install (several minutes)"
    $cloned = Join-Path $rdkSrc "thirdparty\cloned"
    for ($attempt = 1; ; $attempt++) {
        # A clone interrupted by a network error leaves a repository without commits, which makes the
        # dependency scripts fail on the next run: remove it so it is cloned again.
        if (Test-Path $cloned) {
            foreach ($dir in Get-ChildItem $cloned -Directory) {
                if (-not (Test-Git -C $dir.FullName rev-parse --verify --quiet HEAD)) {
                    Write-Host "Removing incomplete clone $($dir.FullName)" -ForegroundColor Yellow
                    Remove-Item $dir.FullName -Recurse -Force
                }
            }
        }
        # The install_*.sh scripts clone a dependency only if its folder is missing, so these are provided here.
        # Eigen: from a mirror (gitlab.com refuses clones under load); install_eigen.sh resets it, no patch possible.
        Use-PinnedDependency $cloned "eigen" $EigenUrl $EigenTag $EigenCommit
        # tinyxml2: forces policy CMP0063 to OLD, which CMake 4 refuses. install_tinyxml2.sh only checks out the
        # same tag (no reset), so the patched working tree is kept.
        # Only tags that still pin 8.0.0 need it (v1.9.4.1 has no tinyxml2 at all).
        $tinyxml2Script = Join-Path $rdkSrc "thirdparty\scripts\install_tinyxml2.sh"
        if ((Test-Path $tinyxml2Script) -and (Select-String $tinyxml2Script -Pattern "^VER_TAG=8\.0\.0" -Quiet)) {
            Use-PinnedDependency $cloned "tinyxml2" "https://github.com/leethomason/tinyxml2.git" "8.0.0" `
                "bf15233ad88390461f6ab0dbcf046cce643c5fcb" (Join-Path $PSScriptRoot "patches\tinyxml2-8.0.0-cmake4.patch")
        }

        Push-Location (Join-Path $rdkSrc "thirdparty")
        try {
            & $bash build_and_install_dependencies.sh ($install -replace "\\", "/") "$Jobs"
            $exit = $LASTEXITCODE
        }
        finally { Pop-Location }
        if ($exit -eq 0) {
            Set-Content $depsMarker "Dependencies of flexiv_rdk $RdkTag installed." -Encoding ascii
            break
        }
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
Run cmake @("-S", (Join-Path $PSScriptRoot "flexiv_rdk_csharp"), "-B", $wrapperBuild, "-DFLEXIV_RDK_VERSION=$tagName", "-DCMAKE_PREFIX_PATH=$install")
Run cmake @("--build", $wrapperBuild, "--config", "Release", "-j", "$Jobs")

$dll = Get-ChildItem $wrapperBuild -Recurse -Filter "flexiv_rdk.win64-vs2019.dll" | Where-Object { $_.FullName -match "Release" } | Select-Object -First 1
if (-not $dll) { throw "Wrapper DLL not found under $wrapperBuild" }
$target = Join-Path $repo "src\RobotGateway.Flexiv\native\rdk-$tagName"
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $dll.FullName $target -Force
# Runtime DLLs the wrapper needs besides Windows and the VC++ runtime: RDK 1.9.0-1.9.3 build their dependencies as
# shared libraries (Fast-DDS, spdlog, yaml-cpp, RBDyn, ...), from 1.9.4 RDK itself is a DLL. Every DLL the wrapper
# imports, directly or through another copied DLL, that exists in the RDK install is copied next to it.
Get-ChildItem $target -Filter *.dll | Where-Object Name -ne $dll.Name | Remove-Item -Force   # stale from a previous build
$dumpbin = Get-ChildItem (Join-Path $vs "VC\Tools\MSVC") -Recurse -Filter dumpbin.exe | Where-Object FullName -Match "Hostx64\\x64" | Select-Object -First 1
$searchDirs = @((Join-Path $install "bin"), (Join-Path $install "lib"), (Join-Path $rdkSrc "build")) | Where-Object { Test-Path $_ }
$rdkRuntime = @((Join-Path $install "bin\flexiv_rdk.dll"), (Join-Path $rdkSrc "build\flexiv_rdk.win_amd64.dll")) | Where-Object { Test-Path $_ } | Select-Object -First 1
$copied = [System.Collections.Generic.List[string]]::new()
$queue = [System.Collections.Generic.Queue[string]]::new()
$queue.Enqueue((Join-Path $target $dll.Name))
while ($queue.Count -gt 0) {
    $file = $queue.Dequeue()
    $imports = & $dumpbin.FullName /dependents $file | ForEach-Object { $_.Trim() } | Where-Object { $_ -match "^\S+\.dll$" }
    foreach ($import in $imports) {
        if ($copied.Contains($import)) { continue }
        $source = $searchDirs | ForEach-Object { Get-ChildItem $_ -Recurse -Filter $import -ErrorAction SilentlyContinue } | Select-Object -First 1 -ExpandProperty FullName
        if (-not $source -and $import -like "flexiv_rdk*" -and $rdkRuntime) { $source = $rdkRuntime }  # RDK DLL under its import name
        if (-not $source) { continue }   # Windows / VC++ runtime
        Copy-Item $source (Join-Path $target $import) -Force
        $copied.Add($import)
        $queue.Enqueue((Join-Path $target $import))
    }
}
Write-Host "Runtime DLLs copied next to the wrapper: $(if ($copied.Count) { $copied -join ', ' } else { 'none' })"

$rdkCommit = (git -C $rdkSrc rev-parse HEAD).Trim()
$wrapperCommit = "37a09c2700d668365324dcde85fac23e6ba08ddf (+ local changes, see third_party/flexiv_rdk_csharp/LOCAL_CHANGES.md)"
$sha = (Get-ChildItem $target -Filter *.dll | ForEach-Object { "$($_.Name)=$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)" }) -join " "
@"
flexiv_rdk        : $RdkTag ($rdkCommit)
flexiv_rdk_csharp : $wrapperCommit
built             : $(Get-Date -Format "yyyy-MM-dd HH:mm") on $env:COMPUTERNAME, MSVC from $vs
runtime dlls      : $(if ($copied.Count) { $copied -join ', ' } else { 'none' })
dll sha256        : $sha
"@ | Set-Content (Join-Path $target "VERSION.txt") -Encoding utf8

Step "Done: $target"
