namespace HagiCode.Libs.ConsoleTesting;

public sealed record ProviderConsoleScenarioResult(
    string ProviderName,
    string ScenarioName,
    bool Success,
    long ElapsedMs,
    bool Required = true,
    string? ErrorMessage = null,
    IReadOnlyList<string>? DetailLines = null);
