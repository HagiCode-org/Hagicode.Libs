# HagiCode.Libs.Providers

`HagiCode.Libs.Providers` builds on `HagiCode.Libs.Core` and adds reusable provider abstractions plus built-in integrations for Claude Code, Codex, CodeBuddy, Hermes, and QoderCLI.

## What is included

- The `ICliProvider` and `ICliProvider<TOptions>` contracts for provider-oriented integrations
- Built-in provider implementations for the supported HagiCode CLI backends
- `AddHagiCodeLibs()` for dependency injection registration
- A provider registry for resolving providers by name or alias

## Install

```bash
dotnet add package HagiCode.Libs.Providers
```

If your application uses dependency injection, also reference `Microsoft.Extensions.DependencyInjection`.

## Dependency injection entry point

```csharp
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Codex;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHagiCodeLibs();

await using var serviceProvider = services.BuildServiceProvider();
var codex = serviceProvider.GetRequiredService<ICliProvider<CodexOptions>>();
```

## Provider usage

```csharp
using HagiCode.Libs.Providers.Codex;

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
```

Use the typed option records under each provider namespace when you need provider-specific configuration such as CLI arguments, environment variables, model selection, or session resume data.
