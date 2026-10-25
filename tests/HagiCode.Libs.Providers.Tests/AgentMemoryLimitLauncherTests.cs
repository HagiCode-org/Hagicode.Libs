using System.Diagnostics;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Codex.AppServer;
using HagiCode.Libs.Providers.OpenCode;
using ManagedCode.CodexSharpSDK.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// The launchers that build their own start information apply the same memory-limit wrapping as <see cref="CliProcessManager" />.
/// </summary>
public sealed class AgentMemoryLimitLauncherTests
{
    private const string SystemdRunPath = "/usr/bin/systemd-run";

    [Test]
    public void Codex_exec_start_info_is_wrapped_with_the_same_arguments_as_the_process_manager()
    {
        var provider = new FixedLimitProvider(4096);
        var probe = SupportedProbe();
        var invocation = new CodexProcessInvocation(
            "/opt/agents/codex",
            ["exec", "--json", "--config", "model_reasoning_effort=\"high\""],
            new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
            "prompt");

        var codexStartInfo = DefaultCodexProcessRunner.CreateStartInfo(invocation, isWindows: false, provider, probe);
        var managerPlan = CreateManager(provider, probe).CreateStartPlan(new ProcessStartContext
        {
            ExecutablePath = invocation.ExecutablePath,
            Arguments = invocation.Arguments
        });

        codexStartInfo.FileName.ShouldBe(SystemdRunPath);
        codexStartInfo.FileName.ShouldBe(managerPlan.StartInfo.FileName);
        codexStartInfo.ArgumentList.ShouldBe(managerPlan.StartInfo.ArgumentList);
        codexStartInfo.Environment["NODE_OPTIONS"].ShouldBe(managerPlan.StartInfo.Environment["NODE_OPTIONS"]);
        codexStartInfo.Environment["NODE_OPTIONS"].ShouldBe("--max-old-space-size=3072");
        codexStartInfo.Environment["PATH"].ShouldBe("/usr/bin");
        codexStartInfo.RedirectStandardInput.ShouldBeTrue();
        codexStartInfo.RedirectStandardOutput.ShouldBeTrue();
    }

    [Test]
    public void Codex_exec_start_info_stays_unwrapped_without_a_provider()
    {
        var invocation = new CodexProcessInvocation("/opt/agents/codex", ["exec", "--json"], new Dictionary<string, string>(), "prompt");

        var startInfo = DefaultCodexProcessRunner.CreateStartInfo(invocation, isWindows: false, resourceLimitProvider: null, SupportedProbe());

        startInfo.FileName.ShouldBe("/opt/agents/codex");
        startInfo.ArgumentList.ShouldBe(["exec", "--json"]);
        startInfo.Environment.ContainsKey("NODE_OPTIONS").ShouldBeFalse();
    }

    [Test]
    public void Codex_app_server_start_info_is_wrapped_and_keeps_its_stdio_redirection_and_arguments()
    {
        var workingDirectory = Path.GetTempPath();

        var startInfo = CodexAppServerProcess.CreateStartInfo(
            "/opt/agents/codex",
            new Dictionary<string, string> { ["CODEX_HOME"] = "/home/user/.codex" },
            workingDirectory,
            isWindows: false,
            new CliResourceLimits(4096),
            SupportedProbe());

        startInfo.FileName.ShouldBe(SystemdRunPath);
        startInfo.ArgumentList.ShouldBe([.. new CliResourceLimits(4096).SystemdRunArguments, "/opt/agents/codex", "app-server"]);
        startInfo.RedirectStandardInput.ShouldBeTrue();
        startInfo.RedirectStandardOutput.ShouldBeTrue();
        startInfo.RedirectStandardError.ShouldBeTrue();
        startInfo.WorkingDirectory.ShouldBe(workingDirectory);
        startInfo.Environment["CODEX_HOME"].ShouldBe("/home/user/.codex");
        startInfo.Environment["NODE_OPTIONS"].ShouldBe("--max-old-space-size=3072");
    }

    [Test]
    public void Codex_app_server_start_info_stays_unwrapped_without_a_limit()
    {
        var startInfo = CodexAppServerProcess.CreateStartInfo(
            "/opt/agents/codex",
            new Dictionary<string, string>(),
            workingDirectory: null,
            isWindows: false,
            limits: null,
            SupportedProbe());

        startInfo.FileName.ShouldBe("/opt/agents/codex");
        startInfo.ArgumentList.ShouldBe(["app-server"]);
    }

