using HagiCode.Libs.Core.Environment;
using Moq;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Environment;

public sealed class RuntimeEnvironmentResolverTests
{
    private const string AgentCliPathEnvironmentVariable = "HAGICODE_AGENT_CLI_PATH";

    [Test]
    [Category("CriticalRegression")]
    public async Task ResolveAsync_returns_current_environment_when_not_on_macos()
    {
        var originalValue = System.Environment.GetEnvironmentVariable(AgentCliPathEnvironmentVariable);
        try
        {
            System.Environment.SetEnvironmentVariable(AgentCliPathEnvironmentVariable, "/tmp/custom-agent-cli");

            var runner = new Mock<IShellCommandRunner>(MockBehavior.Strict);
            var resolver = new RuntimeEnvironmentResolver(
                runner.Object,
                new RuntimeEnvironmentOptions(),
                TimeProvider.System,
                static () => false);

            var environment = await resolver.ResolveAsync();

            environment.ContainsKey("PATH").ShouldBeTrue();
            environment[AgentCliPathEnvironmentVariable].ShouldBe("/tmp/custom-agent-cli");
            runner.VerifyNoOtherCalls();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(AgentCliPathEnvironmentVariable, originalValue);
        }
    }

    [Test]
    public async Task ResolveAsync_uses_shell_output_on_macos_and_caches_result()
    {
        var customPathValue = string.Join(Path.PathSeparator, ["/tmp/agent-a", "/tmp/agent-b"]);
        var shellOutput = string.Join(
            '\0',
            ["FOO=bar", "PATH=/tmp/bin", $"{AgentCliPathEnvironmentVariable}={customPathValue}", string.Empty]);

        var runner = new Mock<IShellCommandRunner>(MockBehavior.Strict);
        runner.Setup(static mock => mock.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShellCommandResult(0, shellOutput, string.Empty));

        var resolver = new RuntimeEnvironmentResolver(
            runner.Object,
            new RuntimeEnvironmentOptions { CacheDuration = TimeSpan.FromSeconds(30) },
            TimeProvider.System,
            static () => true,
            static () => "/bin/zsh");

        var first = await resolver.ResolveAsync();
        var second = await resolver.ResolveAsync();

        first["FOO"].ShouldBe("bar");
        first[AgentCliPathEnvironmentVariable].ShouldBe(customPathValue);
        second["FOO"].ShouldBe("bar");
        runner.Verify(static mock => mock.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ResolveAsync_falls_back_when_shell_command_fails()
    {
        var runner = new Mock<IShellCommandRunner>();
        runner.Setup(static mock => mock.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShellCommandResult(1, string.Empty, "boom"));

        var resolver = new RuntimeEnvironmentResolver(
            runner.Object,
            new RuntimeEnvironmentOptions(),
            TimeProvider.System,
            static () => true,
            static () => "/bin/zsh");

        var environment = await resolver.ResolveAsync();

        environment.ContainsKey("PATH").ShouldBeTrue();
    }
}
