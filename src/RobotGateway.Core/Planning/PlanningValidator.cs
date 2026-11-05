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
    /// <param name="robotPlans">
    /// Plans currently stored on the robot. When empty (robot never connected) the plan name cannot be
    /// checked here; it is checked again before every cycle start.
    /// </param>
    public static IReadOnlyList<string> Validate(JobDraft draft, IReadOnlyCollection<string> robotPlans)
    {
        var errors = new List<string>();
        if (ProductCode.Validate(draft.ProductCode) is { } codeError)
            errors.Add(codeError);
        if (string.IsNullOrWhiteSpace(draft.PlanName))
            errors.Add("Il plan è obbligatorio.");
        else if (robotPlans.Count > 0 && !robotPlans.Contains(draft.PlanName))
            errors.Add($"Il plan '{draft.PlanName}' non è presente sul robot.");
        if (draft.Description?.Length > MaxDescriptionLength)
            errors.Add($"La descrizione supera {MaxDescriptionLength} caratteri.");
        return errors;
    }

    /// <param name="draft">A normalized draft. Whether its code has a job and its day are checked by the caller.</param>
    public static IReadOnlyList<string> Validate(OrderDraft draft)
    {
        var errors = new List<string>();
        if (draft.OrderNumber.Length > MaxOrderNumberLength)
            errors.Add($"Il numero ordine supera {MaxOrderNumberLength} caratteri.");
        if (ProductCode.Validate(draft.ProductCode) is { } codeError)
            errors.Add(codeError);
        if (draft.Quantity is < 1 or > MaxQuantity)
            errors.Add($"La quantità deve essere tra 1 e {MaxQuantity}.");
        if (draft.Notes?.Length > MaxNotesLength)
            errors.Add($"Le note superano {MaxNotesLength} caratteri.");
        return errors;
    }
}
