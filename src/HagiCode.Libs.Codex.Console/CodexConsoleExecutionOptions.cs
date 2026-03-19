using HagiCode.Libs.Providers.Codex;

namespace HagiCode.Libs.Codex.Console;

public sealed record CodexConsoleExecutionOptions(
    string? ApiKey,
    string? BaseUrl,
    string? Model,
    string? SandboxMode,
    string? ApprovalPolicy,
    string? RepositoryPath)
{
    private static readonly HashSet<string> AllowedApprovalPolicies =
    [
        "never",
        "on-request",
        "on-failure",
        "untrusted"
    ];

    private static readonly HashSet<string> AllowedSandboxModes =
    [
        "read-only",
        "workspace-write",
        "danger-full-access"
    ];

    public static CodexConsoleExecutionOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? apiKey = null;
        string? baseUrl = null;
        string? model = null;
        string? sandboxMode = null;
        string? approvalPolicy = null;
        string? repositoryPath = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--api-key":
                    apiKey = ReadValue(args, ref index, argument);
                    break;
                case "--base-url":
                    baseUrl = ReadValue(args, ref index, argument);
                    break;
                case "--model":
                    model = ReadValue(args, ref index, argument);
                    break;
                case "--sandbox":
                    sandboxMode = ReadValue(args, ref index, argument);
                    ValidateSandboxMode(sandboxMode);
                    break;
                case "--approval-policy":
                    approvalPolicy = ReadValue(args, ref index, argument);
                    ValidateApprovalPolicy(approvalPolicy);
                    break;
                case "--repo":
                    repositoryPath = ReadValue(args, ref index, argument);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {argument}");
            }
        }

        return new CodexConsoleExecutionOptions(apiKey, baseUrl, model, sandboxMode, approvalPolicy, repositoryPath);
    }

    public CodexOptions CreateBaseOptions()
    {
        return new CodexOptions
        {
            ApiKey = ApiKey,
            BaseUrl = BaseUrl,
            Model = Model,
            SandboxMode = SandboxMode,
            ApprovalPolicy = ApprovalPolicy,
            SkipGitRepositoryCheck = true,
        };
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string flag)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{flag} requires a value.");
        }

        index++;
        return args[index];
    }

    private static void ValidateApprovalPolicy(string value)
    {
        if (!AllowedApprovalPolicies.Contains(value))
        {
            throw new ArgumentException($"Unsupported approval policy: {value}");
        }
    }

    private static void ValidateSandboxMode(string value)
    {
        if (!AllowedSandboxModes.Contains(value))
        {
            throw new ArgumentException($"Unsupported sandbox mode: {value}");
        }
    }
}
