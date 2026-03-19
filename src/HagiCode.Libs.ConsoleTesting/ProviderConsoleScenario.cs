namespace HagiCode.Libs.ConsoleTesting;

public sealed record ProviderConsoleScenario<TProvider>(
    string Name,
    string Description,
    Func<TProvider, CancellationToken, Task<ProviderConsoleScenarioResult>> ExecuteAsync,
    bool Required = true)
    where TProvider : notnull;
