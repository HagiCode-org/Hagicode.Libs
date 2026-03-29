# HagiCode.Libs

`HagiCode.Libs` is a lightweight .NET 10 library workspace for reusable HagiCode CLI integrations and repository exploration.

## Projects

- `src/HagiCode.Libs.Core` - transport, process management, executable discovery, and runtime environment resolution.
- `src/HagiCode.Libs.Providers` - provider abstractions, the Claude Code/Copilot/Codex/CodeBuddy/Gemini/Hermes/Kimi/Kiro/OpenCode/QoderCLI providers, and optional DI registration.
- `src/HagiCode.Libs.Skills` - skills-oriented infrastructure. Its first shipped capability is a typed online API client for search, well-known discovery, audit, telemetry, and GitHub metadata/tree requests.
- `src/HagiCode.Libs.Exploration` - Git repository discovery and state inspection.
- `tests/*` - xUnit coverage for each project.

## Getting started

```bash
cd repos/Hagicode.Libs
dotnet build HagiCode.Libs.slnx
dotnet test HagiCode.Libs.slnx
```

## NuGet publishing

`repos/Hagicode.Libs/.github/workflows/nuget-publish.yml` now supports two publish modes for `src/HagiCode.Libs.Core`, `src/HagiCode.Libs.Providers`, and `src/HagiCode.Libs.Skills`:

- Push a `v*.*.*` tag to publish a stable package version that matches the tag name without the leading `v` and publish the matching GitHub Release.
- Push to `main` to publish a dev prerelease package automatically without creating a GitHub Release.

The `main` prerelease version format is:

```text
<next-patch>-dev.<github.run_number>.<github.run_attempt>
```

- If the latest stable tag is `v1.2.3`, the next `main` publish becomes `1.2.4-dev.<run_number>.<run_attempt>`.
- If the repository has no stable `v*.*.*` tags yet, the workflow falls back to `0.1.0-dev.<run_number>.<run_attempt>`.
- Rerunning the same workflow increments `github.run_attempt`, so each rerun still produces a unique package version.

Because the `-dev.*` suffix marks these builds as NuGet prereleases, consumers must explicitly allow prerelease packages to install or upgrade to them. Stable consumers that do not opt into prerelease packages continue to resolve only stable versions.

Before using that workflow, configure GitHub and nuget.org for Trusted Publishing:

- Set the GitHub Actions secret `NUGET_USER` to the nuget.org account name that owns the packages.
- In nuget.org Trusted Publishing, add a policy for the `newbe36524/Hagicode.Libs` repository and set the workflow file name to `nuget-publish.yml`.
- Do not keep a long-lived `NUGET_API_KEY` secret for this workflow; both stable and dev publishes expect `NuGet/login@v1` to mint a temporary key through GitHub OIDC.

## Skills package usage

`HagiCode.Libs.Skills` is intentionally broader than the initial HTTP wrapper so future non-HTTP skills capabilities can ship in the same package without a rename. For now, the `OnlineApi` module is the delivered surface.

Register the package through DI:

```csharp
using HagiCode.Libs.Skills;
using HagiCode.Libs.Skills.OnlineApi;
using HagiCode.Libs.Skills.OnlineApi.Models;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHagiCodeSkills(options =>
{
    options.DisableTelemetry = true;
    options.GitHubToken = "<token>";
});

await using var provider = services.BuildServiceProvider();
var onlineApiClient = provider.GetRequiredService<IOnlineApiClient>();
```

Search and well-known discovery use typed request/response models:

```csharp
var searchResponse = await onlineApiClient.SearchAsync(new SearchSkillsRequest
{
    Query = "codex",
    Limit = 10,
});

var discoveryResponse = await onlineApiClient.DiscoverWellKnownAsync(new WellKnownDiscoveryRequest
{
    SourceUrl = "https://example.com/docs",
});
```

The endpoint profile is provider-driven, so consumers can replace `IOnlineApiEndpointProvider` or override base URIs without changing the public client contract.

