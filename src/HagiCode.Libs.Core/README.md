# HagiCode.Libs.Core

`HagiCode.Libs.Core` provides the low-level building blocks behind HagiCode CLI integrations. Use it when you need to discover installed executables, resolve the runtime environment for spawned tools, manage CLI processes, talk to ACP-compatible transports, or launch external commands through the new shared execution facade.

## What is included

- CLI executable discovery for local tools and custom paths, with structured results, version verification, and install-route attribution
- Runtime environment resolution, including the macOS shell-aware fallback
- Process management helpers for launching and monitoring CLI subprocesses
- A shared CLI execution facade with buffered and streaming result envelopes
- Transport, ACP session primitives, and shared ACP pool contracts for higher-level integrations

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

Set `HAGICODE_AGENT_CLI_PATH` to a platform-separated directory list when you need HagiCode to search custom Agent CLI install locations before falling back to `PATH`.

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

Redirected stdin, stdout, and stderr default to UTF-8 unless the caller explicitly overrides `CliExecutionRequest.InputEncoding` or `OutputEncoding`. This is the shared contract used by providers such as Codex so Windows prompt delivery keeps non-ASCII input valid without provider-local code-page workarounds.

## Structured discovery

`CliDiscoveryService` returns one typed result per provider: the resolved path, how it was found (`Source`), a best-effort install kind, the verified version, and any shadowed installs. It starts the CLI only to read its version, and only when `VerifyVersion` is true (the default). Set `VerifyVersion` to `false` to resolve the path without starting anything.

```csharp
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;

var service = new CliDiscoveryService(new CliExecutableResolver(), new CliProcessManager());
var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "ClaudeCode");
var result = await service.DiscoverAsync(descriptor);

Console.WriteLine(result.IsFound ? $"{result.ResolvedPath} {result.Version}" : "Claude Code not found.");
```

The [discovery guide](https://github.com/HagiCode-org/Hagicode.Libs/blob/main/docs/agent-cli-discovery-guide.md) covers the resolution order, install-kind attribution, and the mapping from the backend's discovery symbols.

## Adoption boundaries

- Use `CliExecutionFacade` when you want typed requests, policy evaluation, normalized diagnostics, and structured success/failure/timeout handling.
- Use `CliProcessManager` directly when you need a long-lived stdio transport such as ACP or provider-specific session protocols.
- Use the ACP pool contracts (`CliPoolSettings`, `CliAcpPoolRequest`, `PooledAcpSessionEntry`, and `CliAcpSessionPool`) when provider code needs warm session reuse, idle eviction, or deterministic fault cleanup.
- `CliAcpSessionPool.GetDiagnosticsSnapshot()` exposes read-only global and provider-scoped hit/miss/evict/fault counters, live entry counts, and the most recent eviction/fault reasons; the pool also emits structured logs plus `System.Diagnostics.Metrics` counters for warm reuse, misses, evictions, and faults.
- The embedded lifecycle improvements intentionally stay behind HagiCode namespaces; callers should continue passing structured argument tokens instead of raw shell strings.
