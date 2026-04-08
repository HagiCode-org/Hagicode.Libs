# HagiCode.Libs.Providers

`HagiCode.Libs.Providers` builds on `HagiCode.Libs.Core` and adds reusable provider abstractions plus built-in integrations for Claude Code, Copilot, Codex, DeepAgents, CodeBuddy, Hermes, Kimi, Kiro, and QoderCLI.

## What is included

- The `ICliProvider` and `ICliProvider<TOptions>` contracts for provider-oriented integrations
- Built-in provider implementations for the supported HagiCode CLI backends
- `AddHagiCodeLibs()` for dependency injection registration
- A provider registry for resolving providers by name or alias
- Registration of the shared `ICliExecutionFacade` for provider-side probes or adapters
- Registration of the shared `CliProviderPoolCoordinator`, provider-scoped pool defaults, and ACP/runtime reuse backends

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
var codex = serviceProvider.GetRequiredService<ICliProvider<CodexOptions>>();
var deepAgents = serviceProvider.GetRequiredService<ICliProvider<DeepAgentsOptions>>();
var hermes = serviceProvider.GetRequiredService<ICliProvider<HermesOptions>>();
var kimi = serviceProvider.GetRequiredService<ICliProvider<KimiOptions>>();
var kiro = serviceProvider.GetRequiredService<ICliProvider<KiroOptions>>();
```

The same DI graph also exposes the shared pool services when advanced callers need diagnostics or explicit cleanup:

```csharp
using HagiCode.Libs.Providers.Pooling;

var poolCoordinator = serviceProvider.GetRequiredService<CliProviderPoolCoordinator>();
var poolDefaults = serviceProvider.GetRequiredService<CliProviderPoolConfigurationRegistry>();
```

## Shared adapter parity

`Claude Code`、`CodeBuddy`、`Hermes` 现在与 `hagicode-core` 的对应 provider 薄适配层共享同一套 libs-backed 实现。重点是：

- `Claude Code` 继续保留 raw stream / resume 语义，但真实执行与 warm transport reuse 统一落在 `ClaudeCodeProvider`
- `CodeBuddy` 的 ACP session reuse、tool update 归一化与 permission-mode 映射统一落在 `CodebuddyProvider`
- `Hermes` 的 ACP session reuse、fallback 文本聚合与 lifecycle 诊断统一落在 `HermesProvider`

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
    ModeId = "analysis",
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

// Reuse a persisted provider-native Copilot conversation on the next call.
var resumedOptions = copilotOptions with { SessionId = "copilot-session-123" };

// Without SessionId, Copilot requests stay anonymous; WorkingDirectory and permission
// flags only shape the compatibility fingerprint and will not hit the shared pool alone.

var options = new CodexOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "gpt-5-codex",
    SandboxMode = "workspace-write",
    ApprovalPolicy = "never",
    LogicalSessionKey = "session-123|/path/to/repo|codex|gpt-5-codex",
    AddDirectories = ["/path/to/repo"],
    SkipGitRepositoryCheck = true,
};

await foreach (var message in codex.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}

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
// Compatibility flags such as --auto-approve may still be forwarded, but pooled
// sessions re-apply ModeId before every prompt and a faulted entry is evicted so
// later requests can cold-start a fresh session.

// Reuse the same logical Codex session key to keep thread continuity on later calls.
// The logical key stays stable, while the compatibility fingerprint decides whether
// the existing warm runtime can be resumed or must be rebuilt under that same key.
// If LogicalSessionKey and ThreadId are both absent, the request remains anonymous
// even when WorkingDirectory matches a previous call.

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

## Pool controls

Every built-in provider option record now exposes `PoolSettings`:

```csharp
var qoderOptions = new QoderCliOptions
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

- `CodeBuddy`, `Gemini`, `Hermes`, `Kimi`, `Kiro`, and `QoderCLI` pool live ACP sessions.
- `DeepAgents` pools live ACP sessions, treats `DeepAgentsOptions.ModeId` as the authoritative typed session-mode signal, re-applies it before prompt execution, and evicts faulted entries so later acquires can cold-start a fresh session.
- `Claude Code` pools warm stdio transports keyed by stable session identity plus a compatibility fingerprint built from the effective startup shape.
- `Codex` keeps logical session identity stable, but rebuilds pooled entries when the compatibility fingerprint changes before reusing a previous thread binding.
- `Copilot` only treats explicit `SessionId` as the logical reuse key; permissions and runtime flags stay in the compatibility fingerprint so config changes cold-start a replacement runtime under the same session identity.
- `CodeBuddy` and `Hermes` now include `ModeId` in their reuse fingerprint and re-apply it after `session/new` or warm reuse.
- Pooling can be disabled per provider call, which falls back to the original one-shot behavior without changing message semantics.
- Idle eviction is lazy and deterministic; if a lease faults, the coordinator disposes that entry immediately rather than returning it to the warm set.

## Adoption boundaries

- Interactive provider transports still use `CliProcessManager` directly because they need open stdio sessions.
- The new execution facade is intended for provider-facing adapters, diagnostics, and one-shot probes such as version checks.
- Provider callers should continue passing structured option models; the new facade is additive and does not replace provider-specific option records.
- `gemini` is the canonical built-in provider name; `ProviderRegistry` and the dedicated console also accept `gemini-cli` as an alias.
- `deepagents` is the canonical built-in provider name, and the managed runtime boots ACP through `deepagents --acp`.
- `kimi` is the canonical built-in provider name; `ProviderRegistry` and the dedicated console also accept `kimi-cli` as an alias.
- `kiro` is the canonical built-in provider name; `ProviderRegistry` and the dedicated console also accept `kiro-cli` as an alias.
- `CliInstallRegistry` now treats DeepAgents as local-only validation metadata because the managed runtime expects a `deepagents` executable (or `uvx --from deepagents-cli deepagents --acp`) rather than the legacy `deepagents-acp` npm package.
