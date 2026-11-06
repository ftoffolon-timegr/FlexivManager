using System.Reflection;
using System.Runtime.InteropServices;
using RobotGateway.Core;

namespace RobotGateway.Flexiv;

/// <summary>RDK versions shipped in native/rdk-&lt;version&gt;/ next to the executable.</summary>
public static class NativeRdkVersions
{
    public const string WrapperDll = "flexiv_rdk.win64-vs2019.dll";

    public static string RootFolder => Path.Combine(AppContext.BaseDirectory, "native");

    public static string FolderFor(string rdkVersion) => Path.Combine(RootFolder, $"rdk-{rdkVersion}");

    /// <summary>Versions with a wrapper DLL, e.g. "1.8", "1.9.4.1".</summary>
    public static IReadOnlyList<string> Installed() =>
        Directory.Exists(RootFolder)
            ? Directory.GetDirectories(RootFolder, "rdk-*")
                .Where(d => File.Exists(Path.Combine(d, WrapperDll)))
                .Select(d => Path.GetFileName(d)["rdk-".Length..])
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    /// <summary>Checked at start-up: a wrong Robot:Flexiv:RdkVersion must not look like a network problem.</summary>
    /// <exception cref="InvalidOperationException">The configured version is not installed.</exception>
    public static void EnsureInstalled(string rdkVersion)
    {
        if (Installed().Contains(rdkVersion))
            return;
        var installed = Installed();
        throw new InvalidOperationException(
            $"Robot:Flexiv:RdkVersion = '{rdkVersion}': versione non installata in {RootFolder}. " +
            $"Disponibili: {(installed.Count > 0 ? string.Join(", ", installed) : "nessuna")}.");
    }
}

/// <summary>
/// Points the wrapper's [DllImport("flexiv_rdk.win64-vs2019.dll")] at native/rdk-&lt;version&gt;/, so several RDK
/// versions can ship side by side and configuration picks one (one per process).
/// </summary>
internal static class NativeRdkLoader
{
    private static readonly Lock Sync = new();
    private static string? _loadedVersion;
    private static nint _handle;

    /// <summary>Loads the native DLLs of the given version once per process.</summary>
    /// <exception cref="RobotBackendException">DLL missing, wrong architecture, or another version already loaded.</exception>
    public static void EnsureLoaded(string rdkVersion)
    {
        lock (Sync)
        {
            if (_loadedVersion == rdkVersion) return;
            if (_loadedVersion is not null)
                throw new RobotBackendException($"RDK {_loadedVersion} è già caricato: riavvia il servizio per passare a {rdkVersion}.");

            var folder = NativeRdkVersions.FolderFor(rdkVersion);
            var path = Path.Combine(folder, NativeRdkVersions.WrapperDll);
            if (!File.Exists(path))
                throw new RobotBackendException($"Wrapper nativo RDK non trovato: {path}. Compilalo con third_party/build-native.ps1 -RdkTag v{rdkVersion}.");
            if (!Environment.Is64BitProcess)
                throw new RobotBackendException("Il wrapper Flexiv RDK richiede un processo a 64 bit (x64).");

            try
            {
                // From RDK 1.9.4 the wrapper depends on the RDK DLL in the same folder: load it first, so Windows
                // resolves the wrapper's import to it whatever the search path is.
                foreach (var dependency in Directory.GetFiles(folder, "*.dll").Where(f => !f.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    NativeLibrary.Load(dependency);
                _handle = NativeLibrary.Load(path);
            }
            catch (Exception ex) { throw new RobotBackendException($"Impossibile caricare {path}: {ex.Message}", ex); }

            var wrapperAssembly = typeof(FlexivRdk.Robot).Assembly;
            NativeLibrary.SetDllImportResolver(wrapperAssembly, Resolve);
            _loadedVersion = rdkVersion;
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName.StartsWith("flexiv_rdk", StringComparison.OrdinalIgnoreCase) ? _handle : 0;
}
