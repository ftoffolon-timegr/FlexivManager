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

    /// <summary>Monitoring host: planning pages (jobs, work list) require the editor login.</summary>
    public bool RequireLoginForEdit { get; set; } = true;

    /// <summary>Monitoring host: the single planner account.</summary>
    public EditorAccountOptions Editor { get; set; } = new();

    public IPAddress ParseAddress(string hostName) =>
        IPAddress.TryParse(Address, out var ip)
            ? ip
            : throw new InvalidOperationException($"Hosts:{hostName}:Address '{Address}' is not a valid IP address.");
}

public sealed class EditorAccountOptions
{
    public string UserName { get; set; } = "planner";

    /// <summary>
    /// PBKDF2 hash produced by <c>FlexivManager hash-password &lt;password&gt;</c>.
    /// Empty: nobody can log in (planning is read-only).
    /// </summary>
    public string PasswordHash { get; set; } = "";
}

/// <summary>
/// Which host a component is rendered on. Operator (local): executes, never edits.
/// Monitoring (remote): configures jobs and the work list, never moves the robot.
/// </summary>
public sealed record HostRole(string Name, bool CanCommand, bool CanEdit)
{
    public static HostRole Operator { get; } = new("Operator", CanCommand: true, CanEdit: false);
    public static HostRole Monitoring { get; } = new("Monitoring", CanCommand: false, CanEdit: true);
}

public static class Policies
{
    /// <summary>Planning edits (jobs, work list). Login required unless disabled in configuration.</summary>
    public const string Planner = "Planner";
}
