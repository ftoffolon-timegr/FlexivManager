using System.Net;

namespace FlexivManager.Hosting;

/// <summary>Listening endpoint of one web host. Bound from "Hosts:Operator" / "Hosts:Monitoring".</summary>
public sealed class HostEndpointOptions
{
    /// <summary>IP address to bind (an interface address, 127.0.0.1, or 0.0.0.0 for all interfaces).</summary>
    public string Address { get; set; } = "127.0.0.1";

    public int Port { get; set; }

    /// <summary>Only meaningful for the monitoring host.</summary>
    public bool Enabled { get; set; } = true;

    public IPAddress ParseAddress(string hostName) =>
        IPAddress.TryParse(Address, out var ip)
            ? ip
            : throw new InvalidOperationException($"Hosts:{hostName}:Address '{Address}' is not a valid IP address.");
}

/// <summary>Which host a component is rendered on. Lets the UI hide command features on the monitoring host.</summary>
public sealed record HostRole(string Name, bool CanCommand)
{
    public static HostRole Operator { get; } = new("Operator", CanCommand: true);
    public static HostRole Monitoring { get; } = new("Monitoring", CanCommand: false);
}