## Dedicated provider console

`src/HagiCode.Libs.ClaudeCode.Console`, `src/HagiCode.Libs.Copilot.Console`, `src/HagiCode.Libs.Codex.Console`, `src/HagiCode.Libs.Codebuddy.Console`, `src/HagiCode.Libs.Gemini.Console`, `src/HagiCode.Libs.Hermes.Console`, `src/HagiCode.Libs.Kimi.Console`, `src/HagiCode.Libs.Kiro.Console`, `src/HagiCode.Libs.OpenCode.Console`, and `src/HagiCode.Libs.QoderCli.Console` are dedicated provider consoles built on the shared `HagiCode.Libs.ConsoleTesting` harness.

`src/HagiCode.Libs.Providers/OpenCode` now owns the canonical OpenCode typed runtime/session surface and the reusable `OpenCodeFixtureServer` test fixture. `hagicode-core` consumes that boundary through adapter-level tests and no longer ships a second OpenCode console or runtime project.

From `repos/Hagicode.Libs`, you can use:

```bash
dotnet run --project src/HagiCode.Libs.ClaudeCode.Console -- --help
dotnet run --project src/HagiCode.Libs.ClaudeCode.Console
dotnet run --project src/HagiCode.Libs.ClaudeCode.Console -- --test-provider
dotnet run --project src/HagiCode.Libs.ClaudeCode.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.ClaudeCode.Console -- --test-all claude

dotnet run --project src/HagiCode.Libs.Copilot.Console -- --help
dotnet run --project src/HagiCode.Libs.Copilot.Console
dotnet run --project src/HagiCode.Libs.Copilot.Console -- --test-provider github-copilot
dotnet run --project src/HagiCode.Libs.Copilot.Console -- --test-provider-full --model claude-sonnet-4.5 --config-dir .copilot --repo .
dotnet run --project src/HagiCode.Libs.Copilot.Console -- --test-all copilot

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

dotnet run --project src/HagiCode.Libs.Hermes.Console -- --help
dotnet run --project src/HagiCode.Libs.Hermes.Console
dotnet run --project src/HagiCode.Libs.Hermes.Console -- --test-provider hermes-cli
dotnet run --project src/HagiCode.Libs.Hermes.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.Hermes.Console -- --test-provider-full --arguments "acp --profile smoke"
dotnet run --project src/HagiCode.Libs.Hermes.Console -- --test-all hermes

dotnet run --project src/HagiCode.Libs.Gemini.Console -- --help
dotnet run --project src/HagiCode.Libs.Gemini.Console
dotnet run --project src/HagiCode.Libs.Gemini.Console -- --test-provider gemini-cli
dotnet run --project src/HagiCode.Libs.Gemini.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.Gemini.Console -- --test-provider-full --model gemini-2.5-pro --arg --profile=smoke
dotnet run --project src/HagiCode.Libs.Gemini.Console -- --test-all gemini

dotnet run --project src/HagiCode.Libs.Kimi.Console -- --help
dotnet run --project src/HagiCode.Libs.Kimi.Console
dotnet run --project src/HagiCode.Libs.Kimi.Console -- --test-provider kimi-cli
dotnet run --project src/HagiCode.Libs.Kimi.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.Kimi.Console -- --test-provider-full --model kimi-k2.5 --arg --profile=smoke
dotnet run --project src/HagiCode.Libs.Kimi.Console -- --test-all kimi

dotnet run --project src/HagiCode.Libs.Kiro.Console -- --help
dotnet run --project src/HagiCode.Libs.Kiro.Console
dotnet run --project src/HagiCode.Libs.Kiro.Console -- --test-provider kiro-cli
dotnet run --project src/HagiCode.Libs.Kiro.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.Kiro.Console -- --test-provider-full --model kiro-default --auth-method token --auth-token <token> --arg --profile
dotnet run --project src/HagiCode.Libs.Kiro.Console -- --test-all kiro

dotnet run --project src/HagiCode.Libs.OpenCode.Console -- --help
dotnet run --project src/HagiCode.Libs.OpenCode.Console
dotnet run --project src/HagiCode.Libs.OpenCode.Console -- --test-provider open-code
dotnet run --project src/HagiCode.Libs.OpenCode.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.OpenCode.Console -- --test-provider-full --model anthropic/claude-sonnet-4 --base-url http://127.0.0.1:4096
dotnet run --project src/HagiCode.Libs.OpenCode.Console -- --test-all opencode

dotnet run --project src/HagiCode.Libs.QoderCli.Console -- --help
dotnet run --project src/HagiCode.Libs.QoderCli.Console
dotnet run --project src/HagiCode.Libs.QoderCli.Console -- --test-provider qodercli
dotnet run --project src/HagiCode.Libs.QoderCli.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.QoderCli.Console -- --test-provider-full --model qoder-max
dotnet run --project src/HagiCode.Libs.QoderCli.Console -- --test-all qodercli
```

