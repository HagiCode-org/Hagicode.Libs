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

}
