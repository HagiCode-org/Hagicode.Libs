# Agent CLI discovery guide

This guide covers `HagiCode.Libs.Core.Discovery`: how an Agent CLI is located, how its install route is attributed, how its version is verified, and how CI proves each install route. The code is the source of truth; this page explains it and records the installer contracts and lane status behind the CI workflow.

- Architecture and API: this page
- Registry metadata: `src/HagiCode.Libs.Core/Discovery/CliInstallRegistry.cs`
- Install and verify commands: `src/HagiCode.Libs.CiSetup.Console/`
- Real-install tests: `tests/HagiCode.Libs.Core.Tests/Discovery/RealCliDiscoveryTests.cs`
- Workflows: [`.github/workflows/agent-cli-discovery-integration.yml`](../.github/workflows/agent-cli-discovery-integration.yml) (discovery lanes) and [`.github/workflows/cli-discovery-cross-platform.yml`](../.github/workflows/cli-discovery-cross-platform.yml) (invocation error contracts; the name predates this guide)
- Test tiers and switches: [testing-tiers.md](testing-tiers.md)
- Command reference and CI overview: [README](../README.md#cross-platform-cli-discovery-validation)

## Architecture

```
HagiCode.Libs.Core (packaged)
  Discovery/
    CliInstallDescriptor       provider metadata: npm package, pin, executable candidates, public flag,
                               version argument sets, install strategies
    CliInstallRegistry         the descriptors (single source of truth)
    CliInstallStrategy         one install route: kind, platforms, well-known directories, pin support
    CliExecutableResolver      PATH-style lookup (unchanged public behavior)
    CliVersionProbe            runs the version command on CliProcessManager and parses the output
    CliDiscoveryService        orchestrates lookup, policies, attribution, and probing
    CliDiscoveryOptions/Result the typed contract
    Copilot/KiroExecutablePathPolicy  provider-specific path rules
  Process/CliProcessManager    runs commands; ProcessStartContext.MaxCapturedOutputCharacters caps captured output
HagiCode.Libs.Providers (packaged)
  ServiceCollectionExtensions  registers CliDiscoveryService next to the resolver
HagiCode.Libs.CiSetup.Console (not packaged)
  --install (npm, native, Homebrew, WinGet), --verify, --verify-discovery
```

Installer commands live only in `CiSetup.Console`. The shipped package holds discovery facts (kind, platforms, directories, pin support) and never runs an installer.

## Usage

```csharp
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;

var service = new CliDiscoveryService(new CliExecutableResolver(), new CliProcessManager());
var descriptor = CliInstallRegistry.Descriptors.Single(d => d.ProviderName == "ClaudeCode");

var result = await service.DiscoverAsync(descriptor, new CliDiscoveryOptions
{
    VerifyVersion = true,
});

if (result.IsFound)
{
    Console.WriteLine($"{result.ResolvedPath} source={result.Source} kind={result.InstallKind} version={result.Version}");
}
else
{
    Console.WriteLine($"Not found. Probed {result.ProbedLocations.Count} locations.");
}
```

`CliDiscoveryServiceTests.Usage_example_from_the_guide_compiles_and_runs` compiles the same statements and runs them with `EnvironmentVariables` set to an empty `PATH`, so the test starts no process.

Options:

| Option | Default | Meaning |
| --- | --- | --- |
| `ConfiguredExecutablePath` | `null` | Tried first. If it does not resolve, discovery continues. |
| `VerifyVersion` | `true` | `false` resolves the path only and starts no CLI process. |
| `EnvironmentVariables` | `null` | Supplies `PATH`, `HAGICODE_AGENT_CLI_PATH`, `PATHEXT`, home variables, and the probe environment. `null` uses the runtime environment resolver when registered, otherwise the process environment. |
| `VersionProbeTimeout` | 5 s | The time limit for each version command. |

## Resolution order

Discovery stops at the first step that yields an executable. Later candidates are still reported as shadowed installs.

1. **Configured path.** `ConfiguredExecutablePath`. If it does not resolve, the next step runs. It never causes a not-found on its own.
2. **Standard lookup**, in the order of `CliExecutableResolver`: process directory, working directory, `HAGICODE_AGENT_CLI_PATH`, then `PATH`. Matches inside `HAGICODE_AGENT_CLI_PATH` report source `AgentCliPath`; the rest report `Path`.
3. **Well-known locations.** The directories of each install strategy that applies to the current platform, expanded from the supplied environment. Matches report source `WellKnownLocation`. A well-known match never overrides a `PATH` match; it is reported as shadowed.

Candidates are de-duplicated with the platform's case rule (case-insensitive on Windows). Two provider policies then apply:

- **Copilot:** `CopilotExecutablePathPolicy` moves the preferred executable to the front, preferring real binaries over the VS Code Copilot Chat shim. It is skipped when a configured path is given.
- **Kiro:** `KiroExecutablePathPolicy` drops every candidate whose file name does not start with `kiro-cli`.

### Version selection

Candidates are probed in order using the descriptor's `VersionArgumentSets`. The first version wins. A failed candidate is listed as shadowed with its failure category. Candidates after the winner are listed with no probe result (not probed). If every probe fails, the first candidate is reported as the path, and `VersionFailure` holds the category of the last attempt.

### Result fields

| Field | Meaning |
| --- | --- |
| `IsFound`, `ResolvedPath` | Whether an executable was selected, and its absolute path |
| `Source` | `Configured`, `AgentCliPath`, `Path`, `WellKnownLocation`, or `None` |
| `InstallKind` | Best-effort: `Npm`, `NativeInstaller`, `Homebrew`, `WinGet`, `LegacyLocal`, or `Unknown` |
| `Version` | The parsed version token, or `null` |
| `VersionFailure` | `null` when not probed or not found; `None` on success; otherwise `ProcessStartFailed`, `CommandFailed`, `InvalidOutput`, or `TimedOut` |
| `ProbedLocations` | Locations searched, in order |
| `ShadowedInstalls` | Other matches with their source, kind, and probe result (`null` when not probed) |

### Install-kind attribution

`InstallKind` is inferred separately from `Source`. For each applicable strategy in descriptor order, discovery checks:

1. whether the resolved path, or its final symlink target, lies under one of the strategy's directories;
2. for npm only, whether the final symlink target contains `node_modules/<package>`, or a `node_modules/<package>` directory sits next to the executable (this covers Windows npm shims).

The first match wins. When nothing matches, the kind is `Unknown`. The `--verify-discovery` command asserts a kind only when it is told to (or when a strategy is named), so a lane cannot pass on a different install.

## Core symbol mapping

`repos/hagicode-core` is a read-only reference and is not changed by this work. The backend can adopt these types later; most of that is a namespace change.

| Core symbol (read-only) | Libs counterpart |
| --- | --- |
| `CliVersionProbe.ProbeVersionAsync` / `TryGetVersionAsync` | `CliVersionProbe.ProbeVersionAsync` / `TryGetVersionAsync` (instance, built on `CliProcessManager`) |
| `CliVersionProbe.GetCopilotVersionArgumentSets` | `CliVersionProbe.GetCopilotVersionArgumentSets` (also exposed through the Copilot descriptor's `VersionArgumentSets`) |
| `CommandUtil.TryResolveExecutablePath` | `CliExecutableResolver.ResolveExecutablePath` or `CliDiscoveryService.DiscoverAsync` |
| `AgentCliRuntimeEnvironmentResolver` | `RuntimeEnvironmentResolver` (`IRuntimeEnvironmentResolver`) |
| `KiroExecutablePathPolicy` (internal) | `KiroExecutablePathPolicy` (public) |
| `AgentCliMonitoringDescriptor.ResolveExecutablePath` | `CliDiscoveryService.DiscoverAsync` with `ConfiguredExecutablePath` |

Provider `PingAsync` is unchanged. Its `Version` output stays as it is, so moving it to the shared probe is a separate change.

## Installer contracts

### Strategy directories and pin arguments in code

Each row matches a strategy in `CliInstallRegistry` and the installer that `CiSetup.Console` builds for it. A test-free check (run by the discovery workflow) confirms that each directory and pin argument appears in this section.

| Strategy | Directories (code) | Pin argument (code) | Pin status |
| --- | --- | --- | --- |
| npm | `%APPDATA%\npm` | `npm install --global <package>@<version>` | Pinned |
| native (`NativeInstaller`) | `~/.local/bin` | `bash -s <version>`; PowerShell `& (...) <version>` | Pinned |
| Homebrew | `/opt/homebrew/bin`, `/usr/local/bin` | none (`brew install --cask claude-code`) | Not pinnable |
| WinGet | `%LOCALAPPDATA%\Microsoft\WinGet\Links` | `--version <version>` | Pinned |
| legacy (`LegacyLocal`) | `~/.claude/local` | none (no installer) | Not pinnable |

The contract details behind each row follow.


Each route's facts come from its vendor or package manager. Status is `CONFIRMED` (documented by an official source), `PARTIAL` (documented indirectly), or `UNCONFIRMED` (no authoritative source found; the lane stays non-blocking).

| Route | Item | Status | Value | Source |
| --- | --- | --- | --- | --- |
| npm | Package and pin | CONFIRMED | `@anthropic-ai/claude-code`, pinned as `package@version`; fails if the version is not published. Requires Node.js 22+. | [Claude Code setup, npm](https://code.claude.com/docs/en/setup); [npm install](https://docs.npmjs.com/cli/v10/commands/npm-install) |
| npm | Deprecation | NOTE | The Claude Code README says npm installation is deprecated in favor of the native installer. The npm lanes stay because they are the existing CI route and the only pinned route for Copilot and Codex. | [anthropics/claude-code README](https://github.com/anthropics/claude-code) |
| native (Unix) | Install command | CONFIRMED | `curl -fsSL https://claude.ai/install.sh \| bash` (`bash -o pipefail -c ...` in CiSetup) | [Claude Code setup](https://code.claude.com/docs/en/setup) |
| native (Unix) | Version pin | CONFIRMED | `bash -s 2.1.168` pins; the script accepts a version or `latest` / `stable` | [Claude Code setup](https://code.claude.com/docs/en/setup) |
| native (Unix) | Launcher | CONFIRMED | `~/.local/bin/claude`, a symlink into `~/.local/share/claude/versions/` | [Claude Code setup, auto-updates](https://code.claude.com/docs/en/setup) |
| native (Unix) | Auto-update | CONFIRMED | `DISABLE_AUTOUPDATER=1` stops the background check; `claude install` still works | [Claude Code setup, disable auto-updates](https://code.claude.com/docs/en/setup) |
| native (Windows) | Install command | CONFIRMED | `irm https://claude.ai/install.ps1 \| iex`; pinned: `& ([scriptblock]::Create((irm https://claude.ai/install.ps1))) 2.1.168` | [Claude Code setup](https://code.claude.com/docs/en/setup) |
| native (Windows) | Launcher directory | PARTIAL | `%USERPROFILE%\.local\bin\claude.exe`, from the uninstall instructions. The bootstrap script delegates placement to `claude install`. | [Claude Code setup, uninstall](https://code.claude.com/docs/en/setup) |
| Homebrew | Cask | CONFIRMED | `brew install --cask claude-code` (stable channel; `claude-code@latest` also exists) | [formulae.brew.sh](https://formulae.brew.sh/cask/claude-code) |
| Homebrew | Version pin | UNCONFIRMED | No pin syntax is documented; the `@` suffix selects a channel. The registry therefore sets `SupportsVersionPin: false`. | [Cask Cookbook](https://docs.brew.sh/Cask-Cookbook) |
| Homebrew | Bin directory, Apple Silicon | CONFIRMED | `/opt/homebrew/bin` | [formulae.brew.sh](https://formulae.brew.sh/cask/claude-code@latest); [Homebrew installation](https://docs.brew.sh/Installation) |
| Homebrew | Bin directory, Intel | UNCONFIRMED | `/usr/local/bin` is kept as the Intel prefix; it is not in the fetched cask page. | [Homebrew installation](https://docs.brew.sh/Installation) |
| WinGet | Package identifier | CONFIRMED | `Anthropic.ClaudeCode` (portable manifest, command `claude`) | [winget-pkgs manifest](https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/a/Anthropic/ClaudeCode/2.1.109/Anthropic.ClaudeCode.installer.yaml); [setup](https://code.claude.com/docs/en/setup) |
| WinGet | Version pin | CONFIRMED | `winget install --id <id> --version <X>` specifies an exact version; the version must be published | [Microsoft Learn: winget install](https://learn.microsoft.com/en-us/windows/package-manager/winget/install) |
| WinGet | Links directory | UNCONFIRMED | `%LOCALAPPDATA%\Microsoft\WinGet\Links` appears only in third-party sources. Microsoft's docs do not describe it. The lane stays non-blocking. | Third-party only |

Copilot (`@github/copilot`) and Codex (`@openai/codex`) use the npm route at their registry pins. Their installer contracts were not researched separately; their lanes are blocking because the npm route is the same one that is already in CI.

## Lane status

The workflow `agent-cli-discovery-integration.yml` runs 14 lanes. A lane is blocking only after it passes on every operating system it targets, and this table is updated in the same change as the workflow.

| Lane | OS | Install strategy | Expected kind | Off-PATH step | Status | Reason |
| --- | --- | --- | --- | --- | --- | --- |
| claude-npm | ubuntu, macos, windows | `npm` | `Npm` | no (npm bin is on PATH) | Blocking | Existing npm route, pinned |
| claude-native | ubuntu | `native` | `NativeInstaller` | yes (`~/.local/bin`) | Non-blocking | Contract CONFIRMED by docs; not yet green on a runner |
| claude-native | macos | `native` | `NativeInstaller` | yes (`~/.local/bin`) | Non-blocking | Contract CONFIRMED by docs; not yet green on a runner |
| claude-native | windows | `native` | `NativeInstaller` | yes (`%USERPROFILE%\.local\bin`) | Non-blocking | Launcher directory PARTIAL |
| claude-homebrew | macos | `homebrew` | `Homebrew` | yes (`/opt/homebrew/bin`) | Non-blocking | No documented pin; Intel prefix UNCONFIRMED |
| claude-winget | windows | `winget` | `WinGet` | yes (`%LOCALAPPDATA%\Microsoft\WinGet\Links`) | Non-blocking | Links directory UNCONFIRMED |
| copilot-npm | ubuntu, macos, windows | `npm` | `Npm` | no | Blocking | Existing npm route, pinned |
| codex-npm | ubuntu, macos, windows | `npm` | `Npm` | no | Blocking | Existing npm route, pinned |

Why the npm lanes have no off-PATH step: the npm strategy declares no well-known directory on Unix, so `--without-install-dir-on-path` would have nothing to prove. On Windows the npm strategy directory is `%APPDATA%\npm`, which hosted runners already have on `PATH`; the off-PATH check is not part of these lanes.

## Lane diagnostics

When a lane fails, `--verify-discovery` prints the failing stage on stderr: `not found`, `version unavailable`, `version mismatch`, or `install kind mismatch`. Exit codes are 3, 4, 5, and 6 respectively. It also prints every probed location and a listing of each strategy directory, which shows whether an installer put the launcher somewhere unexpected.

## Promotion checklist

1. The lane passes on every operating system it targets, in the workflow (manual dispatch or the weekly run).
2. Set `blocking: true` for its matrix entries in `agent-cli-discovery-integration.yml`.
3. Update its row in the lane status table above and in the README.
4. If the installer contract changed, update the registry and the installer, and the contract table in this guide.
