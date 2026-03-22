# HagiCode.Libs.Providers

`HagiCode.Libs.Providers` builds on `HagiCode.Libs.Core` and adds reusable provider abstractions plus built-in integrations for Claude Code, Copilot, Codex, CodeBuddy, Hermes, Kimi, Kiro, and QoderCLI.

## What is included

- The `ICliProvider` and `ICliProvider<TOptions>` contracts for provider-oriented integrations
- Built-in provider implementations for the supported HagiCode CLI backends
- `AddHagiCodeLibs()` for dependency injection registration
- A provider registry for resolving providers by name or alias
- Registration of the shared `ICliExecutionFacade` for provider-side probes or adapters

## Install

```bash
dotnet add package HagiCode.Libs.Providers
```

If your application uses dependency injection, also reference `Microsoft.Extensions.DependencyInjection`.

## Dependency injection entry point

```csharp
using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Copilot;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Kimi;
using HagiCode.Libs.Providers.Kiro;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHagiCodeLibs();

await using var serviceProvider = services.BuildServiceProvider();
var executionFacade = serviceProvider.GetRequiredService<ICliExecutionFacade>();
var copilot = serviceProvider.GetRequiredService<ICliProvider<CopilotOptions>>();
var codex = serviceProvider.GetRequiredService<ICliProvider<CodexOptions>>();
var kimi = serviceProvider.GetRequiredService<ICliProvider<KimiOptions>>();
var kiro = serviceProvider.GetRequiredService<ICliProvider<KiroOptions>>();
```

## Provider usage

```csharp
using HagiCode.Libs.Providers.Copilot;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Kimi;
using HagiCode.Libs.Providers.Kiro;

var copilotOptions = new CopilotOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "claude-sonnet-4.5",
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

var options = new CodexOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "gpt-5-codex",
    SandboxMode = "workspace-write",
    ApprovalPolicy = "never",
    AddDirectories = ["/path/to/repo"],
    SkipGitRepositoryCheck = true,
};

await foreach (var message in codex.ExecuteAsync(options, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}

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

## Adoption boundaries

- Interactive provider transports still use `CliProcessManager` directly because they need open stdio sessions.
- The new execution facade is intended for provider-facing adapters, diagnostics, and one-shot probes such as version checks.
- Provider callers should continue passing structured option models; the new facade is additive and does not replace provider-specific option records.
- `kimi` is the canonical built-in provider name; `ProviderRegistry` and the dedicated console also accept `kimi-cli` as an alias.
- `kiro` is the canonical built-in provider name; `ProviderRegistry` and the dedicated console also accept `kiro-cli` as an alias.
- `CliInstallRegistry` currently marks both Kimi and Kiro as local-only validation metadata (`IsPubliclyInstallable = false`), so default public CI does not assume their credentials or installation are available.
