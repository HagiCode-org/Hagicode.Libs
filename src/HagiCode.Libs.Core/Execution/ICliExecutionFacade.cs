namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Provides a unified facade for buffered and streaming CLI execution.
/// </summary>
public interface ICliExecutionFacade
{
    /// <summary>
    /// Executes a command to completion and returns a structured result.
    /// </summary>
    Task<CliExecutionResult> ExecuteAsync(
        CliExecutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a command and streams output events before returning a terminal envelope.
    /// </summary>
    IAsyncEnumerable<CliExecutionEvent> ExecuteStreamingAsync(
        CliExecutionRequest request,
        CancellationToken cancellationToken = default);
}
