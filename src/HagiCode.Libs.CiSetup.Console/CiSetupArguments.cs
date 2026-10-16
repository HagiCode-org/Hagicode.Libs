namespace HagiCode.Libs.CiSetup.Console;

/// <summary>
/// The parsed command line for <c>CiSetup.Console</c>.
/// </summary>
public sealed record CiSetupArguments
{
    /// <summary>Gets a value indicating whether <c>--install</c> was supplied.</summary>
    public bool Install { get; init; }

    /// <summary>Gets a value indicating whether <c>--verify</c> was supplied.</summary>
    public bool Verify { get; init; }

    /// <summary>Gets a value indicating whether <c>--verify-discovery</c> was supplied.</summary>
    public bool VerifyDiscovery { get; init; }

    /// <summary>Gets the provider name from <c>--provider</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the strategy name from <c>--strategy</c>.</summary>
    public string? Strategy { get; init; }

    /// <summary>Gets the explicit expected version from <c>--expect-version</c>.</summary>
    public string? ExpectVersion { get; init; }

    /// <summary>Gets the explicit expected install kind from <c>--expect-install-kind</c>.</summary>
    public string? ExpectInstallKind { get; init; }

    /// <summary>Gets a value indicating whether <c>--without-install-dir-on-path</c> was supplied.</summary>
    public bool WithoutInstallDirOnPath { get; init; }

    /// <summary>Gets the argument errors, such as an option that is missing its value.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Gets a value indicating whether a provider or strategy was selected.</summary>
    public bool HasSelection => Provider is not null || Strategy is not null;
}

/// <summary>
/// Parses <c>CiSetup.Console</c> arguments. Unknown tokens are ignored, as they were before the discovery modes existed.
/// </summary>
public static class CiSetupArgumentParser
{
    /// <summary>
    /// Parses the supplied arguments.
    /// </summary>
    /// <param name="args">The raw command-line arguments.</param>
    /// <returns>The parsed arguments, with any argument errors recorded.</returns>
    public static CiSetupArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var errors = new List<string>();
        string? provider = null;
        string? strategy = null;
        string? expectVersion = null;
        string? expectInstallKind = null;
        var install = false;
        var verify = false;
        var verifyDiscovery = false;
        var withoutInstallDir = false;

        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            switch (token)
            {
                case "--install":
                    install = true;
                    break;
                case "--verify":
                    verify = true;
                    break;
                case "--verify-discovery":
                    verifyDiscovery = true;
                    break;
                case "--without-install-dir-on-path":
                    withoutInstallDir = true;
                    break;
                case "--provider":
                    provider = ReadValue(args, ref index, token, errors);
                    break;
                case "--strategy":
                    strategy = ReadValue(args, ref index, token, errors);
                    break;
                case "--expect-version":
                    expectVersion = ReadValue(args, ref index, token, errors);
                    break;
                case "--expect-install-kind":
                    expectInstallKind = ReadValue(args, ref index, token, errors);
                    break;
            }
        }

        return new CiSetupArguments
        {
            Install = install,
            Verify = verify,
            VerifyDiscovery = verifyDiscovery,
            Provider = provider,
            Strategy = strategy,
            ExpectVersion = expectVersion,
            ExpectInstallKind = expectInstallKind,
            WithoutInstallDirOnPath = withoutInstallDir,
            Errors = errors
        };
    }

    private static string? ReadValue(IReadOnlyList<string> args, ref int index, string option, List<string> errors)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            errors.Add($"{option} requires a value.");
            return null;
        }

        index++;
        return args[index];
    }
}
