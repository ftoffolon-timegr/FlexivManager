# Local changes to flexiv_rdk_csharp (base 37a09c2)

Goal: build one source tree against RDK 1.8 (robot software 3.10) and RDK 1.9 (3.11).

| File | Change |
|------|--------|
| `CMakeLists.txt` | `FLEXIV_RDK_VERSION` cache variable (default `1.9`) instead of the hard-coded `find_package(flexiv_rdk 1.9.3)`; defines `FLEXIV_RDK_API` = major*100+minor (108, 109). |
| `cpp_wrapper/CMakeLists.txt` | Passes `FLEXIV_RDK_API` to the compiler. |
| `cpp_wrapper/src/wrapper_api.cpp` | With `FLEXIV_RDK_API < 109`: `GetActions`, `SendMultiCartesianMotionForce`, `DeviceEnabled`, `DeviceConnected`, `DownloadCollisionMesh`, `SetJointOutputTorqueRegulator`, `SyncKinematicsYAML` keep their export but return an error "requires flexiv_rdk 1.9 or later"; `ReachedTimelinessFailureLimit` returns 0; `SetTimelinessFailureLimit` passes a failure count (RDK 1.8) instead of a percentage; `StreamJointTorque` ignores `frictionCompScale` (not in RDK 1.8). |
| `cpp_wrapper/src/wrapper_api.cpp` | **Upstream bug fix**, all versions: in `SetJointVelocityNormalLimits` / `SetJointVelocityReducedLimits` the local `std::vector<double> maxVel(maxVel, maxVel + len)` shadowed the parameter, so the vector was built from itself (undefined behavior in a safety-limit setter). Local renamed to `maxVelVec`. |
| `CMakeLists.txt`, `cpp_wrapper/` | spdlog is optional (`FLEXIV_HAS_SPDLOG`): RDK 1.9.4+ is a self-contained DLL without it; the `Spdlog*` exports then print to the console. |
| `FlexivRdk.csproj` | New: builds `csharp/FlexivRdk/*.cs` as a library for RobotGateway.Flexiv. |

The list is complete for RDK 1.8: the wrapper compiles against the installed v1.8 headers with MSVC 14.51 (2026-10-29),
and a full syntax check with GCC reported the same set. None of the affected functions is used by the gateway.

Known upstream oddity (not changed): `EnablingButtonReleased` returns `robot->enabling_button_pressed()`, so the C#
`enabling_button_pressed()` value may be inverted. Not used by the gateway; verify on the robot before using it.
