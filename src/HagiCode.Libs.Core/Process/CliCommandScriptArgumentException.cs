namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Thrown when an argument cannot be passed safely to a Windows command script (<c>.cmd</c> or <c>.bat</c>), so the launch is refused before any process starts.
/// The message names the argument index and the rule. It never includes the argument value, because values can carry secrets.
/// </summary>
public sealed class CliCommandScriptArgumentException : ArgumentException
{
    internal CliCommandScriptArgumentException(int? argumentIndex, string rule)
        : base(BuildMessage(argumentIndex, rule))
    {
        ArgumentIndex = argumentIndex;
        Rule = rule;
    }

    /// <summary>
    /// Gets the zero-based index of the rejected argument, or <see langword="null" /> when the script path itself was rejected.
    /// </summary>
    public int? ArgumentIndex { get; }

    /// <summary>
    /// Gets the rule that rejected the argument. It describes the rule and never the value.
    /// </summary>
    public string Rule { get; }

    private static string BuildMessage(int? argumentIndex, string rule)
    {
        var subject = argumentIndex is { } index
            ? $"Argument {index}"
            : "The command script path";

        return $"{subject} cannot be passed to a Windows command script because {rule}. "
               + "Install or use the CLI's native .exe, or pass this value another way (for example through stdin or a file). The value is not shown.";
    }
}
