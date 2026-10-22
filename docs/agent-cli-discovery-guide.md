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
2. **Standard lookup**, in the order of `CliExecutableResolver`: process directory, working directory, `HAGICODE_AGENT_CLI_PATH`, then `PATH`. Matches inside `HAGICODE_AGENT_CLI_PATH` report source `AgentCliPath`; the rest report `Path`. On Windows the extensions follow a fixed order, and each agent's native-executable policy applies (see [Windows executable policy](#windows-executable-policy)).
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

## Windows executable policy

On Windows the lookup prefers a native executable, and each agent's policy in `CliInstallRegistry` decides how far it goes. `CliExecutableResolver.ResolveProviderExecutable` applies the policy for availability and launch, and `CliDiscoveryService` applies it for discovery, so both agree.

### Probe order

Probing runs tier by tier. Tier 1 is the process directory and the working directory, tier 2 is `HAGICODE_AGENT_CLI_PATH`, and tier 3 is `PATH`. A later tier is consulted only when an earlier one has no match, so an explicit `HAGICODE_AGENT_CLI_PATH` entry still outranks `PATH`. Inside a tier the extensions are tried in a fixed order, and each extension is tried across all of the tier's directories before the next one:

1. `.exe`
2. `.cmd`
3. `.bat`
4. the other `PATHEXT` entries, in their order (deduplicated, case-insensitive)

The fixed first three do not depend on how `PATHEXT` lists them. A native `.exe` in a later `PATH` directory therefore beats a `.cmd` in an earlier one. Unix resolution is unchanged.

### Per-agent policy

| Agent | Npm package | Windows native binary | Versions checked | Policy |
| --- | --- | --- | --- | --- |
| ClaudeCode | `@anthropic-ai/claude-code` | `@anthropic-ai/claude-code-win32-x64`: `claude.exe` at the package root | 2.1.168 (pin), 2.1.307 | `RequireNative` |
| Copilot | `@github/copilot` (Node loader) | `@github/copilot-win32-x64`: `copilot.exe` | 1.0.60 (pin), 1.0.102 | `RequireNative` |
| Codex | `@openai/codex` (Node script) | `@openai/codex-win32-x64`: `vendor\x86_64-pc-windows-msvc\codex\codex.exe` | 0.115.0 (pin), 0.163.0 | `RequireNative` |
| OpenCode | `opencode-ai` | `opencode-windows-x64`: `bin\opencode.exe` (and `-baseline`, `-windows-arm64`) | 1.3.3 (pin), 1.19.3 | `PreferNative` with a packaged layout |
| Reasonix | `reasonix` (Node script) | `@reasonix/cli-win32-x64`: `bin\reasonix.exe` | 2.47.1 (no registry pin) | `PreferNative` with a packaged layout |
| Pi, Omp, Gemini, QoderCLI, Codebuddy | Node scripts | none found | 1.4.0, 18.12.6, 0.65.0, 1.1.76, 2.172.1 | `PreferNative`, script only |
| Kimi, Hermes, DeepAgents, Kiro, Junie, Dsh | not npm packages in this registry | not examined | none | `PreferNative`, not examined |

The arm64 package names and file paths come from the optional-dependency lists and follow the x64 pattern, so they are unverified. The oldest release that ships the `.exe` was not established for any agent. Every registry entry carries its evidence note in `WindowsExecutableEvidence`.

An agent is promoted to `RequireNative` only when its Windows package or installer ships a native executable at the pinned version and at the current release, and every documented Windows install route yields that executable. OpenCode and Reasonix are the next promotion candidates. Their supported install routes were not checked.

### Packaged binaries behind a shim

For a `RequireNative` agent, and for an agent with a packaged layout, the `.exe` is often not on `PATH`: npm puts only the `.cmd` shim there, and the binary sits inside `node_modules`. The lookup therefore tests the packaged binary beside each shim directory, and in the npm global prefix (`%APPDATA%\npm`), for the process architecture only:

- nested: `<shim dir>\node_modules\<main package>\node_modules\<platform package>\<file>`
- hoisted: `<shim dir>\node_modules\<platform package>\<file>`
- Claude Code only, last: `<shim dir>\node_modules\@anthropic-ai\claude-code\bin\claude.exe`, used only when no platform package exists

A packaged binary is started directly, with no shell. It is attributed to `Npm`.

### Rules by policy

