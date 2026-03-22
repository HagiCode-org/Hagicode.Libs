namespace HagiCode.Libs.Providers.Copilot;

internal sealed record CopilotSdkRequest(
    string Prompt,
    string? Model,
    string? WorkingDirectory,
    string? SessionId,
    string CliPath,
    string? CliUrl,
    string? GitHubToken,
    bool UseLoggedInUser,
    TimeSpan Timeout,
    TimeSpan StartupTimeout,
    IReadOnlyList<string> CliArgs,
    IReadOnlyDictionary<string, string?> EnvironmentVariables);
