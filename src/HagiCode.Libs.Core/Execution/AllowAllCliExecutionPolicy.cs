namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Allows all execution requests.
/// </summary>
public sealed class AllowAllCliExecutionPolicy : ICliExecutionPolicy
{
    /// <inheritdoc />
    public ValueTask<CliExecutionPolicyDecision> EvaluateAsync(
        CliExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CliExecutionPolicyDecision.Allow());
    }
}
