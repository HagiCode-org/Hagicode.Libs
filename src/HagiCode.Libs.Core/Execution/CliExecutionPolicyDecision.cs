namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Represents the result of evaluating a CLI execution request against policy.
/// </summary>
public sealed record CliExecutionPolicyDecision
{
    /// <summary>
    /// Gets a value indicating whether the request is allowed.
    /// </summary>
    public required bool IsAllowed { get; init; }

    /// <summary>
    /// Gets diagnostics describing the decision.
    /// </summary>
    public IReadOnlyList<CliExecutionDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Creates an allow decision.
    /// </summary>
    public static CliExecutionPolicyDecision Allow()
    {
        return new CliExecutionPolicyDecision
        {
            IsAllowed = true
        };
    }

    /// <summary>
    /// Creates a reject decision.
    /// </summary>
    public static CliExecutionPolicyDecision Reject(params CliExecutionDiagnostic[] diagnostics)
    {
        return new CliExecutionPolicyDecision
        {
            IsAllowed = false,
            Diagnostics = diagnostics
        };
    }
}