- No arguments run the default Claude suite.
- 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Restore`。
- `--test-provider` runs the provider ping flow for the Claude console only.
- `--test-provider-full` and `--test-all` run the full provider-scoped suite.
- `--repo <path>` adds the repository analysis scenario to the suite.
- `--api-key <key>` and `--model <model>` override Claude execution options for scenario runs.
- No arguments also run the default Copilot suite.
- Copilot 默认套件当前包含 `Ping`、`Simple Prompt` 和 `Complex Prompt`。
- Copilot accepts `--model <model>`, `--executable <path>`, `--auth-source <mode>`, `--github-token <token>`, `--config-dir <path>`, and compatible filtered startup overrides such as `--log-level <level>`.
- Copilot repository analysis remains opt-in via `--repo <path>`, and the provider filters unsupported startup flags before SDK launch with deterministic diagnostics.
- No arguments also run the default Codex suite.
- Codex 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- Codex accepts `--model <model>`, `--sandbox <mode>`, `--approval-policy <mode>`, `--api-key <key>`, and `--base-url <url>` overrides.
- Codex repository analysis remains opt-in via `--repo <path>` and reuses the same shared report formatter.
- No arguments also run the default CodeBuddy suite.
- CodeBuddy 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- CodeBuddy accepts `--model <model>` and defaults to `glm-4.7` when no explicit model override is supplied.
- CodeBuddy repository summary remains opt-in via `--repo <path>`.
- No arguments also run the default Hermes suite.
- Hermes 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Memory Reuse`。
- Hermes accepts `--model <model>`, `--executable <path>`, and `--arguments <value>` overrides.
- Hermes repository summary remains opt-in via `--repo <path>`.
- No arguments also run the default Gemini suite.
- Gemini 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- Gemini accepts `--model <model>`, `--executable <path>`, repeated `--arg <value>` overrides, and optional `--auth-method <id>` / `--auth-token <token>` bootstrap hints.
- Gemini repository summary remains opt-in via `--repo <path>`, and `gemini-cli` remains a dedicated-console alias for the canonical `gemini` provider name.
- No arguments also run the default Kimi suite.
- Kimi 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- Kimi accepts `--model <model>`, `--executable <path>`, repeated `--arg <value>` overrides, and optional `--auth-method <id>` / `--auth-token <token>` bootstrap hints.
- Kimi repository summary remains opt-in via `--repo <path>`, and `kimi-cli` remains a dedicated-console alias for the canonical `kimi` provider name.
- No arguments also run the default Kiro suite.
- Kiro 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- Kiro accepts `--model <model>`, `--executable <path>`, repeated `--arg <value>` overrides, and optional `--auth-method <id>` / `--auth-token <token>` / `--bootstrap-method <name>` bootstrap hints.
- Kiro repository summary remains opt-in via `--repo <path>`, and `kiro-cli` remains a dedicated-console alias for the canonical `kiro` provider name.
- No arguments also run the default OpenCode suite.
- OpenCode 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- OpenCode accepts `--model <model>`, `--executable <path>`, `--base-url <url>`, `--workspace <id>`, and repeated `--arg <value>`.
- OpenCode repository summary remains opt-in via `--repo <path>`, and `open-code` / `opencode-cli` both normalize to the canonical `opencode` provider name.
- No arguments also run the default QoderCLI suite.
- QoderCLI 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- QoderCLI accepts `--model <model>` for explicit model forwarding only; no default model is imposed because supported qodercli model identifiers have not been confirmed yet.
- QoderCLI repository summary remains opt-in via `--repo <path>`, and the provider now forces ACP sessions into `yolo` mode for unattended runs.

