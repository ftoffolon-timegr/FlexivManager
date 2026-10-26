# flexiv_rdk_csharp (vendored)

Official C# wrapper for Flexiv RDK, copied into this repository.

| | |
|---|---|
| Origin | https://github.com/flexivrobotics/flexiv_rdk_csharp |
| Commit | `37a09c2700d668365324dcde85fac23e6ba08ddf` (2026-09-23, "update to v1.9.3") |
| License | Apache 2.0 (`LICENSE`) |
| Upstream readme | `UPSTREAM_README.md` |

Copied: `cpp_wrapper/` (native C++ wrapper), `csharp/FlexivRdk/*.cs` (P/Invoke classes), `third_party/nlohmann_json`,
root `CMakeLists.txt`. **Not copied**: examples and `released_dll/` (prebuilt for RDK 1.9.3, not usable with robot
software 3.10).

Local changes are listed in `LOCAL_CHANGES.md` and visible in git history after the vendoring commit.
Native build: `third_party/build-native.ps1` (see `docs/ARCHITETTURA.md`, section 9).
