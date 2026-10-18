namespace RobotGateway.Core;

public enum ProgramKind
{
    /// <summary>Runs a plan already stored on the robot (created in Flexiv Elements), by name.</summary>
    ElementsPlan,

    /// <summary>Sequence of RDK primitives defined in JSON (not implemented yet).</summary>
    PrimitiveSequence,
}

/// <summary>Catalog entry for a work program. Only programs in the catalog can be run.</summary>
public sealed record WorkProgram
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public ProgramKind Kind { get; init; } = ProgramKind.ElementsPlan;

    /// <summary>Robot-side plan name, for <see cref="ProgramKind.ElementsPlan"/>.</summary>
    public string? PlanName { get; init; }

    /// <summary>Velocity scale proposed to the operator [%].</summary>
    public int VelocityScale { get; init; } = 100;

    /// <summary>Upper limit the operator cannot exceed for this program [%], enforced server-side.</summary>
    public int MaxVelocityScale { get; init; } = 100;

    public int Version { get; init; } = 1;
    public string? Author { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
