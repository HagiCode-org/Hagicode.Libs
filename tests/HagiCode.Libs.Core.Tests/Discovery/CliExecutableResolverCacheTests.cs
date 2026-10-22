using System.Runtime.InteropServices;
using HagiCode.Libs.Core.Discovery;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

/// <summary>
/// Tasks 2.2 to 2.5: Windows probe order and the bounded directory read, and the found-result cache with its validation,
/// expiry, key isolation and native-executable rules. The file system and the clock are in-memory test doubles, so each
/// existence check and directory read is counted.
/// </summary>
public sealed class CliExecutableResolverCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void Windows_probe_prefers_exe_in_a_later_directory_over_cmd_in_an_earlier_one()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/A/tool.cmd");
        fs.AddFile("/win/B/tool.exe");
        var resolver = WindowsResolver(fs);

        var resolved = resolver.ResolveExecutablePaths("tool", Env(path: Paths("/win/A", "/win/B")));

        resolved.ShouldBe(new[] { "/win/B/tool.exe", "/win/A/tool.cmd" });
    }

    [Test]
    public void Names_that_only_share_a_prefix_do_not_match()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/A/tool.exe.config");
        fs.AddFile("/win/A/tool-extra.exe");
        var resolver = WindowsResolver(fs);

        resolver.ResolveExecutablePath("tool", Env(path: "/win/A")).ShouldBeNull();
    }

    [Test]
    public void Missing_directories_are_skipped()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/B/tool.exe");
        var resolver = WindowsResolver(fs);

        var resolved = resolver.ResolveExecutablePath("tool", Env(path: Paths("/win/missing", "/win/B")));

        resolved.ShouldBe("/win/B/tool.exe");
    }

    [Test]
    public void Unreadable_directories_are_skipped()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/A/tool.exe");
        fs.AddFile("/win/B/tool.exe");
        fs.MarkUnreadable("/win/A");
        var resolver = WindowsResolver(fs);

        var resolved = resolver.ResolveExecutablePaths("tool", Env(path: Paths("/win/A", "/win/B")));

        resolved.ShouldBe(new[] { "/win/B/tool.exe" });
    }

    [Test]
    public void Windows_names_match_case_insensitively()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/A/Claude.EXE");
        var resolver = WindowsResolver(fs);

        resolver.ResolveExecutablePath("claude", Env(path: "/win/A")).ShouldBe("/win/A/claude.exe");
    }

    [Test]
    public void A_second_resolution_reuses_the_first_with_one_existence_check_and_no_directory_read()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/B/tool.exe");
        var clock = new ManualTimeProvider(Start);
        var resolver = WindowsResolver(fs, clock);
        var environment = Env(path: Paths("/win/A", "/win/B"));
        var first = resolver.ResolveExecutablePath("tool", environment);

        fs.ResetCounts();
        var second = resolver.ResolveExecutablePath("tool", environment);

        second.ShouldBe(first);
        fs.FileExistsCalls.ShouldBe(1);
        fs.ListFilesCalls.ShouldBe(0);
        fs.DirectoryExistsCalls.ShouldBe(0);
    }

    [Test]
    public void A_removed_cached_executable_is_found_again_by_a_full_lookup()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/B/tool.exe");
        var resolver = WindowsResolver(fs);
        var environment = Env(path: Paths("/win/A", "/win/B"));
        resolver.ResolveExecutablePath("tool", environment).ShouldBe("/win/B/tool.exe");

        fs.RemoveFile("/win/B/tool.exe");
        fs.AddFile("/win/A/tool.cmd");

        resolver.ResolveExecutablePath("tool", environment).ShouldBe("/win/A/tool.cmd");
    }

    [Test]
    public void A_found_result_expires_after_sixty_seconds_so_a_higher_priority_install_is_seen()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/B/tool.exe");
        var clock = new ManualTimeProvider(Start);
        var resolver = WindowsResolver(fs, clock);
        var environment = Env(path: Paths("/win/A", "/win/B"));
        resolver.ResolveExecutablePath("tool", environment).ShouldBe("/win/B/tool.exe");

        fs.AddFile("/win/A/tool.exe");
        clock.Advance(TimeSpan.FromSeconds(59));
        resolver.ResolveExecutablePath("tool", environment).ShouldBe("/win/B/tool.exe", "the found result is still reused inside its lifetime");

        clock.Advance(TimeSpan.FromSeconds(2));
        resolver.ResolveExecutablePath("tool", environment).ShouldBe("/win/A/tool.exe", "the expired result is replaced by a full lookup");
    }

    [Test]
    public void Keys_isolate_results_when_PATH_PATHEXT_or_HAGICODE_AGENT_CLI_PATH_differ()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/A/tool.exe");
        fs.AddFile("/win/B/tool.exe");
        fs.AddFile("/win/C/tool.exe");
        fs.AddFile("/win/D/tool.com");
        var resolver = WindowsResolver(fs);

        resolver.ResolveExecutablePath("tool", Env(path: "/win/A")).ShouldBe("/win/A/tool.exe");
        resolver.ResolveExecutablePath("tool", Env(path: "/win/B")).ShouldBe("/win/B/tool.exe");
        resolver.ResolveExecutablePath("tool", Env(path: "/win/D", pathExt: ".COM;.EXE")).ShouldBe("/win/D/tool.com");
        resolver.ResolveExecutablePath("tool", Env(path: "/win/D", pathExt: ".EXE")).ShouldBeNull("a PATHEXT without .com must not reuse the .com result");
        resolver.ResolveExecutablePath("tool", Env(path: "/win/B", agentCliPath: "/win/C")).ShouldBe("/win/C/tool.exe");
        resolver.ResolveExecutablePath("tool", Env(path: "/win/B", agentCliPath: "")).ShouldBe("/win/B/tool.exe");
    }

    [Test]
    public void An_absent_path_list_is_found_by_the_very_next_lookup_without_a_clock_advance()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddDirectory("/win/A");
        var resolver = WindowsResolver(fs, new ManualTimeProvider(Start));
        var environment = Env(path: "/win/A");

        resolver.ResolveExecutablePath("tool", environment).ShouldBeNull();
        fs.AddFile("/win/A/tool.exe");

        resolver.ResolveExecutablePath("tool", environment).ShouldBe("/win/A/tool.exe");
    }

    [Test]
    public void An_absent_provider_lookup_is_found_by_the_very_next_lookup_without_a_clock_advance()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddDirectory("/win/A");
        var resolver = WindowsResolver(fs, new ManualTimeProvider(Start));
        var environment = Env(path: "/win/A");

        resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], environment).IsFound.ShouldBeFalse();
        fs.AddFile("/win/A/claude.exe");

        resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], environment).ResolvedPath.ShouldBe("/win/A/claude.exe");
    }

    [Test]
    public void An_absent_directory_lookup_is_found_by_the_very_next_lookup_without_a_clock_advance()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddDirectory("/win/D");
        var resolver = WindowsResolver(fs, new ManualTimeProvider(Start));

        resolver.ResolveExecutablePathsInDirectories("tool", ["/win/D"], Env()).ShouldBeEmpty();
        fs.AddFile("/win/D/tool.exe");

        resolver.ResolveExecutablePathsInDirectories("tool", ["/win/D"], Env()).ShouldBe(new[] { "/win/D/tool.exe" });
    }

    [Test]
    public void Repeated_absent_lookups_leave_the_store_empty()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddDirectory("/win/A");
        var resolver = WindowsResolver(fs, new ManualTimeProvider(Start));
        var environment = Env(path: "/win/A");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            resolver.ResolveExecutablePath("absent", environment);
            resolver.ResolveProviderExecutable("ClaudeCode", ["absent"], environment);
            resolver.ResolveExecutablePathsInDirectories("absent", ["/win/A"], environment);
        }

        resolver.ResolutionCacheCount.ShouldBe(0);
    }

    [Test]
    public void The_store_drops_expired_entries_when_it_passes_512_keys()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        var clock = new ManualTimeProvider(Start);
        var resolver = WindowsResolver(fs, clock);
        var environment = Env(path: "/win/A");
        for (var index = 0; index <= CliResolutionCache.PruneThreshold; index++)
        {
            fs.AddFile($"/win/A/tool{index}.exe");
            resolver.ResolveExecutablePath($"tool{index}", environment);
        }

        clock.Advance(CliResolutionCache.FoundResultLifetime + TimeSpan.FromSeconds(1));
        fs.AddFile("/win/A/last.exe");
        resolver.ResolveExecutablePath("last", environment);

        resolver.ResolutionCacheCount.ShouldBe(1);
    }

    [Test]
    public void A_cached_native_hit_keeps_every_accepted_path()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/A/claude.exe");
        fs.AddFile("/win/B/claude.exe");
        var resolver = WindowsResolver(fs);
        var environment = Env(path: Paths("/win/A", "/win/B"));

        var first = resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], environment);
        var second = resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], environment);

        first.AcceptedPaths.ShouldBe(new[] { "/win/A/claude.exe", "/win/B/claude.exe" });
        second.ResolvedPath.ShouldBe(first.ResolvedPath);
        second.AcceptedPaths.ShouldBe(first.AcceptedPaths);
    }

    [Test]
    public void A_native_required_shim_only_stays_not_found_with_its_skipped_scripts_until_the_packaged_exe_is_placed()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/A/claude.cmd");
        var resolver = WindowsResolver(fs);
        var environment = Env(path: "/win/A");

        var first = resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], environment);
        var second = resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], environment);

        first.NotFoundReason.ShouldBe(CliExecutableNotFoundReason.NativeExecutableMissing);
        first.SkippedCommandScripts.ShouldBe(new[] { "/win/A/claude.cmd" });
        second.NotFoundReason.ShouldBe(CliExecutableNotFoundReason.NativeExecutableMissing);
        second.SkippedCommandScripts.ShouldBe(new[] { "/win/A/claude.cmd" });

        var packaged = PackagedClaudeBinary("/win/A");
        fs.AddFile(packaged);

        resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], environment).ResolvedPath.ShouldBe(packaged);
    }

    [Test]
    public void A_provider_hit_performs_only_the_existence_checks_of_its_accepted_paths()
    {
        var fs = new InMemoryCliFileSystem(caseInsensitive: true);
        fs.AddFile("/win/A/claude.exe");
        fs.AddFile("/win/B/claude.exe");
        var resolver = WindowsResolver(fs);
        var environment = Env(path: Paths("/win/A", "/win/B"));
        resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], environment);

        fs.ResetCounts();
        resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], environment);

        fs.FileExistsCalls.ShouldBe(2);
        fs.ListFilesCalls.ShouldBe(0);
        fs.DirectoryExistsCalls.ShouldBe(0);
    }

    [TestCaseSource(nameof(RegisteredProviderPlatforms))]
    public void Second_resolution_of_every_registered_provider_performs_only_existence_checks(string providerName, bool isWindows)
    {
        var descriptor = CliInstallRegistry.FindByProviderName(providerName)!;
        var executable = descriptor.ExecutableCandidates[0] + (isWindows ? ".exe" : string.Empty);
        var fs = new InMemoryCliFileSystem(caseInsensitive: isWindows);
        fs.AddFile("/win/A/" + executable);
        var resolver = isWindows ? WindowsResolver(fs) : PosixResolver(fs);
        var environment = Env(path: "/win/A");

        var first = resolver.ResolveProviderExecutable(providerName, descriptor.ExecutableCandidates, environment);
        fs.ResetCounts();
        var second = resolver.ResolveProviderExecutable(providerName, descriptor.ExecutableCandidates, environment);

        first.IsFound.ShouldBeTrue();
        second.ResolvedPath.ShouldBe(first.ResolvedPath);
        second.AcceptedPaths.ShouldBe(first.AcceptedPaths);
        fs.FileExistsCalls.ShouldBe(second.AcceptedPaths.Count);
        fs.ListFilesCalls.ShouldBe(0);
        fs.DirectoryExistsCalls.ShouldBe(0);
    }

    [Test]
    public void The_cache_never_serves_a_fake_windows_entry_to_a_real_platform_lookup_and_the_reverse()
    {
        var clock = new ManualTimeProvider(Start);
        var cache = new CliResolutionCache(clock);
        var windowsKey = new CliResolutionKey("paths", string.Empty, "tool", string.Empty, "/win/A", string.Empty, string.Empty, "/cwd", "/proc", IsWindows: true);
        var posixKey = windowsKey with { IsWindows = false };

        cache.StorePaths(windowsKey, ["/win/A/tool.exe"]);

        cache.TryGet(posixKey, out _).ShouldBeFalse();
        cache.TryGet(windowsKey, out _).ShouldBeTrue();
    }

    private static IEnumerable<TestCaseData> RegisteredProviderPlatforms()
    {
        foreach (var descriptor in CliInstallRegistry.Descriptors)
        {
            yield return new TestCaseData(descriptor.ProviderName, true).SetName($"{descriptor.ProviderName}_windows");
            yield return new TestCaseData(descriptor.ProviderName, false).SetName($"{descriptor.ProviderName}_posix");
        }
    }

    private static string PackagedClaudeBinary(string shimDirectory)
    {
        var layout = CliInstallRegistry.FindByProviderName("ClaudeCode")!.PackagedBinaryLayout!;
        var target = layout.Targets.First(target => target.Architecture == Architecture.X64);
        var segments = new[] { "node_modules", layout.MainPackage, "node_modules", target.PlatformPackage, target.FileInPackage };
        return segments.Aggregate(shimDirectory, (path, segment) => path + "/" + segment);
    }

    private static CliExecutableResolver WindowsResolver(InMemoryCliFileSystem fs, TimeProvider? clock = null) =>
        new(static () => true, static () => Architecture.X64, clock ?? new ManualTimeProvider(Start), fs);

    private static CliExecutableResolver PosixResolver(InMemoryCliFileSystem fs) =>
        new(static () => false, static () => Architecture.X64, new ManualTimeProvider(Start), fs);

    private static string Paths(params string[] paths) => string.Join(Path.PathSeparator, paths);

    private static Dictionary<string, string?> Env(string? path = null, string? pathExt = ".EXE;.CMD;.BAT", string? agentCliPath = "") =>
        new(StringComparer.Ordinal)
        {
            ["PATH"] = path ?? string.Empty,
            ["PATHEXT"] = pathExt,
            ["HAGICODE_AGENT_CLI_PATH"] = agentCliPath,
        };
}
