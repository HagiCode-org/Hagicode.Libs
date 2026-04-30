# HagiCode.Libs.Providers

`HagiCode.Libs.Providers` builds on `HagiCode.Libs.Core` and adds reusable provider abstractions plus built-in integrations for Claude Code, Copilot, Codex, DeepAgents, CodeBuddy, Hermes, Kimi, Kiro, and QoderCLI.

## What is included

- The `ICliProvider` and `ICliProvider<TOptions>` contracts for provider-oriented integrations
- Built-in provider implementations for the supported HagiCode CLI backends
- `AddHagiCodeLibs()` for dependency injection registration
- A provider registry for resolving providers by name or alias
- Registration of the shared `ICliExecutionFacade` for provider-side probes or adapters
- Registration of the shared `CliProviderPoolCoordinator`, Hermes pool defaults, and ACP/runtime reuse backends
- Single-attempt provider primitives that preserve native thread or session context for upstream retry orchestration

## Install

```bash
dotnet add package HagiCode.Libs.Providers
```

If your application uses dependency injection, also reference `Microsoft.Extensions.DependencyInjection`.

## Dependency injection entry point

```csharp
using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Codebuddy;
using HagiCode.Libs.Providers.Copilot;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.DeepAgents;
using HagiCode.Libs.Providers.Hermes;
using HagiCode.Libs.Providers.Kimi;
using HagiCode.Libs.Providers.Kiro;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHagiCodeLibs();

await using var serviceProvider = services.BuildServiceProvider();
var executionFacade = serviceProvider.GetRequiredService<ICliExecutionFacade>();
var codebuddy = serviceProvider.GetRequiredService<ICliProvider<CodebuddyOptions>>();
var copilot = serviceProvider.GetRequiredService<ICliProvider<CopilotOptions>>();
var codex = serviceProvider.GetRequiredService<ICodexProvider>();
var deepAgents = serviceProvider.GetRequiredService<ICliProvider<DeepAgentsOptions>>();
var hermes = serviceProvider.GetRequiredService<ICliProvider<HermesOptions>>();
var kimi = serviceProvider.GetRequiredService<ICliProvider<KimiOptions>>();
var kiro = serviceProvider.GetRequiredService<ICliProvider<KiroOptions>>();
```

The same DI graph also exposes the shared pool services for Hermes diagnostics or explicit cleanup:

```csharp
using HagiCode.Libs.Providers.Pooling;

var poolCoordinator = serviceProvider.GetRequiredService<CliProviderPoolCoordinator>();
var poolDefaults = serviceProvider.GetRequiredService<CliProviderPoolConfigurationRegistry>();
```

## Shared adapter parity

`Claude Code`、`CodeBuddy`、`Hermes` 现在与 `hagicode-core` 的对应 provider 薄适配层共享同一套 libs-backed 实现。重点是：

- `Claude Code` 继续保留 raw stream / resume 语义，但 `ClaudeCodeProvider` 现在固定按次启动并回收本地 CLI 进程，不再做 warm transport reuse
- `CodeBuddy` 的 tool update 归一化与 permission-mode 映射统一落在 `CodebuddyProvider`
- `Hermes` 的 ACP session reuse、fallback 文本聚合与 lifecycle 诊断统一落在 `HermesProvider`
- 自动重试调度属于 `hagicode-core` 的 backend wrapper；`HagiCode.Libs.Providers` 只负责单次执行并保留可恢复上下文

`ProviderRegistry` 的规范名称与兼容别名也已统一：

- `claude-code` -> `claude`, `claudecode`, `anthropic-claude`
- `codebuddy` -> `codebuddy-cli`
- `hermes` -> `hermes-cli`

## Provider usage

