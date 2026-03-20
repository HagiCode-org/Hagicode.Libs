# HagiCode.Libs

`HagiCode.Libs` is a lightweight .NET 10 library workspace for reusable HagiCode CLI integrations and repository exploration.

## Projects

- `src/HagiCode.Libs.Core` - transport, process management, executable discovery, and runtime environment resolution.
- `src/HagiCode.Libs.Providers` - provider abstractions, the Claude Code/Codex/CodeBuddy/IFlow providers, and optional DI registration.
- `src/HagiCode.Libs.Exploration` - Git repository discovery and state inspection.
- `tests/*` - xUnit coverage for each project.

## Getting started

```bash
cd repos/Hagicode.Libs
dotnet build HagiCode.Libs.sln
dotnet test HagiCode.Libs.sln
```

## Dedicated provider console

`src/HagiCode.Libs.ClaudeCode.Console`, `src/HagiCode.Libs.Codex.Console`, `src/HagiCode.Libs.Codebuddy.Console`, and `src/HagiCode.Libs.IFlow.Console` are dedicated provider consoles built on the shared `HagiCode.Libs.ConsoleTesting` harness.

From `repos/Hagicode.Libs`, you can use:

```bash
dotnet run --project src/HagiCode.Libs.ClaudeCode.Console -- --help
dotnet run --project src/HagiCode.Libs.ClaudeCode.Console
dotnet run --project src/HagiCode.Libs.ClaudeCode.Console -- --test-provider
dotnet run --project src/HagiCode.Libs.ClaudeCode.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.ClaudeCode.Console -- --test-all claude

dotnet run --project src/HagiCode.Libs.Codex.Console -- --help
dotnet run --project src/HagiCode.Libs.Codex.Console
dotnet run --project src/HagiCode.Libs.Codex.Console -- --test-provider codex-cli
dotnet run --project src/HagiCode.Libs.Codex.Console -- --test-provider-full --sandbox workspace-write --repo .
dotnet run --project src/HagiCode.Libs.Codex.Console -- --test-all codex

dotnet run --project src/HagiCode.Libs.Codebuddy.Console -- --help
dotnet run --project src/HagiCode.Libs.Codebuddy.Console
dotnet run --project src/HagiCode.Libs.Codebuddy.Console -- --test-provider codebuddy-cli
dotnet run --project src/HagiCode.Libs.Codebuddy.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.Codebuddy.Console -- --test-all codebuddy

dotnet run --project src/HagiCode.Libs.IFlow.Console -- --help
dotnet run --project src/HagiCode.Libs.IFlow.Console
dotnet run --project src/HagiCode.Libs.IFlow.Console -- --test-provider iflow-cli
dotnet run --project src/HagiCode.Libs.IFlow.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.IFlow.Console -- --test-provider-full --endpoint ws://127.0.0.1:7331/acp
dotnet run --project src/HagiCode.Libs.IFlow.Console -- --test-all iflow
```

- No arguments run the default Claude suite.
- 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Restore`。
- `--test-provider` runs the provider ping flow for the Claude console only.
- `--test-provider-full` and `--test-all` run the full provider-scoped suite.
- `--repo <path>` adds the repository analysis scenario to the suite.
- `--api-key <key>` and `--model <model>` override Claude execution options for scenario runs.
- No arguments also run the default Codex suite.
- Codex 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- Codex accepts `--model <model>`, `--sandbox <mode>`, `--approval-policy <mode>`, `--api-key <key>`, and `--base-url <url>` overrides.
- Codex repository analysis remains opt-in via `--repo <path>` and reuses the same shared report formatter.
- No arguments also run the default CodeBuddy suite.
- CodeBuddy 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- CodeBuddy accepts `--model <model>` and defaults to `glm-4.7` when no explicit model override is supplied.
- CodeBuddy repository summary remains opt-in via `--repo <path>`.
- No arguments also run the default IFlow suite.
- IFlow 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- IFlow accepts `--model <model>`, `--endpoint <ws-url>`, and `--executable <path>` overrides.
- IFlow repository summary remains opt-in via `--repo <path>`.

## Provider usage

The DI registration path now exposes all built-in providers:

```csharp
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;
using HagiCode.Libs.Providers.Codebuddy;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.IFlow;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHagiCodeLibs();