## Provider usage

The DI registration path now exposes all built-in providers:

```csharp
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;
using HagiCode.Libs.Providers.Codebuddy;
using HagiCode.Libs.Providers.Copilot;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Gemini;
using HagiCode.Libs.Providers.Hermes;
using HagiCode.Libs.Providers.Kimi;
using HagiCode.Libs.Providers.Kiro;
using HagiCode.Libs.Providers.OpenCode;
using HagiCode.Libs.Providers.QoderCli;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHagiCodeLibs();

await using var provider = services.BuildServiceProvider();
var claude = provider.GetRequiredService<ICliProvider<ClaudeCodeOptions>>();
var codebuddy = provider.GetRequiredService<ICliProvider<CodebuddyOptions>>();
var copilot = provider.GetRequiredService<ICliProvider<CopilotOptions>>();
var codex = provider.GetRequiredService<ICliProvider<CodexOptions>>();
var gemini = provider.GetRequiredService<ICliProvider<GeminiOptions>>();
var hermes = provider.GetRequiredService<ICliProvider<HermesOptions>>();
var kimi = provider.GetRequiredService<ICliProvider<KimiOptions>>();
var kiro = provider.GetRequiredService<ICliProvider<KiroOptions>>();
var opencode = provider.GetRequiredService<ICliProvider<OpenCodeOptions>>();
var qoderCli = provider.GetRequiredService<ICliProvider<QoderCliOptions>>();
```

OpenCode uses an HTTP runtime/session model instead of ACP. Consumers can attach to an existing runtime or launch a local `opencode serve`:

```csharp
await foreach (var message in opencode.ExecuteAsync(
                   new OpenCodeOptions
                   {
                       WorkingDirectory = "/path/to/repo",
                       Model = "anthropic/claude-sonnet-4",
                   },
                   "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}
```

`hagicode-core` 里的 `ClaudeCodeCliProvider`、`CodebuddyCliProvider`、`HermesCliProvider` 现已全部降为薄适配层，直接复用这里的 `ICliProvider<TOptions>` 实现。结论是：同一组 Claude raw-stream / resume、CodeBuddy ACP tool update、Hermes 会话复用与 fallback 语义，现在在 libs 与 core 间保持一致。

`ProviderRegistry` 也同步暴露了新的兼容别名，便于 console、测试和上层工厂共用同一命名面：

- `claude-code` -> `claude`, `claudecode`, `anthropic-claude`
- `codebuddy` -> `codebuddy-cli`
- `hermes` -> `hermes-cli`
- `opencode` -> `open-code`, `opencode-cli`

## OpenCode install metadata

`CliInstallRegistry` now tracks OpenCode as a publicly installable CLI descriptor:

- npm package: `opencode-ai@1.3.3`
- executable candidates: `opencode`
- public installability: `true`

The descriptor is aligned with the official OpenCode install guidance (`npm i -g opencode-ai@latest`) plus the pinned npm version snapshot resolved for this repository on `2026-03-28`. Real CLI validation still stays behind the `HAGICODE_REAL_CLI_TESTS` gate, so the default automated suite remains deterministic even when OpenCode is not installed locally.

## Shared pooling

Built-in providers now participate in a shared pooling architecture:

