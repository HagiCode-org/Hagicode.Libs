using HagiCode.Libs.Core.Tools;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ToolCallFixtureTests
{
    public static IEnumerable<object[]> ToolCallEmittingProviders() =>
        ToolCallFixtureCatalog.ToolCallEmittingProviders.OrderBy(static name => name, StringComparer.Ordinal).Select(static name => new object[] { name });

    [TestCaseSource(nameof(ToolCallEmittingProviders))]
    public void Fixtures_replay_to_their_expected_tool_kinds(string provider)
    {
        var files = ToolCallFixtureFile.LoadProvider(provider);
        files.ShouldNotBeEmpty($"Fixtures/ToolCalls/{provider}/ must hold at least one fixture");

        foreach (var file in files)
        {
            file.ExpectedToolKinds.ShouldNotBeEmpty(file.Path);
            var observations = ToolCallFixtureReplay.Replay(provider, file);

            foreach (var (toolCallId, expectedKind) in file.ExpectedToolKinds)
            {
                observations.ShouldContain(
                    observation => observation.ToolCallId == toolCallId && observation.Kind == expectedKind,
                    $"{file.Path}: tool call '{toolCallId}' should resolve to kind '{expectedKind}'");
            }

            observations.ShouldAllBe(
                observation => observation.Kind != null && ToolKinds.All.Contains(observation.Kind),
                $"{file.Path}: every replayed tool message must carry a vocabulary kind");
        }
    }

    [Test]
    public void Reasonix_write_file_resolves_to_write_and_dsh_bash_resolves_to_shell()
    {
        var reasonix = ToolCallFixtureFile.LoadProvider("reasonix").Single();
        ToolCallFixtureReplay.Replay("reasonix", reasonix)
            .ShouldContain(observation => observation.ToolCallId == "toolu_01E9UTbLGHddn5vpsziLZM37" && observation.Kind == ToolKinds.Write);

        var dsh = ToolCallFixtureFile.LoadProvider("dsh").Single();
        ToolCallFixtureReplay.Replay("dsh", dsh)
            .ShouldContain(observation => observation.ToolCallId == "tool-call-1" && observation.Kind == ToolKinds.Shell);
    }
}
