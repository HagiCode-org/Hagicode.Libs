namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Validates CLI execution requests before process creation.
/// </summary>
public interface ICliExecutionPolicy
{
    /// <summary>
    /// Evaluates a fully-resolved execution context.
    /// </summary>
    ValueTask<CliExecutionPolicyDecision> EvaluateAsync(
        CliExecutionContext context,
        CancellationToken cancellationToken = default);
}
