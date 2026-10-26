# Native RDK wrapper DLLs

One folder per RDK version, built with `third_party/build-native.ps1 -RdkTag vX.Y` (x64 Release):

| Folder | RDK | Robot software |
|--------|-----|----------------|
| `rdk-1.8/` | v1.8 | 3.10.x |
| `rdk-1.9/` | v1.9 | 3.11.x |

Each folder holds `flexiv_rdk.win64-vs2019.dll` and `VERSION.txt` (RDK commit, wrapper commit, build date, SHA256).
`Robot:Flexiv:RdkVersion` selects the folder at runtime.
