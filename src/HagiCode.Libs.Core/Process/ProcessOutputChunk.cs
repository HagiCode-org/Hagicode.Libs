namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Represents one captured piece of process output.
/// </summary>
/// <param name="Channel">The source stream.</param>
/// <param name="Text">The captured text.</param>
/// <param name="TimestampUtc">The capture timestamp.</param>
public sealed record ProcessOutputChunk(
    ProcessOutputChannel Channel,
    string Text,
    DateTimeOffset TimestampUtc);
