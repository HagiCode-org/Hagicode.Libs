using System.Text.RegularExpressions;
using HagiCode.Libs.Core.Discovery;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed partial class CliInstallRegistryTests
{
    [Test]
    public void Descriptors_include_copilot_with_public_install_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Copilot");

        descriptor.NpmPackage.ShouldBe("@github/copilot");
        descriptor.PinnedVersion.ShouldBe("1.0.60");
        descriptor.ExecutableCandidates.ShouldBe(["copilot"]);
        descriptor.IsPubliclyInstallable.ShouldBeTrue();
    }

    [Test]
    public void Descriptors_include_kimi_with_explicit_local_only_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Kimi");

        descriptor.NpmPackage.ShouldBeEmpty();
        descriptor.PinnedVersion.ShouldBeEmpty();
        descriptor.ExecutableCandidates.ShouldBe(["kimi", "kimi-cli"]);
        descriptor.IsPubliclyInstallable.ShouldBeFalse();
    }

    [Test]
    public void Descriptors_include_gemini_with_explicit_local_only_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Gemini");

        descriptor.NpmPackage.ShouldBeEmpty();
        descriptor.PinnedVersion.ShouldBeEmpty();
        descriptor.ExecutableCandidates.ShouldBe(["gemini", "gemini-cli"]);
        descriptor.IsPubliclyInstallable.ShouldBeFalse();
    }

    [Test]
    public void Descriptors_include_junie_with_explicit_local_only_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Junie");

        descriptor.NpmPackage.ShouldBeEmpty();
        descriptor.PinnedVersion.ShouldBeEmpty();
        descriptor.ExecutableCandidates.ShouldBe(["junie", "junie-cli"]);
        descriptor.IsPubliclyInstallable.ShouldBeFalse();
    }

    [Test]
    public void Descriptors_include_dsh_with_explicit_local_only_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Dsh");

        descriptor.NpmPackage.ShouldBeEmpty();
        descriptor.PinnedVersion.ShouldBeEmpty();
        descriptor.ExecutableCandidates.ShouldBe(["dsh", "dsh-cli"]);
        descriptor.IsPubliclyInstallable.ShouldBeFalse();
    }

    [Test]
    public void Descriptors_include_deepagents_with_explicit_local_only_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "DeepAgents");

        descriptor.NpmPackage.ShouldBeEmpty();
        descriptor.PinnedVersion.ShouldBeEmpty();
        descriptor.ExecutableCandidates.ShouldBe(["deepagents"]);
        descriptor.IsPubliclyInstallable.ShouldBeFalse();
    }

    [Test]
    public void Descriptors_include_kiro_with_explicit_local_only_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Kiro");

        descriptor.NpmPackage.ShouldBeEmpty();
        descriptor.PinnedVersion.ShouldBeEmpty();
        descriptor.ExecutableCandidates.ShouldBe(["kiro-cli"]);
        descriptor.IsPubliclyInstallable.ShouldBeFalse();
    }

    [Test]
    public void Descriptors_include_reasonix_with_explicit_local_only_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Reasonix");

        descriptor.NpmPackage.ShouldBeEmpty();
        descriptor.PinnedVersion.ShouldBeEmpty();
        descriptor.ExecutableCandidates.ShouldBe(["reasonix"]);
        descriptor.IsPubliclyInstallable.ShouldBeFalse();
    }

    [Test]
    public void Descriptors_include_pi_with_published_package_but_local_only_and_unpinned()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Pi");

        descriptor.NpmPackage.ShouldBe("@earendil-works/pi-coding-agent");
        descriptor.PinnedVersion.ShouldBeEmpty();
        descriptor.ExecutableCandidates.ShouldBe(["pi", "pi-cli"]);
        descriptor.IsPubliclyInstallable.ShouldBeFalse();
    }

    [Test]
    public void Descriptors_include_omp_with_explicit_local_only_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "Omp");

        descriptor.NpmPackage.ShouldBeEmpty();
        descriptor.PinnedVersion.ShouldBeEmpty();
        descriptor.ExecutableCandidates.ShouldBe(["omp", "omp-cli"]);
        descriptor.IsPubliclyInstallable.ShouldBeFalse();
    }

    [Test]
    public void Descriptors_include_opencode_with_public_install_metadata()
    {
        var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "OpenCode");

        descriptor.NpmPackage.ShouldBe("opencode-ai");
        descriptor.PinnedVersion.ShouldBe("1.3.3");
        descriptor.ExecutableCandidates.ShouldBe(["opencode"]);
        descriptor.IsPubliclyInstallable.ShouldBeTrue();
    }

    [Test]
    public void PubliclyInstallable_matrix_contains_claude_code_copilot_codex_and_opencode_only()
    {
        CliInstallRegistry.PubliclyInstallable
            .Select(static descriptor => descriptor.ProviderName)
            .ShouldBe(["ClaudeCode", "Copilot", "Codex", "OpenCode"], ignoreOrder: true);
    }

    [Test]
    public void README_registry_table_matches_the_registry()
    {
        var rows = ParseRegistryTable(File.ReadAllText(FindReadme()));

        rows.Count.ShouldBeGreaterThan(0);
        foreach (var row in rows)
        {
            var descriptor = CliInstallRegistry.Descriptors.SingleOrDefault(candidate =>
                string.Equals(candidate.ProviderName, row.Provider.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase));
            descriptor.ShouldNotBeNull($"README lists provider '{row.Provider}' that the registry does not declare.");

            var expectedPackage = BacktickedPackage().Match(row.Package) is { Success: true } packageMatch
                ? packageMatch.Groups[1].Value
                : string.Empty;
            descriptor.NpmPackage.ShouldBe(expectedPackage, $"npm package for {row.Provider} in README");

            var expectedVersion = row.PinnedVersion is "—" or "-" or "" ? string.Empty : row.PinnedVersion;
            descriptor.PinnedVersion.ShouldBe(expectedVersion, $"pinned version for {row.Provider} in README");
        }
    }

    private sealed record RegistryRow(string Provider, string Package, string PinnedVersion);

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex BacktickedPackage();

    private static IReadOnlyList<RegistryRow> ParseRegistryTable(string readme)
    {
        const string heading = "### CLI install registry";
        var start = readme.IndexOf(heading, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "README has no '### CLI install registry' section.");

        var lines = readme[start..].Split('\n');
        var rows = new List<RegistryRow>();
        var inTable = false;
        foreach (var rawLine in lines.Skip(1))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("### ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            if (!line.StartsWith('|'))
            {
                if (inTable)
                {
                    break;
                }

                continue;
            }

            var cells = line.Trim('|').Split('|').Select(static cell => cell.Trim()).ToArray();
            if (cells.Length < 3 || cells[0] == "Provider" || cells[0].StartsWith("---", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            inTable = true;
            rows.Add(new RegistryRow(cells[0], cells[1], cells[2]));
        }

        return rows;
    }

    private static string FindReadme()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HagiCode.Libs.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("Could not locate the HagiCode.Libs solution root from the test assembly.");
        return Path.Combine(directory.FullName, "README.md");
    }
}