- **`RequireNative`:** only `.exe` files, or a packaged binary, are accepted. A `.cmd` or `.bat` is never selected. When only command scripts exist, the result is not found with the reason `NativeExecutableMissing`, and the skipped script paths are listed in `SkippedCommandScripts`. A configured path (`ConfiguredExecutablePath` or a provider's `ExecutablePath`) is still honored as given, including a `.cmd`.
- **`PreferNative`:** within each tier, an `.exe` comes first, then a packaged binary where one is known, then command scripts. The npm prefix is consulted only when no tier produced anything.
- **Other platforms:** resolution is the same as before.

### Not-found message

A provider whose agent is `RequireNative` reports the reason in its start error. For example:

```text
Unable to locate a Copilot executable. Install @github/copilot and ensure the CLI or a VS Code Copilot Chat shim is on PATH.
On Windows this agent requires its native executable, but only command script(s) were found: C:\Users\u\AppData\Roaming\npm\copilot.cmd.
Reinstall it with its native installer, or run 'npm install -g @github/copilot' and check that its platform package was installed (npm skips optional dependencies with --omit=optional).
```

The common causes are an install that skipped optional dependencies (`--omit=optional`, so the platform package is missing) and a release older than the native-binary line.

### Command-script launch rule

A resolved `.cmd` or `.bat` is never passed to Windows as the process file name. It runs through the `cmd.exe` in the Windows system directory (`Environment.SystemDirectory`), with the arguments string `/d /v:off /s /c "<line>"`. The shell is never taken from `PATH`, the working directory, or `ComSpec`. `/v:off` turns delayed expansion off. `.exe` files are started directly, with `ArgumentList` and no shell.

The library builds the line and verifies it before anything starts:

1. Each argument is escaped for the C runtime: backslashes before a `"` are doubled, each `"` becomes `\"`, and trailing backslashes are doubled when the value is quoted. A value is quoted when it is empty or contains whitespace or one of `& | < > ^ ( ) " , ; =`.
2. An argument is rejected, and the launch fails before any process starts, when it contains a NUL, carriage return or line feed; a `%` (which `cmd.exe` expands even inside quotes); or an odd number of `"`.
3. The whole line is walked with a model of `cmd.exe`: each `"` toggles the quote state, and outside quotes `& | < > ^ ( )` are live. A live metacharacter rejects the launch.

The rejection error is a `CliCommandScriptArgumentException`. It names the argument index and the rule, never the value, and tells the caller to use the CLI's native `.exe` or to pass the value another way (for example through stdin or a file).

### Worked example (`PreferNative`, OpenCode)

With `opencode.cmd` in the npm prefix and `bin\opencode.exe` in `opencode-windows-x64` beside it, the lookup returns the `.exe`, which starts with no shell. With only `opencode.cmd`, the lookup returns the `.cmd`, which starts through the rule above. The argument `{"mode":"fast"}` becomes `"{\"mode\":\"fast\"}"`, is accepted, and reaches the script as one argument. The argument `x" & echo injected & "y` is rejected with a `CliCommandScriptArgumentException` for argument 0, and nothing runs.

### Known constraints from the argument scan

Only a command-script launch is affected by the rules above. These arguments were checked and are not changed in this release:

- **Pi and Omp** pass the user prompt, and `--system-prompt` or `--append-system-prompt`, as argv on the one-shot path. On Windows, a prompt that contains `%`, a line break or an odd number of quotes is refused before launch. These are the agents most likely to hit the rule, because they are script-only. Passing the prompt through stdin is the fix, and it is a per-provider change.
- **Claude Code** passes `--system-prompt`, `--append-system-prompt`, and an `--mcp-config` JSON payload as argv. Only an explicitly configured `.cmd` path reaches `cmd.exe`.
- **Codex and Copilot** are `RequireNative`, so their arguments reach `cmd.exe` only through an explicitly configured `.cmd` path.
- The remaining one-shot providers (Gemini, QoderCLI, Codebuddy, Kimi, Junie, Hermes, Dsh, Kiro, Reasonix) send the prompt through the transport's stdin. Their argument builders were not read line by line.

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

When a lane fails, `--verify-discovery` prints the failing stage on stderr: `not found`, `version unavailable`, `version mismatch`, `install kind mismatch`, or `extension mismatch`. Exit codes are 3, 4, 5, 6, and 7 respectively. `--expect-extension <.ext>` asserts the file extension of the resolved path, and the Windows rows of `claude-npm`, `copilot-npm`, and `codex-npm` pass `.exe`. It also prints every probed location and a listing of each strategy directory, which shows whether an installer put the launcher somewhere unexpected.

## Promotion checklist

1. The lane passes on every operating system it targets, in the workflow (manual dispatch or the weekly run).
2. Set `blocking: true` for its matrix entries in `agent-cli-discovery-integration.yml`.
3. Update its row in the lane status table above and in the README.
4. If the installer contract changed, update the registry and the installer, and the contract table in this guide.