```csharp
using HagiCode.Libs.Providers.Copilot;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Codebuddy;
using HagiCode.Libs.Providers.DeepAgents;
using HagiCode.Libs.Providers.Hermes;
using HagiCode.Libs.Providers.Kimi;
using HagiCode.Libs.Providers.Kiro;

var codebuddyOptions = new CodebuddyOptions
{
    WorkingDirectory = "/path/to/repo",
    SessionId = "codebuddy-session-123",
    ModeId = "plan",
    Model = "glm-4.7"
};

await foreach (var message in codebuddy.ExecuteAsync(codebuddyOptions, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}

var hermesOptions = new HermesOptions
{
    WorkingDirectory = "/path/to/repo",
    SessionId = "hermes-session-123",
    ModeId = "bypassPermissions",
    Model = "hermes/default",
    Arguments = ["acp"]
};

await foreach (var message in hermes.ExecuteAsync(hermesOptions, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}

var copilotOptions = new CopilotOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "claude-sonnet-4.5",
    SessionId = "copilot-session-123",
    Permissions = new CopilotPermissionOptions
    {
        AllowAllTools = true,
        AllowedPaths = ["/path/to/repo"]
    },
    AdditionalArgs = ["--config-dir", "/path/to/.copilot"]
};

await foreach (var message in copilot.ExecuteAsync(copilotOptions, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}

// Copilot tool-call turns always terminate with either a "result" or "error"
// message so upstream Orleans/session UIs can leave the running state deterministically.

// Reuse a persisted provider-native Copilot conversation on the next call.
var resumedOptions = copilotOptions with { SessionId = "copilot-session-123" };

// Without SessionId, Copilot requests stay anonymous; WorkingDirectory and permission
// flags only shape the compatibility fingerprint and do not preserve prior runtime state on their own.

var codexOptions = new CodexSessionOptions
{
    ThreadOptions = new ThreadOptions
    {
        WorkingDirectory = "/path/to/repo",
        Model = "gpt-5-codex",
        SandboxMode = SandboxMode.WorkspaceWrite,
        ApprovalPolicy = ApprovalMode.Never,
        AdditionalDirectories = ["/path/to/repo"],
        SkipGitRepoCheck = true,
    }
};

await using var codexSession = await codex.CreateSessionAsync(codexOptions);
var codexResult = await codexSession.Thread.RunAsync("Reply with exactly the word 'pong'");
Console.WriteLine(codexResult.FinalResponse);

var deepAgentsOptions = new DeepAgentsOptions
{
    Model = "glm-5.1",
    WorkspaceRoot = "/path/to/repo",
    ModeId = "bypassPermissions",
    AgentName = "coding-assistant",
    AgentDescription = "Repo-aware DeepAgents ACP runner",
    SkillsDirectories = ["/path/to/skills"],
    ExtraArguments = ["--debug"]
};

await foreach (var message in deepAgents.ExecuteAsync(deepAgentsOptions, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}

// ModeId is the authoritative ACP session-mode contract for DeepAgents.
// Compatibility flags such as --auto-approve may still be forwarded, but cross-call
// resume depends on provider-native session/load support.

// Reuse the same Codex thread id to continue a previous conversation by setting
// CodexSessionOptions.ThreadId before calling CreateSessionAsync.

var kimiOptions = new KimiOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "kimi-k2.5",
    AuthenticationMethod = "token",
    AuthenticationToken = "<token>",
    ExtraArguments = ["--profile", "smoke"]
};

await foreach (var message in kimi.ExecuteAsync(kimiOptions, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}

var kiroOptions = new KiroOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "kiro-default",
    AuthenticationMethod = "token",
    AuthenticationToken = "<token>",
    ExtraArguments = ["--profile", "smoke"]
};

await foreach (var message in kiro.ExecuteAsync(kiroOptions, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}
```

## Vendored Codex SDK notice

The Codex integration vendors source from `ManagedCode.CodexSharpSDK` under `Codex/Vendored/`.
The upstream MIT license is preserved in source control at `Codex/Vendored/LICENSE.ManagedCode.CodexSharpSDK.txt` and is included in the NuGet package under `vendor/codex/`.

## Hermes pool controls

`Hermes` is the only built-in provider that still exposes `PoolSettings`:

```csharp
var hermesPoolOptions = new HermesOptions
{
    SessionId = "demo-session",
    WorkingDirectory = "/path/to/repo",
    PoolSettings = new CliPoolSettings
    {
        Enabled = true,
        IdleTimeout = TimeSpan.FromMinutes(10),
        MaxActiveSessions = 50,
        KeepAnonymousSessions = false
    }
};
```

Practical boundaries:

- `Hermes` pools live ACP sessions.
- `Hermes` includes `ModeId` in its reuse fingerprint and re-applies it after `session/new` or warm reuse.
- Setting `HermesOptions.PoolSettings.Enabled = false` falls back to the one-shot path without changing message semantics.
- Non-Hermes providers now execute as one-shot transports or processes. `SessionId` remains a provider-native continuity hint only where that backend supports it.
- Idle eviction is lazy and deterministic; if a lease faults, the coordinator disposes that entry immediately rather than returning it to the warm set.

## Adoption boundaries

- Interactive provider transports still use `CliProcessManager` directly because they need open stdio sessions.
- The new execution facade is intended for provider-facing adapters, diagnostics, and one-shot probes such as version checks.
- Provider callers should continue passing structured option models; the new facade is additive and does not replace provider-specific option records.
- `gemini` is the canonical built-in provider name; `ProviderRegistry` and the dedicated console also accept `gemini-cli` as an alias.
- `deepagents` is the canonical built-in provider name, and the managed runtime boots ACP through `deepagents --acp`.
- `kimi` is the canonical built-in provider name; `ProviderRegistry` and the dedicated console also accept `kimi-cli` as an alias.
- `kiro-cli` is the canonical built-in provider name across the shared provider registry and the dedicated console.
- Kiro discovery is strict: the shared provider only supports `kiro-cli` executables. Generic `kiro` launchers are not accepted for implicit discovery, explicit `ExecutablePath`, or readiness checks.
- `CliInstallRegistry` now treats DeepAgents as local-only validation metadata because the managed runtime expects a `deepagents` executable (or `uvx --from deepagents-cli deepagents --acp`) rather than the legacy `deepagents-acp` npm package.
