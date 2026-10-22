using System.Runtime.InteropServices;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

/// <summary>
/// Task 2.4 and 2.6: the public constructors share one found-result store, and the discovery service and the process manager
/// reuse it instead of walking the search path again.
/// </summary>
public sealed class CliResolutionSharingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    [SetUp]
    public void ClearSharedStore() => CliExecutableResolver.ClearSharedResolutionCache();

    [Test]
    public void Two_separately_constructed_public_resolvers_share_one_found_result()
    {
        var root = Path.Combine(Path.GetTempPath(), "hagicode-libs-shared-" + Guid.NewGuid().ToString("N"));
        var dirA = Path.Combine(root, "a");
        var dirB = Path.Combine(root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var name = "hc-shared-" + Guid.NewGuid().ToString("N")[..12];
        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        try
        {
            File.WriteAllText(Path.Combine(dirB, fileName), "x");
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PATH"] = dirA + Path.PathSeparator + dirB,
                ["HAGICODE_AGENT_CLI_PATH"] = string.Empty,
            };

            var first = new CliExecutableResolver().ResolveExecutablePath(name, environment);
            File.WriteAllText(Path.Combine(dirA, fileName), "x");
            var second = new CliExecutableResolver().ResolveExecutablePath(name, environment);

            first.ShouldBe(Path.Combine(dirB, fileName));
            second.ShouldBe(first, "the second instance reuses the first instance's found result, so the newer copy in the earlier directory is not seen yet");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void A_second_discovery_of_an_installed_cli_lists_no_well_known_directory_again()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/bin/tool.exe");
        var resolver = new CliExecutableResolver(static () => true, static () => Architecture.X64, new ManualTimeProvider(Start), fs);
        // The resolver is Windows, so the directory lookup lists directories. The discovery platform stays Linux, which expands the
        // absolute well-known directory verbatim instead of rewriting it as a Windows path.
        var service = new CliDiscoveryService(
            resolver,
            new CliProcessManager(),
            runtimeEnvironmentResolver: null,
            platform: static () => CliPlatform.Linux);
        var descriptor = new CliInstallDescriptor(
            ProviderName: "Tool",
            NpmPackage: string.Empty,
            PinnedVersion: string.Empty,
            ExecutableCandidates: ["tool"],
            IsPubliclyInstallable: false)
        {
            InstallStrategies = [new CliInstallStrategy(CliInstallKind.NativeInstaller, CliPlatform.All, ["/win/bin"], SupportsVersionPin: false)],
        };
        var options = new CliDiscoveryOptions
        {
            EnvironmentVariables = new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = string.Empty, ["PATHEXT"] = ".EXE;.CMD;.BAT" },
        };

        var first = service.DiscoverAsync(descriptor, options).GetAwaiter().GetResult();
        fs.ResetCounts();
        var second = service.DiscoverAsync(descriptor, options).GetAwaiter().GetResult();

        first.IsFound.ShouldBeTrue();
        second.ResolvedPath.ShouldBe(first.ResolvedPath);
        fs.ListingsOf("/win/bin").ShouldBe(0, "the well-known directory lookup is served from the store");
    }

    [Test]
    public void A_second_launch_of_a_bare_command_name_performs_only_the_existence_check()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: false);
        fs.AddFile("/bin/tool");
        var resolver = new CliExecutableResolver(static () => false, static () => Architecture.X64, new ManualTimeProvider(Start), fs);
        var manager = new CliProcessManager(
            resolver,
            new CliOwnedProcessRegistry(),
            new CliProcessOwnershipOptions(),
            TimeProvider.System,
            NullLogger<CliProcessManager>.Instance);
        var context = new ProcessStartContext
        {
            ExecutablePath = "tool",
            EnvironmentVariables = new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = "/bin" },
        };

        var first = manager.CreateStartPlan(context);
        fs.ResetCounts();
        var second = manager.CreateStartPlan(context);

        first.StartInfo.FileName.ShouldBe("/bin/tool");
        second.StartInfo.FileName.ShouldBe("/bin/tool");
        fs.FileExistsCalls.ShouldBe(1);
        fs.ListFilesCalls.ShouldBe(0);
        fs.DirectoryExistsCalls.ShouldBe(0);
    }
}
