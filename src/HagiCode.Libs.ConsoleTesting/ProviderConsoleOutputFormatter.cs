using HagiCode.Libs.Providers;

namespace HagiCode.Libs.ConsoleTesting;

public sealed class ProviderConsoleOutputFormatter
{
    private readonly TextWriter _output;

    public ProviderConsoleOutputFormatter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void WritePingResult(CliProviderTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        WriteResultLine(result.Success, result.ProviderName, "Ping", 0);
        if (!string.IsNullOrWhiteSpace(result.Version))
        {
            _output.WriteLine($"  Version: {result.Version}");
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            _output.WriteLine($"  Error: {result.ErrorMessage}");
        }
    }

    public void WriteReport(ProviderConsoleReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (var result in report.Results)
        {
            WriteResult(result);
        }

        _output.WriteLine();
        var summary = $"Summary: {report.PassedCount}/{report.TotalCount} passed";
        if (report.RequiredFailedCount > 0 || report.OptionalFailedCount > 0)
        {
            summary += $", required failures: {report.RequiredFailedCount}, optional failures: {report.OptionalFailedCount}";
        }

        _output.WriteLine(summary);
    }

    public void WriteResult(ProviderConsoleScenarioResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        WriteResultLine(result.Success, result.ProviderName, result.ScenarioName, result.ElapsedMs);
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            _output.WriteLine($"  Error: {result.ErrorMessage}");
        }
    }

    private void WriteResultLine(bool success, string providerName, string scenarioName, long elapsedMs)
    {
        var status = success ? "PASS" : "FAIL";
        var duration = elapsedMs > 0 ? $" ({elapsedMs}ms)" : string.Empty;
        _output.WriteLine($"[{status}] {providerName} / {scenarioName}{duration}");
    }
}
