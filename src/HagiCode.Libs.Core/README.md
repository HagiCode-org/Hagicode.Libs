# HagiCode.Libs.Core

`HagiCode.Libs.Core` provides the low-level building blocks behind HagiCode CLI integrations. Use it when you need to discover installed executables, resolve the runtime environment for spawned tools, manage CLI processes, talk to ACP-compatible transports, or launch external commands through the new shared execution facade.

## What is included

- CLI executable discovery for local tools and custom paths
- Runtime environment resolution, including the macOS shell-aware fallback
- Process management helpers for launching and monitoring CLI subprocesses
- A shared CLI execution facade with buffered and streaming result envelopes
- Transport and ACP session primitives for higher-level integrations

## Install

```bash
dotnet add package HagiCode.Libs.Core
```

## Minimal usage

Resolve an executable path and the effective environment before launching a CLI:

```csharp
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;

var executableResolver = new CliExecutableResolver();
var executablePath = executableResolver.ResolveFirstAvailablePath(["codex", "codex.exe"]);

var environmentResolver = new RuntimeEnvironmentResolver(new ProcessShellCommandRunner());
var environment = await environmentResolver.ResolveAsync();

Console.WriteLine(executablePath ?? "Codex CLI not found.");
Console.WriteLine(environment.TryGetValue("PATH", out var path) ? path : "PATH is unavailable.");
```

Execute a command through the shared execution facade without constructing `ProcessStartContext` directly:

```csharp
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Core.Process;

var facade = new CliExecutionFacade(
    new CliProcessManager(),
    new RuntimeEnvironmentResolver(new ProcessShellCommandRunner()));

var result = await facade.ExecuteAsync(new CliExecutionRequest
{
    ExecutablePath = "dotnet",
    Arguments = ["--info"],
    Timeout = TimeSpan.FromSeconds(10)
});

Console.WriteLine(result.Status);
Console.WriteLine(result.StandardOutput);
```

For long-running or interactive commands, call `ExecuteStreamingAsync()` to receive stdout and stderr events followed by a terminal `CliExecutionResult` envelope.

## Adoption boundaries

- Use `CliExecutionFacade` when you want typed requests, policy evaluation, normalized diagnostics, and structured success/failure/timeout handling.
- Use `CliProcessManager` directly when you need a long-lived stdio transport such as ACP or provider-specific session protocols.
- The embedded lifecycle improvements intentionally stay behind HagiCode namespaces; callers should continue passing structured argument tokens instead of raw shell strings.
