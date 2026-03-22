namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Represents one captured piece of command output.
/// </summary>
/// <param name="Channel">The source stream.</param>
/// <param name="Text">The captured text.</param>
/// <param name="TimestampUtc">The capture timestamp.</param>
public sealed record CliExecutionOutputChunk(
    CliExecutionOutputChannel Channel,
    string Text,
    DateTimeOffset TimestampUtc);
