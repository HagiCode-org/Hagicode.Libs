namespace HagiCode.Libs.ConsoleTesting;

public sealed record ProviderConsoleReport(
    IReadOnlyList<ProviderConsoleScenarioResult> Results,
    string ProviderName,
    string SuiteName)
{
    public int TotalCount => Results.Count;

    public int PassedCount => Results.Count(static result => result.Success);

    public int FailedCount => Results.Count(static result => !result.Success);

    public int RequiredFailedCount => Results.Count(static result => result.Required && !result.Success);

    public int OptionalFailedCount => Results.Count(static result => !result.Required && !result.Success);

    public long TotalElapsedMs => Results.Sum(static result => result.ElapsedMs);

    public bool IsSuccess => RequiredFailedCount == 0;
}
