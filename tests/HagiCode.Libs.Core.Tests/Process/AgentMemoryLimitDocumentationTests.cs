using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Tests.Process;

public sealed class AgentMemoryLimitDocumentationTests
{
    private const string SectionHeading = "## Agent memory limits (Linux)";

    [Test]
    public void The_README_example_produces_the_documented_systemd_run_arguments_and_node_options()
    {
        var section = ReadSection();
        var settingLine = section.Single(static line => line.StartsWith(CliResourceLimits.EnvironmentVariableName + "=", StringComparison.Ordinal));
        var commandLine = section.Single(static line => line.StartsWith("systemd-run ", StringComparison.Ordinal));
        var nodeOptionsLine = section.Single(static line => line.StartsWith("NODE_OPTIONS=", StringComparison.Ordinal));

        CliResourceLimits.TryParseSize(settingLine[(CliResourceLimits.EnvironmentVariableName.Length + 1)..], out var limits).ShouldBeTrue();
        limits.ShouldNotBeNull();

        var documentedArguments = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        documentedArguments.Take(documentedArguments.IndexOf("--") + 1).ShouldBe(limits.SystemdRunArguments);
        nodeOptionsLine["NODE_OPTIONS=".Length..].ShouldBe(CgroupScopeLauncher.MergeNodeOptions(null, limits.NodeHeapOption!));
    }

    private static IReadOnlyList<string> ReadSection()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HagiCode.Libs.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("Could not locate the HagiCode.Libs solution root from the test assembly.");
        var lines = File.ReadAllLines(Path.Combine(directory.FullName, "README.md"));
        var start = Array.IndexOf(lines, SectionHeading);
        start.ShouldBeGreaterThanOrEqualTo(0, $"README has no '{SectionHeading}' section.");

        return lines
            .Skip(start + 1)
            .TakeWhile(static line => !line.StartsWith("## ", StringComparison.Ordinal))
            .Select(static line => line.Trim())
            .ToList();
    }
}
