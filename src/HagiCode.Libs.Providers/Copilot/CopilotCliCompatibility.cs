namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// Represents the filtered Copilot CLI arguments plus deterministic diagnostics for rejected flags.
/// </summary>
/// <param name="CliArgs">The filtered CLI arguments that remain safe for SDK-managed startup.</param>
/// <param name="Diagnostics">Diagnostics describing ignored or rejected startup arguments.</param>
public sealed record CopilotCliArgumentBuildResult(
    IReadOnlyList<string> CliArgs,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Filters Copilot startup arguments down to the subset verified to work with the SDK-managed launch path.
/// </summary>
public static class CopilotCliCompatibility
{
    private static readonly HashSet<string> SupportedStandaloneFlags = new(StringComparer.Ordinal)
    {
        "--allow-all",
        "--allow-all-paths",
        "--allow-all-tools",
        "--allow-all-urls",
        "--banner",
        "--disable-builtin-mcps",
        "--disable-parallel-tools-execution",
        "--disallow-temp-dir",
        "--enable-all-github-mcp-tools",
        "--experimental",
        "--no-ask-user",
        "--no-auto-update",
        "--no-color",
        "--no-custom-instructions",
        "--plain-diff",
        "--screen-reader",
        "--yolo"
    };

    private static readonly Dictionary<string, int> SupportedValueFlags = new(StringComparer.Ordinal)
    {
        ["--add-dir"] = 1,
        ["--add-github-mcp-tool"] = 1,
        ["--add-github-mcp-toolset"] = 1,
        ["--additional-mcp-config"] = 1,
        ["--agent"] = 1,
        ["--allow-tool"] = 1,
        ["--allow-url"] = 1,
        ["--available-tools"] = 1,
        ["--config-dir"] = 1,
        ["--deny-tool"] = 1,
        ["--deny-url"] = 1,
        ["--disable-mcp-server"] = 1,
        ["--excluded-tools"] = 1,
        ["--log-dir"] = 1,
        ["--log-level"] = 1
    };

    private static readonly Dictionary<string, string> RejectedFlags = new(StringComparer.Ordinal)
    {
        ["--headless"] = "the installed Copilot CLI does not advertise this startup flag in 'copilot --help'",
        ["--acp"] = "ACP and server bootstrap are managed by the SDK gateway",
        ["--continue"] = "session resume is managed by CopilotOptions.SessionId instead of raw CLI flags",
        ["--help"] = "help output is not a valid runtime startup argument",
        ["-h"] = "help output is not a valid runtime startup argument",
        ["--interactive"] = "interactive prompting is managed by the provider request model",
        ["-i"] = "interactive prompting is managed by the provider request model",
        ["--model"] = "model selection is forwarded through SDK-native request fields",
        ["--prompt"] = "prompt content is forwarded through SDK-native request fields",
        ["-p"] = "prompt content is forwarded through SDK-native request fields",
        ["--resume"] = "session resume is managed by CopilotOptions.SessionId instead of raw CLI flags",
        ["--share"] = "session export is not part of the provider startup contract",
        ["--share-gist"] = "session export is not part of the provider startup contract",
        ["--silent"] = "output shaping is handled by the provider response pipeline",
        ["-s"] = "output shaping is handled by the provider response pipeline",
        ["--stream"] = "streaming is controlled by the SDK session configuration",
        ["--version"] = "version output is not a valid runtime startup argument",
        ["-v"] = "version output is not a valid runtime startup argument"
    };

    /// <summary>
    /// Gets the verified startup support matrix used by the Copilot compatibility filter.
    /// </summary>
    public static IReadOnlyDictionary<string, string> VerifiedSupportMatrix { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--allow-all"] = "Enable all permissions.",
            ["--allow-all-tools"] = "Allow all tools to run automatically without confirmation.",
            ["--allow-all-paths"] = "Allow access to any filesystem path.",
            ["--allow-all-urls"] = "Allow access to all URLs without confirmation.",
            ["--no-ask-user"] = "Disable ask_user so the agent works autonomously.",
            ["--experimental"] = "Enable experimental Copilot CLI features.",
            ["--agent"] = "Select a named custom agent.",
            ["--add-dir"] = "Add an allowed filesystem directory.",
            ["--allow-tool"] = "Pre-approve a tool invocation pattern.",
            ["--deny-tool"] = "Explicitly deny a tool invocation pattern.",
            ["--allow-url"] = "Allow a URL or domain without confirmation.",
            ["--deny-url"] = "Deny a URL or domain.",
            ["--available-tools"] = "Limit the model to an explicit tool allowlist.",
            ["--excluded-tools"] = "Remove specific tools from the available set.",
            ["--config-dir"] = "Use a custom Copilot configuration directory.",
            ["--log-dir"] = "Write logs to a custom directory.",
            ["--log-level"] = "Set Copilot CLI log verbosity."
        };

    /// <summary>
    /// Builds the filtered Copilot CLI startup arguments and diagnostics for rejected flags.
    /// </summary>
    /// <param name="options">The Copilot options to translate.</param>
    /// <returns>The filtered CLI arguments and diagnostics.</returns>
    public static CopilotCliArgumentBuildResult BuildCliArgs(CopilotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var args = new List<string>();
        var diagnostics = new List<string>();
        var seenStandaloneFlags = new HashSet<string>(StringComparer.Ordinal);

        AddDerivedArguments(options, args, seenStandaloneFlags);
        AddConfiguredArguments(options.AdditionalArgs, args, diagnostics, seenStandaloneFlags);

        return new CopilotCliArgumentBuildResult(args, diagnostics);
    }

    internal static string? TryExtractRejectedOption(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return null;
        }

        const string marker = "unknown option '";
        var markerIndex = rawMessage.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        markerIndex += marker.Length;
        var endIndex = rawMessage.IndexOf('\'', markerIndex);
        if (endIndex <= markerIndex)
        {
            return null;
        }

        return rawMessage[markerIndex..endIndex];
    }

    internal static string DescribeUnknownOption(string rawMessage)
    {
        var rejectedOption = TryExtractRejectedOption(rawMessage);
        if (string.IsNullOrWhiteSpace(rejectedOption))
        {
            return $"[cli_argument_incompatible] {rawMessage}";
        }

        return $"[cli_argument_incompatible] Copilot CLI rejected startup option '{rejectedOption}'. {rawMessage}";
    }

    /// <summary>
    /// Determines whether the specified Copilot startup flag is supported without a separate value token.
    /// </summary>
    /// <param name="flag">The raw CLI flag.</param>
    /// <returns><see langword="true" /> when the flag is supported as a standalone switch.</returns>
    public static bool IsSupportedStandaloneFlag(string flag) => SupportedStandaloneFlags.Contains(flag);

    /// <summary>
    /// Determines whether the specified Copilot startup flag is supported with one or more value tokens.
    /// </summary>
    /// <param name="flag">The raw CLI flag.</param>
    /// <param name="valueCount">The number of value tokens the flag consumes when supported.</param>
    /// <returns><see langword="true" /> when the flag is supported with a fixed value count.</returns>
    public static bool TryGetSupportedValueFlagArity(string flag, out int valueCount)
        => SupportedValueFlags.TryGetValue(flag, out valueCount);

    private static void AddDerivedArguments(
        CopilotOptions options,
        List<string> args,
        HashSet<string> seenStandaloneFlags)
    {
        if (options.Permissions.AllowAllTools)
        {
            AddStandaloneFlag(args, seenStandaloneFlags, "--allow-all-tools");
        }

        if (options.Permissions.AllowAllPaths)
        {
            AddStandaloneFlag(args, seenStandaloneFlags, "--allow-all-paths");
        }

        if (options.Permissions.AllowAllUrls)
        {
            AddStandaloneFlag(args, seenStandaloneFlags, "--allow-all-urls");
        }

        if (options.NoAskUser)
        {
            AddStandaloneFlag(args, seenStandaloneFlags, "--no-ask-user");
        }

        foreach (var allowedPath in options.Permissions.AllowedPaths.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            AddFlagWithValue(args, "--add-dir", allowedPath.Trim());
        }

        foreach (var allowedTool in options.Permissions.AllowedTools.Where(static tool => !string.IsNullOrWhiteSpace(tool)))
        {
            AddFlagWithValue(args, "--available-tools", allowedTool.Trim());
        }

        foreach (var deniedTool in options.Permissions.DeniedTools.Where(static tool => !string.IsNullOrWhiteSpace(tool)))
        {
            AddFlagWithValue(args, "--deny-tool", deniedTool.Trim());
        }

        foreach (var deniedUrl in options.Permissions.DeniedUrls.Where(static url => !string.IsNullOrWhiteSpace(url)))
        {
            AddFlagWithValue(args, "--deny-url", deniedUrl.Trim());
        }
    }

    private static void AddConfiguredArguments(
        IEnumerable<string> additionalArgs,
        List<string> args,
        List<string> diagnostics,
        HashSet<string> seenStandaloneFlags)
    {
        var tokens = additionalArgs
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToArray();

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith("-", StringComparison.Ordinal))
            {
                diagnostics.Add($"Copilot CLI startup argument '{token}' was ignored because it is not attached to a supported option.");
                continue;
            }

            if (TrySplitOptionAssignment(token, out var assignedFlag, out var assignedValue))
            {
                if (RejectedFlags.TryGetValue(assignedFlag, out var rejectedReason))
                {
                    diagnostics.Add(CreateRejectedArgumentMessage(assignedFlag, rejectedReason));
                    continue;
                }

                if (SupportedValueFlags.ContainsKey(assignedFlag))
                {
                    AddFlagWithValue(args, assignedFlag, assignedValue);
                    continue;
                }

                diagnostics.Add($"Copilot CLI startup argument '{assignedFlag}' was ignored because inline values are not supported for this option in HagiCode startup filtering.");
                continue;
            }

            if (RejectedFlags.TryGetValue(token, out var reason))
            {
                diagnostics.Add(CreateRejectedArgumentMessage(token, reason));
                if (SupportedValueFlags.TryGetValue(token, out var rejectedValueCount))
                {
                    index += SkipFlagValues(tokens, index, rejectedValueCount);
                }

                continue;
            }

            if (SupportedStandaloneFlags.Contains(token))
            {
                AddStandaloneFlag(args, seenStandaloneFlags, token);
                continue;
            }

            if (SupportedValueFlags.TryGetValue(token, out var valueCount))
            {
                if (!TryReadFlagValues(tokens, index, valueCount, out var values))
                {
                    diagnostics.Add($"Copilot CLI startup argument '{token}' was ignored because it is missing its required value.");
                    continue;
                }

                foreach (var value in values)
                {
                    AddFlagWithValue(args, token, value);
                }

                index += valueCount;
                continue;
            }

            diagnostics.Add($"Copilot CLI startup argument '{token}' was ignored because it is not present in the verified 'copilot --help' support matrix.");
        }
    }

    private static int SkipFlagValues(IReadOnlyList<string> tokens, int currentIndex, int valueCount)
    {
        var skipped = 0;
        for (var offset = 1; offset <= valueCount && currentIndex + offset < tokens.Count; offset++)
        {
            if (tokens[currentIndex + offset].StartsWith("-", StringComparison.Ordinal))
            {
                break;
            }

            skipped++;
        }

        return skipped;
    }

    private static bool TryReadFlagValues(
        IReadOnlyList<string> tokens,
        int currentIndex,
        int valueCount,
        out string[] values)
    {
        var collectedValues = new List<string>(valueCount);
        for (var offset = 1; offset <= valueCount; offset++)
        {
            if (currentIndex + offset >= tokens.Count)
            {
                values = [];
                return false;
            }

            var valueToken = tokens[currentIndex + offset];
            if (valueToken.StartsWith("-", StringComparison.Ordinal))
            {
                values = [];
                return false;
            }

            collectedValues.Add(valueToken);
        }

        values = [.. collectedValues];
        return true;
    }

    private static bool TrySplitOptionAssignment(string token, out string flag, out string value)
    {
        var equalsIndex = token.IndexOf('=');
        if (equalsIndex <= 0)
        {
            flag = string.Empty;
            value = string.Empty;
            return false;
        }

        flag = token[..equalsIndex];
        value = token[(equalsIndex + 1)..];
        return true;
    }

    private static string CreateRejectedArgumentMessage(string flag, string reason)
        => $"Copilot CLI startup argument '{flag}' was rejected because {reason}.";

    private static void AddStandaloneFlag(
        List<string> args,
        HashSet<string> seenStandaloneFlags,
        string flag)
    {
        if (!seenStandaloneFlags.Add(flag))
        {
            return;
        }

        args.Add(flag);
    }

    private static void AddFlagWithValue(List<string> args, string flag, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        args.Add(flag);
        args.Add(value);
    }
}
