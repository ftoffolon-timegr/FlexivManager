namespace RobotGateway.Flexiv;

/// <summary>Bound from "Robot:Flexiv". Used only when Robot:Backend = Flexiv.</summary>
public sealed class FlexivBackendOptions
{
    public const string SectionName = "Robot:Flexiv";

    /// <summary>Robot serial number, e.g. "Rizon4-062609" (RDK accepts it with or without the space after the model).</summary>
    public string SerialNumber { get; set; } = "";

    /// <summary>
    /// RDK version of the native wrapper to load: the name of folder native/rdk-&lt;version&gt;. Must match the
    /// robot software: 3.10 -> "1.8", 3.11.1 -> "1.9.2", 3.11.3 -> "1.9.4.1". RDK refuses to connect on mismatch.
    /// </summary>
    public string RdkVersion { get; set; } = "1.8";

    /// <summary>Optional IPv4 addresses of the local interfaces RDK may use to reach the robot; empty = all.</summary>
    public string[] NetworkInterfaces { get; set; } = [];

    /// <summary>
    /// Upper bound for ClearFault, which blocks the worker thread while it waits (no snapshot meanwhile).
    /// The robot is in fault, so it is not moving.
    /// </summary>
    public int ClearFaultTimeoutSeconds { get; set; } = 3;

    /// <summary>RDK console prints (info/warnings).</summary>
    public bool Verbose { get; set; }
}
