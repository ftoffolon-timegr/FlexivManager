using System.Text.RegularExpressions;

namespace RobotGateway.Core;

/// <summary>
/// Text to show the operator for a robot error event. A custom fault raised by a plan in Flexiv Elements
/// arrives as e.g. "[316013] An error occurred while running the parallel program: [errore aria]. - Clear the fault.":
/// the text in the second pair of brackets is the message written in the plan. Other errors keep the robot description.
/// </summary>
public static partial class RobotFaultText
{
    public static string Extract(string message)
    {
        var brackets = Brackets().Matches(message);
        // The first bracket is the event id added by the mapping ("[316013]"), when present.
        var custom = brackets.Skip(LeadingId().IsMatch(message) ? 1 : 0).FirstOrDefault();
        if (custom is not null && custom.Groups["text"].Value.Trim() is { Length: > 0 } text)
            return text;
        return LeadingId().Replace(message, "").Trim();
    }

    [GeneratedRegex(@"\[(?<text>[^\[\]]*)\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"^\s*\[\d+\]\s*")]
    private static partial Regex LeadingId();
}
