using System.Runtime.InteropServices;
using HagiCode.Libs.Core.Discovery;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Discovery;

public sealed class CliExecutableResolverWindowsPolicyTests
{
    private const string AgentCliPathEnvironmentVariable = "HAGICODE_AGENT_CLI_PATH";

    [Test]
    public void ResolveExecutablePath_on_windows_ignores_pathext_order_and_prefers_cmd_over_bat()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("bin", "tool.bat");
        var cmd = sandbox.CreateFile("bin", "tool.cmd");
        var resolver = new CliExecutableResolver(static () => true);

        var resolved = resolver.ResolveExecutablePath("tool", sandbox.Env(pathExt: ".COM;.EXE;.BAT;.CMD", path: sandbox.Dir("bin")));

        resolved.ShouldBe(cmd);
    }

    [Test]
    public void ResolveExecutablePath_on_windows_returns_a_later_path_exe_over_an_earlier_path_cmd()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("a", "tool.cmd");
        var exe = sandbox.CreateFile("b", "tool.exe");
        var resolver = new CliExecutableResolver(static () => true);

        var resolved = resolver.ResolveExecutablePath("tool", sandbox.Env(path: Join(sandbox.Dir("a"), sandbox.Dir("b"))));

        resolved.ShouldBe(exe);
    }

    [Test]
    public void ResolveExecutablePath_on_windows_returns_a_hagicode_agent_cli_path_cmd_over_a_path_exe()
    {
        using var sandbox = new PolicySandbox();
        var cmd = sandbox.CreateFile("custom", "tool.cmd");
        sandbox.CreateFile("path", "tool.exe");
        var resolver = new CliExecutableResolver(static () => true);

        var resolved = resolver.ResolveExecutablePath(
            "tool",
            sandbox.Env(customPath: sandbox.Dir("custom"), path: sandbox.Dir("path")));

        resolved.ShouldBe(cmd);
    }

    [Test]
    public void ResolveExecutablePath_on_windows_returns_a_cmd_when_no_exe_exists()
    {
        using var sandbox = new PolicySandbox();
        var cmd = sandbox.CreateFile("bin", "tool.cmd");
        var resolver = new CliExecutableResolver(static () => true);

        resolver.ResolveExecutablePath("tool", sandbox.Env(path: sandbox.Dir("bin"))).ShouldBe(cmd);
    }

    [Test]
    public void ResolveExecutablePaths_lists_matches_in_the_same_order_and_the_single_result_is_its_first_item()
    {
        using var sandbox = new PolicySandbox();
        var aCmd = sandbox.CreateFile("a", "tool.cmd");
        var aBat = sandbox.CreateFile("a", "tool.bat");
        var bExe = sandbox.CreateFile("b", "tool.exe");
        var bCmd = sandbox.CreateFile("b", "tool.cmd");
        var resolver = new CliExecutableResolver(static () => true);
        var environment = sandbox.Env(path: Join(sandbox.Dir("a"), sandbox.Dir("b")));

        var all = resolver.ResolveExecutablePaths("tool", environment);
        var single = resolver.ResolveExecutablePath("tool", environment);

        all.ShouldBe([bExe, aCmd, bCmd, aBat]);
        single.ShouldBe(all[0]);
    }

    [Test]
    public void ResolveExecutablePath_on_windows_keeps_a_name_with_an_extension_as_given()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("bin", "tool.exe");
        var cmd = sandbox.CreateFile("bin", "tool.cmd");
        var resolver = new CliExecutableResolver(static () => true);
        var currentDirectory = Directory.GetCurrentDirectory();

        Directory.SetCurrentDirectory(sandbox.Dir("bin"));
        try
        {
            resolver.ResolveExecutablePath("tool.cmd", sandbox.Env(path: sandbox.Dir("bin"))).ShouldBe(cmd);
        }
        finally
        {
            Directory.SetCurrentDirectory(currentDirectory);
        }
    }

    [Test]
    public void ResolveExecutablePath_on_unix_is_unchanged_by_the_windows_rules()
    {
        using var sandbox = new PolicySandbox();
        var tool = sandbox.CreateFile("bin", "tool");
        sandbox.CreateFile("bin", "tool.cmd");
        var resolver = new CliExecutableResolver(static () => false);

        resolver.ResolveExecutablePath("tool", sandbox.Env(path: sandbox.Dir("bin"))).ShouldBe(tool);
    }

    [Test]
    public void ResolveProviderExecutable_requires_native_for_copilot_and_ignores_a_shim_only_install()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("npm", "copilot.cmd");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("Copilot", ["copilot"], sandbox.Env(path: sandbox.Dir("npm")));

        lookup.IsFound.ShouldBeFalse();
        lookup.NotFoundReason.ShouldBe(CliExecutableNotFoundReason.NativeExecutableMissing);
        lookup.SkippedCommandScripts.ShouldBe([Path.Combine(sandbox.Dir("npm"), "copilot.cmd")]);
    }

    [Test]
    public void ResolveProviderExecutable_reports_not_installed_when_nothing_exists()
    {
        using var sandbox = new PolicySandbox();
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("Codex", ["codex"], sandbox.Env(path: sandbox.Dir("empty")));

        lookup.IsFound.ShouldBeFalse();
        lookup.NotFoundReason.ShouldBe(CliExecutableNotFoundReason.NotInstalled);
        lookup.SkippedCommandScripts.ShouldBeEmpty();
    }

    [Test]
    public void ResolveProviderExecutable_requires_native_for_claude_and_refuses_a_cmd_in_the_override_tier_when_no_exe_exists()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("custom", "claude.cmd");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], sandbox.Env(customPath: sandbox.Dir("custom")));

        lookup.IsFound.ShouldBeFalse();
        lookup.NotFoundReason.ShouldBe(CliExecutableNotFoundReason.NativeExecutableMissing);
    }

    [Test]
    public void ResolveProviderExecutable_native_exe_in_path_beats_a_cmd_in_an_earlier_directory_for_claude()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("earlier", "claude.cmd");
        var exe = sandbox.CreateFile("later", "claude.exe");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], sandbox.Env(path: Join(sandbox.Dir("earlier"), sandbox.Dir("later"))));

        lookup.ResolvedPath.ShouldBe(exe);
    }

    [Test]
    public void ResolveProviderExecutable_command_script_in_the_override_tier_does_not_shadow_a_path_exe_for_copilot()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("custom", "copilot.cmd");
        var exe = sandbox.CreateFile("path", "copilot.exe");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("Copilot", ["copilot"], sandbox.Env(customPath: sandbox.Dir("custom"), path: sandbox.Dir("path")));

        lookup.ResolvedPath.ShouldBe(exe);
    }

    [Test]
    public void ResolveProviderExecutable_copilot_packaged_binary_is_found_beside_a_nested_shim()
    {
        using var sandbox = new PolicySandbox();
        var prefix = sandbox.Dir("npm");
        sandbox.CreateFile("npm", "copilot.cmd");
        var exe = sandbox.CreateFile("npm", "node_modules", "@github", "copilot", "node_modules", "@github", "copilot-win32-x64", "copilot.exe");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("Copilot", ["copilot"], sandbox.Env(path: prefix));

        lookup.ResolvedPath.ShouldBe(exe);
        lookup.NotFoundReason.ShouldBeNull();
    }

    [Test]
    public void ResolveProviderExecutable_copilot_packaged_binary_is_found_beside_a_hoisted_shim()
    {
        using var sandbox = new PolicySandbox();
        var prefix = sandbox.Dir("npm");
        sandbox.CreateFile("npm", "copilot.cmd");
        var exe = sandbox.CreateFile("npm", "node_modules", "@github", "copilot-win32-x64", "copilot.exe");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("Copilot", ["copilot"], sandbox.Env(path: prefix));

        lookup.ResolvedPath.ShouldBe(exe);
    }

    [Test]
    public void ResolveProviderExecutable_codex_packaged_binary_uses_the_vendor_triple_under_the_shim()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("npm", "codex.cmd");
        var exe = sandbox.CreateFile(
            "npm", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("Codex", ["codex"], sandbox.Env(path: sandbox.Dir("npm")));

        lookup.ResolvedPath.ShouldBe(exe);
    }

    [Test]
    public void ResolveProviderExecutable_claude_packaged_binary_is_found_beside_a_hoisted_shim_and_top_level_file_is_last()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("npm", "claude.cmd");
        sandbox.CreateFile("npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        var hoisted = sandbox.CreateFile("npm", "node_modules", "@anthropic-ai", "claude-code-win32-x64", "claude.exe");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], sandbox.Env(path: sandbox.Dir("npm")));

        lookup.ResolvedPath.ShouldBe(hoisted);
    }

    [Test]
    public void ResolveProviderExecutable_claude_top_level_bin_is_used_only_when_no_platform_package_exists()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("npm", "claude.cmd");
        var topLevel = sandbox.CreateFile("npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], sandbox.Env(path: sandbox.Dir("npm")));

        lookup.ResolvedPath.ShouldBe(topLevel);
    }

    [Test]
    public void ResolveProviderExecutable_finds_the_packaged_binary_when_the_npm_prefix_is_not_on_path()
    {
        using var sandbox = new PolicySandbox();
        var appData = sandbox.Dir("appdata");
        var exe = sandbox.CreateFile("appdata", "npm", "node_modules", "@github", "copilot-win32-x64", "copilot.exe");
        sandbox.CreateFile("appdata", "npm", "copilot.cmd");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("Copilot", ["copilot"], sandbox.Env(path: sandbox.Dir("elsewhere"), appData: appData));

        lookup.ResolvedPath.ShouldBe(exe);
    }

    [Test]
    public void ResolveProviderExecutable_on_arm64_does_not_return_the_x64_packaged_binary()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("npm", "copilot.cmd");
        sandbox.CreateFile("npm", "node_modules", "@github", "copilot-win32-x64", "copilot.exe");
        var resolver = CreateWindowsResolver(Architecture.Arm64);

        var lookup = resolver.ResolveProviderExecutable("Copilot", ["copilot"], sandbox.Env(path: sandbox.Dir("npm")));

        lookup.IsFound.ShouldBeFalse();
        lookup.NotFoundReason.ShouldBe(CliExecutableNotFoundReason.NativeExecutableMissing);
    }

    [Test]
    public void ResolveProviderExecutable_on_arm64_finds_the_arm64_packaged_binary()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("npm", "copilot.cmd");
        sandbox.CreateFile("npm", "node_modules", "@github", "copilot-win32-x64", "copilot.exe");
        var arm64 = sandbox.CreateFile("npm", "node_modules", "@github", "copilot-win32-arm64", "copilot.exe");
        var resolver = CreateWindowsResolver(Architecture.Arm64);

        var lookup = resolver.ResolveProviderExecutable("Copilot", ["copilot"], sandbox.Env(path: sandbox.Dir("npm")));

        lookup.ResolvedPath.ShouldBe(arm64);
    }

    [Test]
    public void ResolveProviderExecutable_opencode_prefers_the_packaged_exe_over_its_shim()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("npm", "opencode.cmd");
        var exe = sandbox.CreateFile("npm", "node_modules", "opencode-windows-x64", "bin", "opencode.exe");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("OpenCode", ["opencode"], sandbox.Env(path: sandbox.Dir("npm")));

        lookup.ResolvedPath.ShouldBe(exe);
    }

    [Test]
    public void ResolveProviderExecutable_opencode_falls_back_to_its_shim_when_no_packaged_binary_exists()
    {
        using var sandbox = new PolicySandbox();
        var cmd = sandbox.CreateFile("npm", "opencode.cmd");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("OpenCode", ["opencode"], sandbox.Env(path: sandbox.Dir("npm")));

        lookup.ResolvedPath.ShouldBe(cmd);
    }

    [Test]
    public void ResolveProviderExecutable_reasonix_uses_its_packaged_binary()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("npm", "reasonix.cmd");
        var exe = sandbox.CreateFile("npm", "node_modules", "@reasonix", "cli-win32-x64", "bin", "reasonix.exe");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("Reasonix", ["reasonix"], sandbox.Env(path: sandbox.Dir("npm")));

        lookup.ResolvedPath.ShouldBe(exe);
    }

    [Test]
    public void ResolveProviderExecutable_script_only_agent_resolves_its_cmd_shim()
    {
        using var sandbox = new PolicySandbox();
        var cmd = sandbox.CreateFile("npm", "pi.cmd");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("Pi", ["pi"], sandbox.Env(path: sandbox.Dir("npm")));

        lookup.ResolvedPath.ShouldBe(cmd);
    }

    [Test]
    public void ResolveProviderExecutable_agent_without_a_layout_prefers_a_later_exe_over_an_earlier_cmd()
    {
        using var sandbox = new PolicySandbox();
        sandbox.CreateFile("earlier", "pi.cmd");
        var exe = sandbox.CreateFile("later", "pi.exe");
        var resolver = CreateWindowsResolver(Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("Pi", ["pi"], sandbox.Env(path: Join(sandbox.Dir("earlier"), sandbox.Dir("later"))));

        lookup.ResolvedPath.ShouldBe(exe);
    }

    [Test]
    public void ResolveProviderExecutable_resolves_an_explicitly_named_cmd_as_named_even_for_a_native_agent()
    {
        using var sandbox = new PolicySandbox();
        var cmd = sandbox.CreateFile("bin", "claude.cmd");
        var resolver = CreateWindowsResolver(Architecture.X64);
        var currentDirectory = Directory.GetCurrentDirectory();

        Directory.SetCurrentDirectory(sandbox.Dir("bin"));
        try
        {
            var lookup = resolver.ResolveProviderExecutable("ClaudeCode", ["claude.cmd"], sandbox.Env());

            lookup.ResolvedPath.ShouldBe(cmd);
        }
        finally
        {
            Directory.SetCurrentDirectory(currentDirectory);
        }
    }

    [Test]
    public void ResolveProviderExecutable_on_unix_uses_plain_resolution_for_a_native_agent()
    {
        using var sandbox = new PolicySandbox();
        var tool = sandbox.CreateFile("npm", "claude");
        var resolver = new CliExecutableResolver(static () => false, static () => Architecture.X64);

        var lookup = resolver.ResolveProviderExecutable("ClaudeCode", ["claude"], sandbox.Env(path: sandbox.Dir("npm")));

        lookup.ResolvedPath.ShouldBe(tool);
    }

    private static CliExecutableResolver CreateWindowsResolver(Architecture architecture)
    {
        return new CliExecutableResolver(static () => true, () => architecture);
    }

    private static string Join(params string[] directories) => string.Join(Path.PathSeparator, directories);

    private sealed class PolicySandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"hagicode-libs-policy-{Guid.NewGuid():N}");

        public PolicySandbox()
        {
            Directory.CreateDirectory(_root);
        }

        public string Dir(params string[] segments)
        {
            var path = Path.Combine([_root, .. segments]);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(params string[] segments)
        {
            var path = Path.Combine([_root, .. segments]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return Path.GetFullPath(path);
        }

        public IReadOnlyDictionary<string, string?> Env(
            string? customPath = null,
            string? path = null,
            string? pathExt = null,
            string? appData = null)
        {
            return new Dictionary<string, string?>
            {
                [AgentCliPathEnvironmentVariable] = customPath,
                ["PATH"] = path ?? _root,
                ["PATHEXT"] = pathExt,
                ["APPDATA"] = appData,
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
