using HagiCode.Libs.Core.Discovery;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CliInstallRegistryWindowsPolicyTests
{
    [Test]
    public void Only_claude_copilot_and_codex_require_a_native_executable_on_windows()
    {
        var required = CliInstallRegistry.Descriptors
            .Where(static descriptor => descriptor.WindowsExecutablePolicy == CliWindowsExecutablePolicy.RequireNative)
            .Select(static descriptor => descriptor.ProviderName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        required.ShouldBe(["ClaudeCode", "Codex", "Copilot"]);
    }

    [Test]
    public void Every_other_registered_agent_prefers_a_native_executable_on_windows()
    {
        var preferred = CliInstallRegistry.Descriptors
            .Where(static descriptor => descriptor.WindowsExecutablePolicy == CliWindowsExecutablePolicy.PreferNative)
            .Select(static descriptor => descriptor.ProviderName)
            .ToArray();

        preferred.ShouldContain("OpenCode");
        preferred.ShouldContain("Pi");
        preferred.ShouldNotContain("ClaudeCode");
        preferred.ShouldNotContain("Copilot");
        preferred.ShouldNotContain("Codex");
    }

    [Test]
    public void The_three_native_agents_and_the_two_promotion_candidates_have_packaged_binary_layouts()
    {
        var layouts = CliInstallRegistry.Descriptors
            .Where(static descriptor => descriptor.PackagedBinaryLayout is not null)
            .Select(static descriptor => descriptor.ProviderName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        layouts.ShouldBe(["ClaudeCode", "Codex", "Copilot", "OpenCode"]);
    }

    [Test]
    public void Every_descriptor_records_a_non_empty_windows_evidence_note()
    {
        var missing = CliInstallRegistry.Descriptors
            .Where(static descriptor => string.IsNullOrWhiteSpace(descriptor.WindowsExecutableEvidence))
            .Select(static descriptor => descriptor.ProviderName)
            .ToArray();

        missing.ShouldBeEmpty();
    }

    [Test]
    public void FindByProviderName_matches_regardless_of_case()
    {
        CliInstallRegistry.FindByProviderName("qodercli")!.ProviderName.ShouldBe("QoderCLI");
        CliInstallRegistry.FindByProviderName("NotARegisteredAgent").ShouldBeNull();
    }
}
