using System.Reflection;
using System.Runtime.InteropServices;
using RobotGateway.Core;

namespace RobotGateway.Flexiv;

/// <summary>
/// Points the wrapper's [DllImport("flexiv_rdk.win64-vs2019.dll")] at native/rdk-x.y/ in the output folder,
/// so several RDK versions can ship side by side and configuration picks one.
/// </summary>
internal static class NativeRdkLoader
{
    public const string DllName = "flexiv_rdk.win64-vs2019.dll";

    private static readonly Lock Sync = new();
    private static string? _loadedVersion;
    private static nint _handle;

    public static string FolderFor(string rdkVersion) =>
        Path.Combine(AppContext.BaseDirectory, "native", $"rdk-{rdkVersion}");

    /// <summary>Loads the native DLL of the given version once per process.</summary>
    /// <exception cref="RobotBackendException">DLL missing, wrong architecture, or another version already loaded.</exception>
    public static void EnsureLoaded(string rdkVersion)
    {
        lock (Sync)
        {
            if (_loadedVersion == rdkVersion) return;
            if (_loadedVersion is not null)
                throw new RobotBackendException($"RDK {_loadedVersion} is already loaded; restart the service to switch to {rdkVersion}.");

            var path = Path.Combine(FolderFor(rdkVersion), DllName);
            if (!File.Exists(path))
                throw new RobotBackendException($"Native RDK wrapper not found: {path}. Build it with third_party/build-native.ps1 -RdkTag v{rdkVersion}.");
            if (!Environment.Is64BitProcess)
                throw new RobotBackendException("The Flexiv RDK wrapper requires a 64-bit (x64) process.");

            try { _handle = NativeLibrary.Load(path); }
            catch (Exception ex) { throw new RobotBackendException($"Cannot load {path}: {ex.Message}", ex); }

            var wrapperAssembly = typeof(FlexivRdk.Robot).Assembly;
            NativeLibrary.SetDllImportResolver(wrapperAssembly, Resolve);
            _loadedVersion = rdkVersion;
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName.StartsWith("flexiv_rdk", StringComparison.OrdinalIgnoreCase) ? _handle : 0;
}