    [Test]
    public void OpenCode_start_info_is_wrapped_and_keeps_its_working_directory_and_environment()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "opencode workspace");
        var launcher = new OpenCodeProcessLauncher(new CliExecutableResolver(), static () => false, new FixedLimitProvider(4096), SupportedProbe());

        var startInfo = launcher.CreateStartInfo(
            "/opt/agents/opencode",
            new OpenCodeStandaloneServerOptions
            {
                WorkingDirectory = workingDirectory,
                EnvironmentVariables = new Dictionary<string, string?> { ["OPENCODE_CONFIG"] = "/etc/opencode.json", ["NODE_OPTIONS"] = "--enable-source-maps" }
            },
            new Dictionary<string, string?> { ["RUNTIME_VALUE"] = "from-runtime" },
            port: 4096);

        startInfo.FileName.ShouldBe(SystemdRunPath);
        startInfo.ArgumentList.ShouldBe([.. new CliResourceLimits(4096).SystemdRunArguments, "/opt/agents/opencode", "serve", "--hostname=127.0.0.1", "--port=4096"]);
        startInfo.WorkingDirectory.ShouldBe(workingDirectory);
        startInfo.Environment["OPENCODE_CONFIG"].ShouldBe("/etc/opencode.json");
        startInfo.Environment["RUNTIME_VALUE"].ShouldBe("from-runtime");
        startInfo.Environment["NODE_OPTIONS"].ShouldBe("--enable-source-maps --max-old-space-size=3072");
        startInfo.RedirectStandardOutput.ShouldBeTrue();
        startInfo.RedirectStandardError.ShouldBeTrue();
    }

    [Test]
    public void OpenCode_start_info_stays_unwrapped_without_a_provider()
    {
        var launcher = new OpenCodeProcessLauncher(new CliExecutableResolver(), static () => false, resourceLimitProvider: null, SupportedProbe());

        var startInfo = launcher.CreateStartInfo("/opt/agents/opencode", new OpenCodeStandaloneServerOptions(), new Dictionary<string, string?>(), port: 4096);

        startInfo.FileName.ShouldBe("/opt/agents/opencode");
        startInfo.ArgumentList.ShouldBe(["serve", "--hostname=127.0.0.1", "--port=4096"]);
    }

    [Test]
    public async Task AddHagiCodeLibs_registers_the_AGENT_MEM_MAX_provider_by_default()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeLibs();

        await using var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetRequiredService<ICliResourceLimitProvider>().ShouldBeOfType<EnvironmentCliResourceLimitProvider>();
        serviceProvider.GetRequiredService<CgroupScopeCapabilityProbe>().ShouldBeSameAs(CgroupScopeCapabilityProbe.Default);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task A_host_registered_provider_wins_over_the_environment_one_for_every_launch_path(bool registerAfterLibs)
    {
        var hostProvider = new FixedLimitProvider(megabytes: null);
        var services = new ServiceCollection();
        if (registerAfterLibs)
        {
            services.AddHagiCodeLibs();
            services.Replace(ServiceDescriptor.Singleton<ICliResourceLimitProvider>(hostProvider));
        }
        else
        {
            services.AddSingleton<ICliResourceLimitProvider>(hostProvider);
            services.AddHagiCodeLibs();
        }

        await using var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetRequiredService<ICliResourceLimitProvider>().ShouldBeSameAs(hostProvider);
        serviceProvider.GetRequiredService<CodexProvider>().ResourceLimitProvider.ShouldBeSameAs(hostProvider);
        serviceProvider.GetRequiredService<OpenCodeStandaloneServerHost>().ProcessLauncher.ResourceLimitProvider.ShouldBeSameAs(hostProvider);

        serviceProvider.GetRequiredService<CliProcessManager>().CreateStartInfo(new ProcessStartContext { ExecutablePath = "/bin/sh" });
        hostProvider.Calls.ShouldBe(1);
    }

    private static CgroupScopeCapabilityProbe SupportedProbe() => new(static () => true, static () => SystemdRunPath, static (_, _, _) => true);

    private static CliProcessManager CreateManager(ICliResourceLimitProvider provider, CgroupScopeCapabilityProbe probe) =>
        new(
            new CliExecutableResolver(),
            new CliOwnedProcessRegistry(),
            new CliProcessOwnershipOptions(),
            TimeProvider.System,
            NullLogger<CliProcessManager>.Instance,
            provider,
            probe);

    private sealed class FixedLimitProvider(int? megabytes) : ICliResourceLimitProvider
    {
        public int Calls { get; private set; }

        public CliResourceLimits? GetCurrent()
        {
            Calls++;
            return megabytes is { } value ? new CliResourceLimits(value) : null;
        }
    }
}