- ACP providers (`CodeBuddy`, `Gemini`, `Hermes`, `Kimi`, `Kiro`, `QoderCLI`) lease warm ACP sessions from the shared `CliProviderPoolCoordinator`.
- `Claude Code` reuses warm stdio transports when the effective session key and compatibility fingerprint match.
- `Codex` keeps thread-resume state in the shared pool for an explicit `LogicalSessionKey` or a stable `ThreadId`; a shared working directory alone never becomes the pool identity.
- `Copilot` reuses SDK-backed runtimes only for an explicit `SessionId`; `WorkingDirectory` remains part of the compatibility fingerprint, not the pool identity.

Every provider option record exposes `PoolSettings` so callers can disable pooling or tune provider-level behavior:

```csharp
var options = new CodebuddyOptions
{
    SessionId = "demo-session",
    WorkingDirectory = "/path/to/repo",
    PoolSettings = new CliPoolSettings
    {
        Enabled = true,
        IdleTimeout = TimeSpan.FromMinutes(15),
        MaxActiveSessions = 4,
        KeepAnonymousSessions = false
    }
};
```

Operational notes:

- Warm reuse only occurs when the logical session key and compatibility fingerprint still match.
- Each pooled entry executes one prompt at a time through an execution lock.
- Idle entries are evicted lazily on acquire/return or explicit reaper calls once `IdleTimeout` elapses.
- Faulted transports, broken ACP sessions, and failed Copilot runtimes are removed immediately instead of being returned to the pool.
- `CliAcpSessionPool.GetDiagnosticsSnapshot()` now reports global plus provider-scoped hit/miss/evict/fault counters, along with the latest eviction/fault reason; the pool also emits structured logs and `System.Diagnostics.Metrics` counters for monitoring hooks.

CodeBuddy execution options cover the ACP-specific runtime settings without forcing raw command lines:

```csharp
var options = new CodebuddyOptions
{
    Model = "glm-4.7",
    WorkingDirectory = "/path/to/repo",
    SessionId = "codebuddy-session-123",
    ModeId = "plan",
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

`SessionId` 会命中共享 ACP 池中的兼容会话；`ModeId` 会在 `session/new` 或 warm reuse 后重新下发，确保权限/执行模式与业务层请求保持一致。

Copilot execution options cover SDK-managed session startup without exposing raw prompt-mode wiring. Unsupported startup flags are filtered before launch, while compatible flags remain available through `AdditionalArgs`:

```csharp
var options = new CopilotOptions
{
    Model = "claude-sonnet-4.5",
    WorkingDirectory = "/path/to/repo",
    SessionId = "copilot-session-123",
    Permissions = new CopilotPermissionOptions
    {
        AllowAllTools = true,
        AllowedPaths = ["/path/to/repo"]
    },
    AdditionalArgs = ["--config-dir", "/path/to/.copilot"]
};

await foreach (var message in copilot.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}
```

Set `SessionId` when you want provider-native Copilot resume semantics. The provider first attempts SDK resume for that id, falls back to creating a new session pinned to the requested id when nothing persisted yet, and emits `session.started`, `session.resumed`, or `session.reused` messages accordingly. Requests without `SessionId` stay anonymous, so the same `WorkingDirectory` alone does not trigger warm reuse.

Codex execution options cover the common CLI settings without forcing raw command lines:

```csharp
var options = new CodexOptions
{
    Model = "gpt-5-codex",
    SandboxMode = "workspace-write",
    ApprovalPolicy = "never",
    WorkingDirectory = "/path/to/repo",
    LogicalSessionKey = "session-123|/path/to/repo|codex|gpt-5-codex",
    AddDirectories = ["/path/to/repo"],
    SkipGitRepositoryCheck = true,
};

