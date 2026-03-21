# HagiCode.Libs.Core

`HagiCode.Libs.Core` provides the low-level building blocks behind HagiCode CLI integrations. Use it when you need to discover installed executables, resolve the runtime environment for spawned tools, manage CLI processes, or talk to ACP-compatible transports.

## What is included

- CLI executable discovery for local tools and custom paths
- Runtime environment resolution, including the macOS shell-aware fallback
- Process management helpers for launching and monitoring CLI subprocesses
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

Use this package directly when you are building your own provider layer or integrating ACP-compatible tools without the higher-level provider abstractions from `HagiCode.Libs.Providers`.
