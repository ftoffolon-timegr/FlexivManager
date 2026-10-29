# Patches for RDK dependencies

Applied by `third_party/build-native.ps1` to dependencies that flexiv_rdk's scripts build. The Flexiv scripts
themselves are not modified: the patched dependency is pre-cloned into the folder the script looks in.

| Patch | Dependency (version pinned by flexiv_rdk v1.8) | Why |
|-------|------------------------------------------------|-----|
| `tinyxml2-8.0.0-cmake4.patch` | tinyxml2 8.0.0, commit `bf15233ad88390461f6ab0dbcf046cce643c5fcb` | Removes `cmake_policy(SET CMP0063 OLD)`: CMake 4 (VS 2026) no longer allows OLD for policies introduced before 3.5. With NEW, visibility properties also apply to static libraries; no effect on this build. |

Scan done on 2026-10-29 at the pinned versions (spdlog v1.14.1, foonathan_memory_vendor v1.3.1 + foonathan/memory
v0.7-3, Fast-CDR v1.0.28, Fast-DDS v2.6.10 with submodules, flexiv_rdk v1.8): no other `cmake_policy(SET ... OLD)` on
removed policies. Fast-DDS bundles tinyxml2 with the same line, but uses the already installed tinyxml2.
`cmake_minimum_required` below 3.5 is handled by `CMAKE_POLICY_VERSION_MINIMUM=3.5` set in the script.

Other pinned sources:
- Eigen 3.4.0 is cloned from `github.com/eigen-mirror/eigen` (gitlab.com refuses clones under load) and accepted only
  at the official commit `3147391d946bb4b6c68edd901f2add6ac1f31f8c`.
