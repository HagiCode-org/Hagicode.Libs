using System.Runtime.InteropServices;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.ClaudeCode;
using HagiCode.Libs.Providers.Copilot;
using HagiCode.Libs.Providers.Gemini;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Availability reads the process PATH, so these tests set and restore it and must not run alongside other PATH writers.
[NonParallelizable]
public sealed class ProviderWindowsNativePolicyTests
{
    [Test]
    public void ClaudeCode_is_not_available_on_windows_when_only_its_command_script_shim_exists()
    {
        using var sandbox = new ProviderSandbox();
        sandbox.CreateFile("npm", "claude.cmd");

        var available = sandbox.WithPath(sandbox.Dir("npm"), () =>
            new ClaudeCodeProvider(WindowsResolver(), new CliProcessManager()).IsAvailable);

        available.ShouldBeFalse();
    }

    [Test]
    public void ClaudeCode_is_available_on_windows_when_the_packaged_binary_sits_behind_the_shim()
    {
        using var sandbox = new ProviderSandbox();
        sandbox.CreateFile("npm", "claude.cmd");
        sandbox.CreateFile("npm", "node_modules", "@anthropic-ai", "claude-code-win32-x64", "claude.exe");

        var available = sandbox.WithPath(sandbox.Dir("npm"), () =>
            new ClaudeCodeProvider(WindowsResolver(), new CliProcessManager()).IsAvailable);

        available.ShouldBeTrue();
    }

    [Test]
    public void Gemini_is_available_on_windows_for_a_script_only_command_script()
    {
        using var sandbox = new ProviderSandbox();
        sandbox.CreateFile("npm", "gemini.cmd");

        var available = sandbox.WithPath(sandbox.Dir("npm"), () =>
            new GeminiProvider(WindowsResolver(), new CliProcessManager()).IsAvailable);

        available.ShouldBeTrue();
    }

    [Test]
    public void OpenCode_is_available_on_windows_through_the_packaged_binary_behind_its_shim()
    {
        using var sandbox = new ProviderSandbox();
        sandbox.CreateFile("npm", "opencode.cmd");
        sandbox.CreateFile("npm", "node_modules", "opencode-windows-x64", "bin", "opencode.exe");

        var available = sandbox.WithPath(sandbox.Dir("npm"), () =>
            new OpenCodeProvider(WindowsResolver()).IsAvailable);

        available.ShouldBeTrue();
    }

    [Test]
    public void Copilot_discovery_prefers_the_packaged_native_binary_over_its_shim()
    {
        using var sandbox = new ProviderSandbox();
        sandbox.CreateFile("npm", "copilot.cmd");
        var exe = sandbox.CreateFile("npm", "node_modules", "@github", "copilot-win32-x64", "copilot.exe");
        var provider = new CopilotProvider(WindowsResolver(), new CliProcessManager());

        var resolved = provider.ResolveExecutablePath(
            new CopilotOptions(),
            new Dictionary<string, string?> { ["PATH"] = sandbox.Dir("npm"), ["APPDATA"] = sandbox.Dir("appdata") });

        resolved.ShouldBe(exe);
    }

    [Test]
    public void Copilot_discovery_is_empty_on_windows_when_only_the_shim_exists()
    {
        using var sandbox = new ProviderSandbox();
        sandbox.CreateFile("npm", "copilot.cmd");
        var provider = new CopilotProvider(WindowsResolver(), new CliProcessManager());

        var resolved = provider.ResolveExecutablePath(
            new CopilotOptions(),
            new Dictionary<string, string?> { ["PATH"] = sandbox.Dir("npm"), ["APPDATA"] = sandbox.Dir("appdata") });

        resolved.ShouldBeNull();
    }

    [Test]
    public void Copilot_honors_an_explicit_command_script_path_as_given()
    {
        using var sandbox = new ProviderSandbox();
        var configured = sandbox.CreateFile("tools", "copilot.cmd");
        var provider = new CopilotProvider(WindowsResolver(), new CliProcessManager());

        var resolved = provider.ResolveExecutablePath(
            new CopilotOptions { ExecutablePath = configured },
            new Dictionary<string, string?> { ["PATH"] = sandbox.Dir("empty") });

        resolved.ShouldBe(configured);
    }

    private static CliExecutableResolver WindowsResolver() => new(static () => true, static () => Architecture.X64);

    private sealed class ProviderSandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"provider-policy-{Guid.NewGuid():N}");

        public ProviderSandbox()
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

        public T WithPath<T>(string path, Func<T> action)
        {
            var original = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", path);
            try
            {
                return action();
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", original);
            }
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
