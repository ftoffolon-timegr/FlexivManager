namespace RobotGateway.Core;

/// <summary>Server-side normalization and validation of job and order drafts.</summary>
public static class PlanningValidator
{
    public const int MaxDescriptionLength = 200;
    public const int MaxOrderNumberLength = 32;
    public const int MaxNotesLength = 500;
    public const int MaxQuantity = 100_000;

    public static JobDraft Normalize(JobDraft d) => d with
    {
        ProductCode = ProductCode.Normalize(d.ProductCode),
        PlanName = (d.PlanName ?? "").Trim(),
        Description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description.Trim(),
    };

    public static OrderDraft Normalize(OrderDraft d) => d with
    {
        OrderNumber = (d.OrderNumber ?? "").Trim(),
        ProductCode = ProductCode.Normalize(d.ProductCode),
        Notes = string.IsNullOrWhiteSpace(d.Notes) ? null : d.Notes.Trim(),
    };

    /// <param name="draft">A normalized draft.</param>
    /// <param name="maxVelocityScale">Global velocity limit [%] from configuration.</param>
    /// <param name="robotPlans">
    /// Plans currently stored on the robot. When empty (robot never connected) the plan name cannot be
    /// checked here; it is checked again before every cycle start.
    /// </param>
    public static IReadOnlyList<string> Validate(JobDraft draft, int maxVelocityScale, IReadOnlyCollection<string> robotPlans)
    {
        var errors = new List<string>();
        if (ProductCode.Validate(draft.ProductCode) is { } codeError)
            errors.Add(codeError);
        if (string.IsNullOrWhiteSpace(draft.PlanName))
            errors.Add("Plan is required.");
        else if (robotPlans.Count > 0 && !robotPlans.Contains(draft.PlanName))
            errors.Add($"Plan '{draft.PlanName}' is not stored on the robot.");
        if (draft.VelocityScale < CommandPolicy.MinVelocityScale || draft.VelocityScale > maxVelocityScale)
            errors.Add($"Velocity must be within {CommandPolicy.MinVelocityScale}..{maxVelocityScale} %.");
        if (draft.Description?.Length > MaxDescriptionLength)
            errors.Add($"Description is longer than {MaxDescriptionLength} characters.");
        return errors;
    }

    /// <param name="draft">A normalized draft. Whether its code has a job is checked by the caller.</param>
    public static IReadOnlyList<string> Validate(OrderDraft draft)
    {
        var errors = new List<string>();
        if (draft.OrderNumber.Length == 0)
            errors.Add("Order number is required.");
        else if (draft.OrderNumber.Length > MaxOrderNumberLength)
            errors.Add($"Order number is longer than {MaxOrderNumberLength} characters.");
        if (ProductCode.Validate(draft.ProductCode) is { } codeError)
            errors.Add(codeError);
        if (draft.Quantity is < 1 or > MaxQuantity)
            errors.Add($"Quantity must be within 1..{MaxQuantity}.");
        if (draft.Notes?.Length > MaxNotesLength)
            errors.Add($"Notes are longer than {MaxNotesLength} characters.");
        return errors;
    }
}