await using var provider = services.BuildServiceProvider();
var claude = provider.GetRequiredService<ICliProvider<ClaudeCodeOptions>>();
var codebuddy = provider.GetRequiredService<ICliProvider<CodebuddyOptions>>();
var codex = provider.GetRequiredService<ICliProvider<CodexOptions>>();
var iflow = provider.GetRequiredService<ICliProvider<IFlowOptions>>();
```

CodeBuddy execution options cover the ACP-specific runtime settings without forcing raw command lines:

```csharp
var options = new CodebuddyOptions
{
    Model = "glm-4.7",
    WorkingDirectory = "/path/to/repo",
    EnvironmentVariables = new Dictionary<string, string?>
    {
        ["CODEBUDDY_TOKEN"] = "<token>"
    }
};

await foreach (var message in codebuddy.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}
```

Codex execution options cover the common CLI settings without forcing raw command lines:

```csharp
var options = new CodexOptions
{
    Model = "gpt-5-codex",
    SandboxMode = "workspace-write",
    ApprovalPolicy = "never",
    WorkingDirectory = "/path/to/repo",
    AddDirectories = ["/path/to/repo"],
    SkipGitRepositoryCheck = true,
};

await foreach (var message in codex.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine(message.Type);
}
```

IFlow execution options cover both managed bootstrap and explicit ACP endpoint reuse:

```csharp
var options = new IFlowOptions
{
    Model = "iflow/default",
    WorkingDirectory = "/path/to/repo",
    Endpoint = new Uri("ws://127.0.0.1:7331/acp")
};

await foreach (var message in iflow.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}
```

If you want the provider to launch `iflow` for you, omit `Endpoint` and optionally set `ExecutablePath`. The managed path starts `iflow --experimental-acp --port <allocated-port>` and keeps the subprocess alive for the duration of the provider call.

## Cross-platform CLI discovery validation

`repos/Hagicode.Libs/.github/workflows/cli-discovery-cross-platform.yml` runs the real Claude Code discovery path on `ubuntu-latest`, `macos-latest`, and `windows-latest`. The workflow installs the npm-distributed Claude Code CLI, verifies the `claude` executable is on `PATH`, and then runs the opt-in `Category=RealCli` test slice so hosted runners exercise the same `CliExecutableResolver` and provider ping path used in production.

The workflow pins `@anthropic-ai/claude-code@2.1.79` to reduce runner drift. That version is the current package version selected for this change on 2026-03-19; if runner images or upstream CLI behavior change, update the workflow pin and rerun the matrix before widening coverage.

To reproduce the same real-CLI validation locally from the `repos/Hagicode.Libs` directory:

```bash
npm install --global @anthropic-ai/claude-code@2.1.79
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "Category=RealCli"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "Category=RealCli"
```

The real-CLI path only checks executable discovery and the auth-free `claude --version` ping behavior through the provider and dedicated console flows. It does not attempt interactive login or prompt execution, so the default test suite remains usable on machines without the external CLI installed.

Codex follows the same opt-in pattern. If `codex` is installed and available on `PATH`, you can run:

```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "Category=RealCli"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Codex"
```

These Codex checks intentionally stay at the auth-free `codex --version` / `--test-provider` layer. Prompt execution remains covered by fake-provider integration tests so the default CI path stays deterministic.

CodeBuddy follows the same opt-in pattern. If `codebuddy` is installed and available on `PATH`, you can run:

```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~Codebuddy"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Codebuddy"
```

These CodeBuddy checks validate the ACP bootstrap ping path (`codebuddy --acp` initialize) and the dedicated `--test-provider` console flow without requiring the default deterministic suite to talk to a real external CLI.

IFlow follows the same opt-in pattern. If `iflow` is installed and available on `PATH`, you can run:

```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~IFlow"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~IFlow"
```

These IFlow checks validate the managed ACP bootstrap ping path and the dedicated `--test-provider` console flow. The default deterministic suite continues to use fake bootstrap/session implementations, while live endpoint reuse can be validated manually with `--endpoint ws://host:port/acp`.

## Design goals

- Zero heavy framework dependencies in the reusable libraries.
- Cross-platform CLI process management for Windows, macOS, and Linux.
- Stream-oriented provider integrations that can power higher-level HagiCode tools.
- Simple provider registration for DI and non-DI consumers.
