using HagiCode.Libs.Core.Discovery;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CliInstallRegistryTests
{
    [Fact]
    public void Descriptors_include_copilot_with_public_install_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Copilot");

        descriptor.NpmPackage.ShouldBe("@github/copilot");
        descriptor.PinnedVersion.ShouldBe("1.0.10");
        descriptor.ExecutableCandidates.ShouldBe(["copilot"]);
        descriptor.IsPubliclyInstallable.ShouldBeTrue();
    }

    [Fact]
    public void Descriptors_include_kimi_with_explicit_local_only_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Kimi");

        descriptor.NpmPackage.ShouldBeEmpty();
        descriptor.PinnedVersion.ShouldBeEmpty();
        descriptor.ExecutableCandidates.ShouldBe(["kimi", "kimi-cli"]);
        descriptor.IsPubliclyInstallable.ShouldBeFalse();
    }

    [Fact]
    public void Descriptors_include_kiro_with_explicit_local_only_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Kiro");

        descriptor.NpmPackage.ShouldBeEmpty();
        descriptor.PinnedVersion.ShouldBeEmpty();
        descriptor.ExecutableCandidates.ShouldBe(["kiro", "kiro-cli"]);
        descriptor.IsPubliclyInstallable.ShouldBeFalse();
    }

    [Fact]
    public void PubliclyInstallable_matrix_contains_claude_code_copilot_and_codex_only()
    {
        CliInstallRegistry.PubliclyInstallable
            .Select(static descriptor => descriptor.ProviderName)
            .ShouldBe(["ClaudeCode", "Copilot", "Codex"], ignoreOrder: true);
    }
}
