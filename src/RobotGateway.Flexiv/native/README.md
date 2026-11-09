# Native RDK wrapper DLLs

One folder per RDK tag, built with `third_party/build-native.ps1 -RdkTag vX.Y` (x64 Release):

| Folder | RDK | Robot software |
|--------|-----|----------------|
| `rdk-1.8/` | v1.8 | 3.10.x |
| `rdk-1.9.2/` | v1.9.2 | 3.11.1 |
| `rdk-1.9.4.1/` | v1.9.4.1 | 3.11.3 |

Each folder holds `flexiv_rdk.win64-vs2019.dll` (the wrapper), `VERSION.txt` (RDK commit, wrapper commit, build date,
SHA256, runtime DLLs) and the runtime DLLs the wrapper depends on: the dependencies built as DLLs for RDK
1.9.0-1.9.3 (Fast-DDS, spdlog, yaml-cpp, RBDyn, ...), the prebuilt RDK DLL from 1.9.4.

`Robot:Flexiv:RdkVersion` is the folder name without `rdk-`. A version that is not here stops the start-up with a clear
message. One version per process: changing it needs a restart.
