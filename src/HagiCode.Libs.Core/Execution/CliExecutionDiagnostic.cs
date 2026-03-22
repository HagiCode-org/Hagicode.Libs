namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Describes a machine-readable execution diagnostic.
/// </summary>
/// <param name="Code">The diagnostic code.</param>
/// <param name="Message">The human-readable message.</param>
public sealed record CliExecutionDiagnostic(string Code, string Message);
