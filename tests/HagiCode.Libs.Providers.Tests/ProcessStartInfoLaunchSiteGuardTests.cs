using System.Text.RegularExpressions;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Every <c>new ProcessStartInfo</c> in the provider project starts an agent CLI that must pass through
/// <c>CgroupScopeLauncher.Apply</c>, or is a non-agent launch listed below with its reason. A new launcher therefore cannot
/// skip the memory limit unnoticed. A file passes when it has at least as many <c>CgroupScopeLauncher.Apply</c> calls as
/// <c>new ProcessStartInfo</c> sites, or exactly the number of exempt sites listed for it.
/// </summary>
public sealed partial class ProcessStartInfoLaunchSiteGuardTests
{
    private const string ProviderProjectDirectory = "src/HagiCode.Libs.Providers";

    // Paths are relative to the provider project, with forward slashes.
    private static readonly IReadOnlyDictionary<string, (int Sites, string Reason)> Exemptions = new Dictionary<string, (int, string)>(StringComparer.Ordinal)
    {
        ["Codex/Vendored/Internal/CodexCliMetadataReader.cs"] = (2, "Reads `codex --version` and `npm view` metadata; neither is an agent run."),
    };

    [Test]
    public void Every_ProcessStartInfo_in_the_provider_project_is_wrapped_or_exempt()
    {
        var offenders = FindUnwrappedSites(FindProviderProjectRoot());

        offenders.ShouldBeEmpty(
            "These files build a ProcessStartInfo without CgroupScopeLauncher.Apply. Wrap the launch, or add the file to the "
            + "exemption list with a reason if it is not an agent CLI: " + string.Join("; ", offenders));
    }

    [Test]
    public void Every_exemption_still_matches_its_file()
    {
        var root = FindProviderProjectRoot();
        foreach (var (relativePath, exemption) in Exemptions)
        {
            var path = Path.Combine(root, relativePath);
            File.Exists(path).ShouldBeTrue($"Exempt file {relativePath} no longer exists; remove it from the list.");
            ProcessStartInfoSite().Matches(File.ReadAllText(path)).Count.ShouldBe(exemption.Sites, $"Exempt site count for {relativePath}");
            exemption.Reason.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Test]
    public void The_guard_sees_the_known_wrapped_launchers()
    {
        var root = FindProviderProjectRoot();

        foreach (var relativePath in new[]
                 {
                     "Codex/Vendored/Execution/CodexExec.cs",
                     "Codex/AppServer/CodexAppServerProcess.cs",
                     "OpenCode/OpenCodeProcessLauncher.cs"
                 })
        {
            var source = File.ReadAllText(Path.Combine(root, relativePath));
            ProcessStartInfoSite().Matches(source).Count.ShouldBeGreaterThan(0, relativePath);
            LauncherCall().Matches(source).Count.ShouldBeGreaterThan(0, relativePath);
        }
    }

    internal static IReadOnlyList<string> FindUnwrappedSites(string providerProjectRoot)
    {
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(providerProjectRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(providerProjectRoot, path).Replace('\\', '/');
            if (relativePath.StartsWith("bin/", StringComparison.Ordinal) || relativePath.StartsWith("obj/", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            var sites = ProcessStartInfoSite().Matches(source).Count;
            if (sites == 0)
            {
                continue;
            }

            if (Exemptions.TryGetValue(relativePath, out var exemption) && exemption.Sites == sites)
            {
                continue;
            }

            var wrappedCalls = LauncherCall().Matches(source).Count;
            if (wrappedCalls < sites)
            {
                offenders.Add($"{relativePath} ({sites} site(s), {wrappedCalls} CgroupScopeLauncher.Apply call(s))");
            }
        }

        return offenders;
    }

    private static string FindProviderProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HagiCode.Libs.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("Could not locate the HagiCode.Libs solution root from the test assembly.");
        return Path.Combine(directory.FullName, ProviderProjectDirectory);
    }

    [GeneratedRegex(@"\bnew\s+(?:System\.Diagnostics\.)?ProcessStartInfo\s*[({]")]
    private static partial Regex ProcessStartInfoSite();

    [GeneratedRegex(@"\bCgroupScopeLauncher\.Apply\s*\(")]
    private static partial Regex LauncherCall();
}