await foreach (var message in codex.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine(message.Type);
}
```

For pooled Codex sessions, `LogicalSessionKey` should remain stable for the same logical conversation and differ across parallel conversations that share a repository path. The provider isolates execution locks by that key, records acquire/wait/reindex diagnostics, and registers a thread-based resume alias after `thread.started`. If both `LogicalSessionKey` and `ThreadId` are absent, the request stays anonymous and does not reuse a pooled entry solely because the directory matches.

Hermes execution options cover the managed `hermes acp` bootstrap path without forcing raw process wiring. `SessionId` is treated as an in-memory conversation key for the current provider instance, rather than a cross-process resume token:

```csharp
var options = new HermesOptions
{
    Model = "hermes/default",
    WorkingDirectory = "/path/to/repo",
    SessionId = "hermes-session-123",
    ModeId = "analysis",
    Arguments = ["acp"],
    EnvironmentVariables = new Dictionary<string, string?>
    {
        ["HERMES_TOKEN"] = "<token>"
    }
};

await foreach (var message in hermes.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}
```

Hermes 的 `SessionId` 仍表示当前 provider 实例内的会话复用键；`ModeId` 现在会进入共享池指纹，并在新建/复用 ACP 会话后重新应用。

Gemini execution options cover ACP bootstrap, optional authentication, and `session/load` resume without forcing consumers to handcraft JSON-RPC calls:

```csharp
var options = new GeminiOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "gemini-2.5-pro",
    AuthenticationMethod = "token",
    AuthenticationToken = "<token>",
    AuthenticationInfo = new Dictionary<string, string?>
    {
        ["region"] = "global"
    },
    ExtraArguments = ["--profile", "smoke"]
};

await foreach (var message in gemini.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}
```

Kimi execution options cover ACP bootstrap, optional authentication, and `session/load` resume without forcing consumers to handcraft JSON-RPC calls:

```csharp
var options = new KimiOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "kimi-k2.5",
    AuthenticationMethod = "token",
    AuthenticationToken = "<token>",
    AuthenticationInfo = new Dictionary<string, string?>
    {
        ["region"] = "cn"
    },
    ExtraArguments = ["--profile", "smoke"]
};

await foreach (var message in kimi.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}
```

Kiro execution options cover the shared ACP bootstrap path with optional authentication and custom bootstrap overrides:

```csharp
var options = new KiroOptions
{
    Model = "kiro-default",
    WorkingDirectory = "/path/to/repo",
    AuthenticationMethod = "token",
    AuthenticationToken = "<token>",
    ExtraArguments = ["--profile", "smoke"]
};

await foreach (var message in kiro.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}
```

If `initialize` advertises auth methods, the provider performs `authenticate` before creating the session. `PingAsync()` intentionally reports that state as a local-validation requirement instead of assuming credentials are available in public CI.

QoderCLI execution options mirror the ACP stdio providers while keeping the model override optional. `SessionId` is forwarded to the `session/new` or `session/resume` ACP call when present, and `ExtraArguments` are appended after the managed `--acp` bootstrap switch:

```csharp
var options = new QoderCliOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "qoder-max",
    EnvironmentVariables = new Dictionary<string, string?>
    {
        ["QODERCLI_TOKEN"] = "<token>"
    },
    ExtraArguments = ["--profile", "smoke"]
};

await foreach (var message in qoderCli.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}
```

If you omit `Model`, the provider forwards no model override so qodercli can use its own default. This keeps the integration aligned with local qodercli installations whose supported model names may vary by environment. The provider also forces each ACP session into `yolo` mode after `session/new` or `session/load`, so unattended prompts can use tool and file-system callbacks without interactive permission gates.

## Cross-platform CLI discovery validation

`repos/Hagicode.Libs/.github/workflows/cli-discovery-cross-platform.yml` runs real CLI discovery on `ubuntu-latest`, `macos-latest`, and `windows-latest`. The workflow uses `CiSetup.Console` to install all publicly available CLI tools and verify their availability on `PATH`, then runs provider-specific discovery tests for the installed CLIs.

### CLI install registry

All CLI metadata is centralized in `HagiCode.Libs.Core.Discovery.CliInstallRegistry`. Each `CliInstallDescriptor` records the provider name, npm package, pinned version, executable candidates, and whether the CLI is publicly installable. This registry is the single source of truth — updating it automatically affects CI install/verify behavior.

| Provider | npm Package | Pinned Version | CI-Covered |
|----------|-------------|----------------|------------|
| Claude Code | `@anthropic-ai/claude-code` | 2.1.79 | Yes |
| Copilot | `@github/copilot` | 1.0.10 | Yes |
| Codex | `@openai/codex` | 0.115.0 | Yes |
| CodeBuddy | (private) | — | No |
| Gemini | (local / explicit validation) | — | No |
| Hermes | (private) | — | No |
| Kimi | (local / explicit validation) | — | No |
| QoderCLI | (private) | — | No |

### CI-covered vs. locally-installed CLIs

CLIs with `IsPubliclyInstallable = true` are automatically installed and tested in CI. Today that public set is Claude Code, Copilot, and Codex. CLIs marked as not publicly installable (CodeBuddy, Gemini, Hermes, Kimi, QoderCLI) are skipped during CI setup and require local installation for validation.

### Local reproduction

To reproduce the same real-CLI validation locally from the `repos/Hagicode.Libs` directory:

```bash
# Install and verify all publicly available CLIs
dotnet run --project src/HagiCode.Libs.CiSetup.Console -- --install --verify

# Run CI-targeted discovery tests (Claude Code + Copilot + Codex)
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~ClaudeCodeProviderTests|FullyQualifiedName~CopilotProviderTests|FullyQualifiedName~CodexProviderTests" --logger "console;verbosity=normal"
```

The real-CLI path only checks executable discovery and the auth-free version ping behavior through the provider and dedicated console flows. It does not attempt interactive login or prompt execution, so the default test suite remains usable on machines without the external CLI installed.

## Real online API validation

`HagiCode.Libs.Skills` also includes an opt-in suite that calls the live search, well-known discovery, audit, telemetry, and GitHub metadata endpoints. The tests stay disabled by default and only run when `HAGICODE_REAL_ONLINE_API_TESTS` is enabled.

From `repos/Hagicode.Libs`, you can run:

```bash
HAGICODE_REAL_ONLINE_API_TESTS=1 dotnet test tests/HagiCode.Libs.Skills.Tests/HagiCode.Libs.Skills.Tests.csproj --filter "FullyQualifiedName~RealOnlineApiIntegrationTests" --logger "console;verbosity=normal"
```

GitHub Actions mirrors the same opt-in path in `repos/Hagicode.Libs/.github/workflows/online-api-integration.yml`.

### Provider-specific local validation

Each provider can be validated individually when its CLI is available on `PATH`:

**Claude Code:**
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~ClaudeCodeProviderTests"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Claude"
```

**Codex:**
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~CodexProviderTests"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Codex"
```

**Copilot:**
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~CopilotProviderTests"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Copilot"
dotnet run --project src/HagiCode.Libs.Copilot.Console -- --test-provider-full --model claude-sonnet-4.5 --config-dir .copilot --repo .
```

**CodeBuddy** (requires local installation):
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~Codebuddy"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Codebuddy"
```

**Hermes** (requires local installation):
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~Hermes"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Hermes"
```

**Gemini** (requires local installation and any required local auth bootstrap):
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~Gemini"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Gemini"
dotnet run --project src/HagiCode.Libs.Gemini.Console -- --test-provider-full --repo .
```

**Kimi** (requires local installation and any required local auth bootstrap):
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~Kimi"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Kimi"
dotnet run --project src/HagiCode.Libs.Kimi.Console -- --test-provider-full --repo .
```

**QoderCLI** (requires local installation):
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~QoderCli"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~QoderCli"
dotnet run --project src/HagiCode.Libs.QoderCli.Console -- --test-provider-full --repo .
```

## Design goals

- Zero heavy framework dependencies in the reusable libraries.
- Cross-platform CLI process management for Windows, macOS, and Linux.
- Stream-oriented provider integrations that can power higher-level HagiCode tools.
- Simple provider registration for DI and non-DI consumers.
