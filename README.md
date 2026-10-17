# HagiCode.Libs

[![license](https://img.shields.io/badge/license-MIT-ffd43b)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512bd4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![NuGet Core](https://img.shields.io/nuget/v/HagiCode.Libs.Core?logo=nuget&label=Libs.Core)](https://www.nuget.org/packages/HagiCode.Libs.Core)
[![NuGet Providers](https://img.shields.io/nuget/v/HagiCode.Libs.Providers?logo=nuget&label=Libs.Providers)](https://www.nuget.org/packages/HagiCode.Libs.Providers)
[![NuGet publish](https://img.shields.io/github/actions/workflow/status/HagiCode-org/Hagicode.Libs/nuget-publish.yml?branch=main&label=NuGet%20publish)](https://github.com/HagiCode-org/Hagicode.Libs/actions/workflows/nuget-publish.yml)

`HagiCode.Libs` is a lightweight .NET 10 library workspace for reusable HagiCode CLI integrations and repository exploration.

## Projects

- `src/HagiCode.Libs.Core` - transport, process management, executable discovery, and runtime environment resolution.
- `src/HagiCode.Libs.Prompts` - file-backed Handlebars prompt catalog loading, override merging, locale fallback, diagnostics, and rendering.
- `src/HagiCode.Libs.Providers` - provider abstractions, the Claude Code/Copilot/Codex/DeepAgents/CodeBuddy/Gemini/Hermes/Kimi/Kiro/OpenCode/Pi/QoderCLI/Reasonix providers, and optional DI registration.
- `src/HagiCode.Libs.Skills` - skills-oriented infrastructure. Its first shipped capability is a typed online API client for search, well-known discovery, audit, telemetry, and GitHub metadata/tree requests.
- `src/HagiCode.Libs.Imaging` - in-process AVIF conversion (PNG, JPEG, WebP and GIF first frame to AVIF) with bundled libvips native libraries. No `ffmpeg`, `avifenc` or other tool is used or looked up. Tests: `tests/HagiCode.Libs.Imaging.Tests`. Backend integration guide: `docs/avif-backend-integration-guide.md`.
- `src/HagiCode.Libs.Exploration` - Git repository discovery and state inspection.
- `tests/*` - NUnit coverage for each project. Shared test settings, the NUnit and Shouldly packages and the global usings live in `tests/Directory.Build.props`; each test project lists only its own references.

## Getting started

```bash
cd repos/Hagicode.Libs
dotnet build HagiCode.Libs.slnx
dotnet test HagiCode.Libs.slnx
dotnet test tests/HagiCode.Libs.Imaging.Tests/HagiCode.Libs.Imaging.Tests.csproj  # AVIF suite; native tests fail on supported platforms when the runtime cannot load
```

## NuGet publishing

`repos/Hagicode.Libs/.github/workflows/nuget-publish.yml` now supports two publish modes for `src/HagiCode.Libs.Core`, `src/HagiCode.Libs.Prompts`, `src/HagiCode.Libs.Providers`, `src/HagiCode.Libs.Skills`, and `src/HagiCode.Libs.Imaging` (AVIF conversion with bundled native libraries; the package step also checks its native layout with `scripts/imaging/verify_native_layout.py`):

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

`src/HagiCode.Libs.ClaudeCode.Console`, `src/HagiCode.Libs.Copilot.Console`, `src/HagiCode.Libs.Codex.Console`, `src/HagiCode.Libs.DeepAgents.Console`, `src/HagiCode.Libs.Dsh.Console`, `src/HagiCode.Libs.Codebuddy.Console`, `src/HagiCode.Libs.Gemini.Console`, `src/HagiCode.Libs.Junie.Console`, `src/HagiCode.Libs.Hermes.Console`, `src/HagiCode.Libs.Kimi.Console`, `src/HagiCode.Libs.Kiro.Console`, `src/HagiCode.Libs.OpenCode.Console`, `src/HagiCode.Libs.Pi.Console`, `src/HagiCode.Libs.QoderCli.Console`, and `src/HagiCode.Libs.Reasonix.Console` are dedicated provider consoles built on the shared `HagiCode.Libs.ConsoleTesting` harness.

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

uv tool install deepagents-cli
dotnet run --project src/HagiCode.Libs.DeepAgents.Console -- --help
dotnet run --project src/HagiCode.Libs.DeepAgents.Console
dotnet run --project src/HagiCode.Libs.DeepAgents.Console -- --test-provider deepagents-acp
dotnet run --project src/HagiCode.Libs.DeepAgents.Console -- --test-provider-full --model glm-5.1 --workspace . --skill ./skills --arg --debug
dotnet run --project src/HagiCode.Libs.DeepAgents.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.DeepAgents.Console -- --test-provider-full --toolcall --verbose
dotnet run --project src/HagiCode.Libs.DeepAgents.Console -- --test-provider-full --toolcall-case mixed
dotnet run --project src/HagiCode.Libs.DeepAgents.Console -- --test-all deepagents

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

dotnet run --project src/HagiCode.Libs.Dsh.Console -- --help
dotnet run --project src/HagiCode.Libs.Dsh.Console
dotnet run --project src/HagiCode.Libs.Dsh.Console -- --test-provider dsh-cli
dotnet run --project src/HagiCode.Libs.Dsh.Console -- --test-provider-full --workspace .
dotnet run --project src/HagiCode.Libs.Dsh.Console -- --test-provider-full --provider omniroute --model claude/claude-haiku-5-5 --dsh-home /path/to/dsh-home --verbose
dotnet run --project src/HagiCode.Libs.Dsh.Console -- --test-all dsh

dotnet run --project src/HagiCode.Libs.Junie.Console -- --help
dotnet run --project src/HagiCode.Libs.Junie.Console
dotnet run --project src/HagiCode.Libs.Junie.Console -- --test-provider junie-cli
dotnet run --project src/HagiCode.Libs.Junie.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.Junie.Console -- --test-provider-full --model sonnet --arg --brave
dotnet run --project src/HagiCode.Libs.Junie.Console -- --test-all junie

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

dotnet run --project src/HagiCode.Libs.Pi.Console -- --help
dotnet run --project src/HagiCode.Libs.Pi.Console
dotnet run --project src/HagiCode.Libs.Pi.Console -- --test-provider pi-cli
dotnet run --project src/HagiCode.Libs.Pi.Console -- --test-provider-full --provider omniroute --model glm/glm-4.7 --repo .
dotnet run --project src/HagiCode.Libs.Pi.Console -- --test-all pi

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

dotnet run --project src/HagiCode.Libs.Reasonix.Console -- --help
dotnet run --project src/HagiCode.Libs.Reasonix.Console
dotnet run --project src/HagiCode.Libs.Reasonix.Console -- --test-provider reasonix
dotnet run --project src/HagiCode.Libs.Reasonix.Console -- --test-provider-full --repo .
dotnet run --project src/HagiCode.Libs.Reasonix.Console -- --test-provider-full --model <provider>/<model>
dotnet run --project src/HagiCode.Libs.Reasonix.Console -- --test-all reasonix
```

- No arguments run the default Claude suite.
- 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt`、`Session Restore (Session Id)` 和 `Session Restore (Resume Id)`。
  - `Session Restore (Session Id)` 用 `--session-id <id>` 开启会话，再用 `--resume <id>` 恢复，并要求 CLI 回报的 `session_id` 与请求的一致。
  - `Session Restore (Resume Id)` 不指定 id，读取首轮 stream 里回报的 `session_id`，再用 `--resume <id>` 恢复。
  - 两个 Session Restore 场景都用运行时生成的随机 secret 校验，只有真正取回上一轮内容才算通过。
  - Claude provider 只按显式 session id 恢复会话，从不发出 `--continue`；`ExtraArgs` 里的 `continue` 会被拒绝。
- `--test-provider` runs the provider ping flow for the Claude console only.
- `--test-provider-full` and `--test-all` run the full provider-scoped suite.
- `--repo <path>` adds the repository analysis scenario to the suite.
- `--subagent` adds the `Background Subagent` scenario (about 20-30 seconds, uses extra tokens). It asks Claude to launch a background subagent and passes only when the final `result` carries a token the subagent generated at run time, i.e. the stream was tracked past the launch turn's own `result`. It reports "inconclusive" if Claude does not launch a background subagent; run it again in that case.
- `--api-key <key>` and `--model <model>` override Claude execution options for scenario runs.
- `--effort <level>` overrides the Claude `Effort` option and is forwarded to the CLI as `--effort`. Supported levels are `low`, `medium`, `high`, `xhigh`, and `max` (case-insensitive).
- No arguments also run the default Copilot suite.
- Copilot 默认套件当前包含 `Ping`、`Simple Prompt` 和 `Complex Prompt`。
- Copilot accepts `--model <model>`, `--executable <path>`, `--auth-source <mode>`, `--github-token <token>`, `--config-dir <path>`, and compatible filtered startup overrides such as `--log-level <level>`.
- Copilot repository analysis remains opt-in via `--repo <path>`, and the provider filters unsupported startup flags before SDK launch with deterministic diagnostics.
- No arguments also run the default Codex suite.
- Codex 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- Codex accepts `--model <model>`, `--sandbox <mode>`, `--approval-policy <mode>`, `--api-key <key>`, and `--base-url <url>` overrides.
- Codex repository analysis remains opt-in via `--repo <path>` and reuses the same shared report formatter.
- No arguments also run the default DeepAgents suite.
- DeepAgents 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- DeepAgents accepts the provider aliases `deepagents` and `deepagents-acp`.
- DeepAgents accepts `--model <model>`, `--workspace <path>`, `--name <name>`, `--description <text>`, repeated `--skill <path>`, repeated `--memory <path>`, `--executable <path>`, repeated `--arg <value>`, `--toolcall`, and `--toolcall-case <parsing|failure|mixed>` overrides.
- DeepAgents repository summary remains opt-in via `--repo <path>`, and the runtime now prefers an explicit executable, then `deepagents --acp`, then `uvx --from deepagents-cli deepagents --acp`.
- `--toolcall` appends the dedicated DeepAgents toolcall diagnostics on top of the baseline suite, while `--toolcall-case <name>` narrows execution to one toolcall scenario without dropping `Ping`, `Simple Prompt`, `Complex Prompt`, or `Session Resume`.
- Toolcall verbose runs print `RawMessageTypes`, `LifecycleTrace`, and per-tool metadata such as `Tool[1]: stage=tool.call, name=bash, id=tool-parse-1`, which makes the first parser divergence easy to spot in CI logs.
- DeepAgents is now treated as local-only validation metadata in `CliInstallRegistry`; install the `deepagents` CLI separately before running the dedicated console commands above.
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
- No arguments also run the default Dsh suite.
- Dsh 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt`、`Session Resume` 和 `Tool Call`。
- Dsh accepts `--provider <id>` with `--model <model>` (for example `--provider omniroute --model claude/claude-haiku-5-5`). Libs sends dsh's advertised `["<provider>","<model>"]` value through `session/set_config_option` and forwards the model without splitting it. `--model` without `--provider` fails before the ACP session starts. Also accepted: `--executable <path>`, repeated `--arg <value>` overrides, `--dsh-home <path>` (exported as `DSH_HOME` to the spawned process), and `--verbose` (adds provider, model, and workspace detail lines to the output).
- Dsh runs in the directory given by `--workspace <path>`; it has no `--repo` option. `dsh-cli` remains a dedicated-console alias for the canonical `dsh` provider name.
- No arguments also run the default Junie suite.
- Junie 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- Junie accepts `--model <model>`, `--executable <path>`, repeated `--arg <value>` overrides, and optional `--auth-method <id>` / `--auth-token <token>` bootstrap hints (`--auth-token` maps to `JUNIE_API_KEY`).
- Junie repository summary remains opt-in via `--repo <path>`, and `junie-cli` remains a dedicated-console alias for the canonical `junie` provider name.
- No arguments also run the default Kimi suite.
- Kimi 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- Kimi accepts `--model <model>`, `--executable <path>`, repeated `--arg <value>` overrides, and optional `--auth-method <id>` / `--auth-token <token>` bootstrap hints.
- Kimi repository summary remains opt-in via `--repo <path>`, and `kimi-cli` remains a dedicated-console alias for the canonical `kimi` provider name.
- No arguments also run the default Kiro suite.
- Kiro 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- Kiro accepts `--model <model>`, `--executable <path>`, repeated `--arg <value>` overrides, and optional `--auth-method <id>` / `--auth-token <token>` / `--bootstrap-method <name>` bootstrap hints.
- Kiro repository summary remains opt-in via `--repo <path>`, and `kiro-cli` remains a dedicated-console alias for the canonical `kiro` provider name.
- No arguments also run the default Pi suite.
- Pi 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- Pi accepts `--provider <name>`, `--model <model>`, `--session-dir <path>`, `--thinking <level>`, `--executable <path>`, and repeated `--arg <value>` overrides.
- Pi repository summary remains opt-in via `--repo <path>`, and `pi-cli` remains a dedicated-console alias for the canonical `pi` provider name.
- No arguments also run the default OpenCode suite.
- OpenCode 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- OpenCode accepts `--model <model>`, `--executable <path>`, `--base-url <url>`, `--workspace <id>`, and repeated `--arg <value>`.
- OpenCode repository summary remains opt-in via `--repo <path>`, and `open-code` / `opencode-cli` both normalize to the canonical `opencode` provider name.
- No arguments also run the default QoderCLI suite.
- QoderCLI 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- No arguments also run the default Reasonix suite.
- Reasonix 默认套件当前包含 `Ping`、`Simple Prompt`、`Complex Prompt` 和 `Session Resume`。
- Reasonix accepts `--model <provider>/<model>` (a ref defined in your Reasonix configuration), `--executable <path>`, repeated `--arg <value>` overrides, and keeps repository summary opt-in via `--repo <path>`. The console targets the Reasonix 2.x ACP line (verified with `v2.41.0`).
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
using HagiCode.Libs.Providers.DeepAgents;
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
var codex = provider.GetRequiredService<ICodexProvider>();
var deepAgents = provider.GetRequiredService<ICliProvider<DeepAgentsOptions>>();
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

- npm package: `opencode-ai@1.3.3` (predates the OpenCode 2.0 runtime; see "OpenCode 2.0 runtime compatibility")
- executable candidates: `opencode`
- public installability: `true`

The descriptor is aligned with the official OpenCode install guidance (`npm i -g opencode-ai@latest`) plus the pinned npm version snapshot resolved for this repository on `2026-03-28`. Real CLI validation still stays behind the `HAGICODE_REAL_CLI_TESTS` gate, so the default automated suite remains deterministic even when OpenCode is not installed locally.

## OpenCode 2.0 runtime compatibility

`HagiCode.Libs.Providers` supports **OpenCode 2.0** (verified with `v2.0.32`). OpenCode 1.x is no longer supported: its routes answer with the Web UI HTML shell and HTTP 200, which the provider reports as an unsupported API version instead of a JSON error.

- **Owned runtime.** The provider launches `opencode serve`. OpenCode 2.0 prints `server listening on <url>` and `server password <value>` at startup; the provider accepts both lines, captures the password, and sends it as HTTP Basic credentials (username `opencode`) on every `/api/*` request. No setting is needed.
- **Attach mode.** When `OpenCodeOptions.BaseUrl` points at an externally managed runtime that requires a password, set `OpenCodeOptions.ServerPassword`. Without it, the health probe reports `authentication failed` in the `health_probe` stage.
- **Password handling.** The password is never returned in `CapturedOutput`, lifecycle `DiagnosticOutput`, exception messages, or the runtime key (only a SHA-256 digest of an attach password takes part in the key). `OpenCodeOptions` and `OpenCodeStandaloneServerOptions` do not print it in `ToString()`.
- **Prompts.** OpenCode 2.0 accepts the prompt as a queued inbox item. The assistant reply is assembled from `GET /api/event`: a turn completes on `session.execution.succeeded`, fails on `session.execution.failed`, and its text comes from `session.text.ended`. The model is set on the session through `POST /api/session/{id}/model`, not on the prompt.
- **Fixture.** `OpenCodeFixtureServer` simulates OpenCode 2.0 (Basic authentication on by default, `/api/*`, `/api/event`, HTML with HTTP 200 on other paths). Its handle exposes `ServerPassword` for attach-mode tests.

Run the OpenCode provider tests (no real OpenCode installation needed):

```bash
dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~OpenCode"
```

> Note: the `CliInstallRegistry` descriptor still pins `opencode-ai@1.3.3` (see below). That version is not compatible with this runtime and needs to move to an OpenCode 2.0 release before the install path can be used with it.

## Claude Code thinking display

`ClaudeCodeOptions.ThinkingDisplay` is forwarded to the CLI as `--thinking-display <mode>`. Supported modes are `summarized`, `omitted`, and `highlights` (case-insensitive, surrounding whitespace ignored); any other value throws `ArgumentException` before a process starts. When unset or empty no flag is sent, so the CLI and model default applies. Current Claude models then stream thinking blocks with empty text, and `summarized` is what makes the text come back. Display only changes visibility; summaries cost the same tokens as omitted thinking.

`--thinking-display` is hidden from `claude --help`. If a CLI exits at launch with an unknown-option error for exactly that flag, before any stream message, the provider starts one replacement process with the same arguments minus the flag, and remembers the executable as unsupported until the host process restarts. Any other failure, and any failure after the first message, is surfaced once with no retry. The `--settings` argument is never inspected or rewritten: `showThinkingSummaries` is ignored by the CLI in `--print` / stream-json mode.

Verified with Claude Code 2.1.292 and 2.1.295: `--thinking-display summarized` returns thinking text and omitting the flag returns empty thinking text. Older builds are covered only by the fake-CLI fallback tests.

## Pi provider

`PiProvider` runs `pi --mode json --print` and normalizes its JSONL stream into the shared `CliMessage` types. A request that sets `PiOptions.UserInputHandler` runs in Pi's RPC mode instead, so the agent can ask the user questions (see **Interactive questions** below).

- **Package and runtime:** the npm package is `@earendil-works/pi-coding-agent`, which requires Node `>=22.19.0`. The `CliInstallRegistry` entry records the package name but stays local-only (`IsPubliclyInstallable = false`, no pinned version); the Desktop manifest owns the Pi version.
- **Thinking levels:** `PiOptions.Thinking` accepts `off`, `minimal`, `low`, `medium`, `high`, `xhigh` and `max` (case-insensitive) and forwards the value as `--thinking`. Any other value throws `ArgumentException` before a process starts, and the message lists the valid values.
- **A provider needs a model:** setting `PiOptions.Provider` without `PiOptions.Model` (empty or whitespace counts as none) is a configuration error for every provider name, `omniroute` included. No process is started and the stream carries a single `terminal.failed` that names the provider, because Pi 1.x would exit with `--provider requires --model`.
- **Streaming formats:** Pi 0.84 and later emit `message_update` events that carry only the new fragment; earlier releases (0.78.x) carry a full `message` / `partial` snapshot. The provider recognizes the format per event, with no version probing. Incremental `assistant` and `assistant.thought` messages carry `delta: true` and contain only the new fragment, so a consumer appends them as they are: a whitespace-only fragment, or one equal to earlier text such as `The`, is real content. Messages from the snapshot format carry no `delta` marker and are still reconciled against earlier text. Tool input arrives as `tool.call` when Pi announces the call and as `tool.update` with the complete arguments; partial argument JSON is never forwarded.
- **New or resumed session:** the `session` header's `timestamp` is compared with the time the process was started. A header created by this run gives `session.started` with `resumeMode: "started"`, an older one gives `session.resumed`, and an id different from the requested one gives `restarted`. When the timestamp is missing or unreadable the provider falls back to comparing the returned id with the requested one.
- **Environment variables:** nothing is set by default. To avoid network activity at startup pass `PI_OFFLINE=1`, `PI_SKIP_VERSION_CHECK=1` and `PI_TELEMETRY=0` through `PiOptions.EnvironmentVariables`.
- **Interactive questions (`PiOptions.UserInputHandler`):** the handler is `null` by default and then nothing changes: the command line, the closed standard input, the completion on process exit and the absence of any extension are exactly what they were. JSON print mode has no UI, so a Pi that can ask must run in RPC mode. When a handler is set (and `DisableAllTools` is off; with every tool disabled the handler is ignored and the print path runs) the provider starts `pi --mode rpc` with the same model, prompt, session, thinking and tool options, adds the bundled `ask_user_question` extension with `-e` (and adds the tool to a non-empty `AllowedTools` allowlist), and sends the prompt as an RPC `prompt` command instead of an argument. Because RPC output has no session header, the session id is read with `get_state` and reported as the same `session.started` / `session.resumed` message the print path produces (RPC cannot tell a session it just created under a requested id from an existing one, so a requested id that Pi reports back is `session.resumed`); the run ends on Pi's `agent_settled` event rather than on process exit, after which standard input is closed and the process is given 5 seconds to exit. Every other Pi event goes through the same `PiJsonEventMapper`, so text, thinking, tool and usage messages are identical on both paths. A prompt that Pi rejects (`prompt` reply with `success: false`) or an exit before `agent_settled` ends the stream with a `terminal.failed` that keeps Pi's error text, exit code and standard error.
  The extension is an embedded resource of `HagiCode.Libs.Providers`, written once to `<temp>/hagicode-libs/pi-extensions/<content hash>/ask-user-question.ts` (temporary file, then move; an intact file is reused, so concurrent runs and upgrades agree) and needs no installation. It registers `ask_user_question` with the input schema of Claude's `AskUserQuestion` (`questions[]` with `question`, `header`, `multiSelect` and `options[]` of `label` / `description`). Pi's own dialogs cannot carry a header, option descriptions or a multi-select flag (`select` takes plain strings, `ctx.ui.custom()` is TUI only), so the whole question travels in one `input` dialog: the title is the wire marker `hagicode:ask_user_question:v1`, a newline and the JSON `{ "toolCallId": "...", "questions": [ ... ] }`; the answer is the JSON `{ "answers": { "<question text>": "<answer>" } }` in the dialog value, where a multi-select answer is one string with the values joined by a comma and a space and free text is passed through unchanged. A dialog with another marker version is not a question. When the dialog is cancelled, or the run is aborted (the extension hands Pi's abort signal to the dialog), the tool tells the agent that the user declined.
  The handler receives a `PiUserInputRequest` (the dialog `RequestId`, the agent's `ToolCallId` and the questions with header, `MultiSelect` and option labels and descriptions) and returns a `PiUserInputResponse` with the chosen labels or free text per question text. It runs in a background task, so the stream keeps flowing while a person thinks. Returning `null`, throwing, or `PiOptions.QuestionTimeout` (default 30 minutes, `Timeout.InfiniteTimeSpan` for no limit; any other non-positive value throws `ArgumentOutOfRangeException` before a process starts) passing answers the dialog as cancelled, the agent is told the user declined and the run goes on to its normal result. Every dialog gets one response. Dialogs of other extensions (`select`, `confirm`, `editor`, an `input` without the marker) are answered cancelled and never reach the handler; fire-and-forget UI requests (`notify`, `setStatus`, `setWidget`, `setTitle`, `set_editor_text`) are ignored. Pi has no tool timeout: an answer after more than 60 seconds worked end to end.
  The question is also yielded to the caller as an `extension_ui_request` stream message, in the position it arrived, and `PiUserInputRequest.TryFromCliMessage` reads it back; the call and result of the question tool are ordinary `tool.call` / `tool.update` / `tool.completed` messages, so a consumer that shows the question another way hides them (`PiUserInputRequest.QuestionToolName`). Callers that persist or parse every message of the stream must recognize the `extension_ui_request` type.
  Cancelling the call dismisses every open dialog, sends Pi an `abort`, closes standard input and, when Pi does not exit within the bound, stops the process; the call ends with `OperationCanceledException` and no Pi process outlives it. Pi refuses to start (exit code 1, no output) when a user extension registers `ask_user_question` as well; the provider recognizes that, logs a warning through its optional `ILogger<PiProvider>` and runs the request in JSON print mode without questions. The opt-in real-CLI tests (`HAGICODE_LIBS_REAL_CLI_PI_USER_QUESTION=1`, optionally `..._MODEL` and `..._EXECUTABLE`) ask real questions through a real model; the default suite covers the same paths with a scripted process and needs no Pi.

## OMP provider

`OmpProvider` runs `omp --mode json --print` and normalizes its JSONL stream into the shared `CliMessage` types. A request that sets `OmpOptions.UserInputHandler` runs in OMP's `rpc-ui` mode instead, so the agent can ask the user questions through OMP's built-in `ask` tool. Verified against omp 18.10.4.

- **Interactive questions (`OmpOptions.UserInputHandler`):** the handler is `null` by default and then nothing changes: the command line, the closed standard input and the completion on process exit are exactly what they were. When a handler is set (and `DisableAllTools` is off; with every tool disabled the handler is ignored and the print path runs) the provider starts `omp --mode rpc-ui` with the same model (omniroute selector handling included), system prompts, thinking, `--resume` / `--no-session`, tool and extra options, adds `ask` to a non-empty `AllowedTools` allowlist, and never passes `--no-ui`, `--print`, an extension or a positional prompt. Plain `--mode rpc` has no `ask` tool. Before the prompt it runs a sequential handshake: `{"type":"set_ask_dialog","enabled":true}` (OMP's structured ask dialog is off by default for every process), then `get_state`, whose `data.sessionId` is reported as the same `session.started` / `session.resumed` message the print path produces, and only then the RPC `prompt` command. OMP answers a run with `prompt_result` and ends it with `session_settled`; background work (an async `bash` job) can wake the agent after a `prompt_result` whose `sessionSettled` is false, so the provider keeps streaming until `session_settled`, then closes standard input and gives the process 5 seconds to exit. `prompt_result.status` decides the terminal message: `completed` gives the print path's completion, `error` a `terminal.failed` with `error.message` (and `provider`, `model`, `http_status`, `retryable`), `aborted` is described below. A prompt that OMP rejects, an exit before `session_settled` and output that is not JSON end the stream with a `terminal.failed` that keeps the diagnostic text. Every other event goes through the same `OmpJsonEventMapper`. `rpc-ui` attaches a message snapshot (`message`, `assistantMessageEvent.partial`) to `message_update` events that `--mode json` does not send, serialized after later deltas already reached it, so fed as it is the mapper would repeat text; the provider removes it from text and thinking events (they reach the mapper exactly as on the print path) and keeps only the `partial` of tool-call events, which gives the call its real id and tool name from its first `tool.call` (the print path's delta-only stream starts with a `tool_call` placeholder until `toolcall_end`). `ready`, `available_commands_update` and widget frames are ignored.
  An `ask` request (`extension_ui_request` with `method: "ask"`) becomes an `OmpUserInputRequest`: `RequestId` is the frame id (unique per `ask` call, stable for the run), and every question carries its OMP `Id`, text, `Header`, `MultiSelect` and option labels and descriptions; OMP's `preview`, `recommended` and the frame's `timeout` are not passed on. The handler returns an `OmpUserInputResponse` with the chosen labels or free text per question text, and the provider answers with one `{id, selectedOptions, customInput?}` per question, in request order. OMP rejects more than one option on a single-select question, an unknown label and a label selected twice (the `ask` tool then fails), so a single-select answer selects a label only when the text equals an offered label (case-sensitive), a multi-select answer only when the text splits on `", "` into distinct offered labels (the longest label is tried first, so a label may contain a comma), and anything else is sent as `customInput` with no selected option.
  The handler runs in a background task, so the stream keeps flowing while a person thinks. Returning `null`, leaving a question without an answer, throwing, or `OmpOptions.QuestionTimeout` (default 30 minutes, `Timeout.InfiniteTimeSpan` for no limit; any other non-positive value throws `ArgumentOutOfRangeException` before a process starts) passing answers the request `{"cancelled":true}`. **Unlike Claude and Pi, OMP then ends the run**: the tool reports `Ask tool was cancelled by the user` and `prompt_result.status` is `aborted`, which the provider reports as a normal `terminal.completed` with `stop_reason: "aborted"`, not as a failure; an aborted run the host did not cause (neither a cancelled answer nor a caller cancellation) is a `terminal.failed` saying OMP aborted the run. The `timedOut` marker of OMP's schema is never sent, because OMP answers a timed-out `ask` with the recommended (or first) options and lets the agent continue as if the user had chosen them. OMP has no ask timeout of its own (`ask.timeout` defaults to 0): an answer after more than 60 seconds worked end to end. Dialogs of extensions (`select`, `confirm`, `input`, `editor`) are answered cancelled and never reach the handler (they resolve to `undefined` / `false` and the run goes on); fire-and-forget requests (`notify`, `setStatus`, `setWidget`, `setTitle`, `set_editor_text`, `open_url`) are ignored. Every dialog gets at most one response.
  The question is also yielded to the caller as an `extension_ui_request` stream message, in the position it arrived, and `OmpUserInputRequest.TryFromCliMessage` reads it back; the call and result of the `ask` tool are ordinary `tool.call` / `tool.update` / `tool.completed` messages, so a consumer that shows the question another way hides them (`OmpUserInputRequest.QuestionToolName`). Cancelling the call sends OMP `abort` and nothing for an open question: OMP closes it with its own `{"method":"cancel","targetId":...}` frame (the provider cancels the handler's token and writes nothing), ends the run `aborted` and settles; the provider then closes standard input and stops the process when it does not exit within the bound, and the call ends with `OperationCanceledException`, so no OMP process outlives it. A build that answers `set_ask_dialog` with a failure (one without the command) gets no prompt: the provider logs a warning with the `omp --version` output through its optional `ILogger<OmpProvider>`, stops that process and runs the request in JSON print mode without questions. The opt-in real-CLI tests (`HAGICODE_LIBS_REAL_CLI_OMP_USER_QUESTION=1`, optionally `..._MODEL` and `..._EXECUTABLE`) ask real questions through a real model in an isolated home; the default suite covers the same paths with frames captured from omp 18.10.4 (`tests/HagiCode.Libs.Providers.Tests/Fixtures/Omp`) and needs no OMP.

## Reasonix provider

`ReasonixProvider` drives `reasonix acp` over the shared ACP session layer (`AcpSessionClient`). Verified against Reasonix v2.41.0.

- **Interactive questions (`ReasonixOptions.UserInputHandler`):** Reasonix's built-in `ask` tool is always available, and each of its questions arrives as its own ACP `session/request_permission` request: tool call id `ask-<hex>-<turn>-qN`, options `qN:1`, `qN:2`, … plus `qN:cancel` (`reject_once`), with the question text, the options (`Label` / `Description`, read case-insensitively) and the `multi` flag in `toolCall.rawInput` and the header in `toolCall.content`. The next question of the same `ask` call is sent only after the previous one is answered. The handler is `null` by default and then nothing changes: a question request is answered with its first option like any other permission request, no question message is produced, and a warning that names the question is logged through the optional `ILogger<ReasonixProvider>`. When a handler is set, the provider gives `AcpSessionClient` a permission hook (`AcpPermissionRequestHandler`, shared ACP core; the default answer applies when it returns `null`) that routes question requests, and only those, to the handler as a `ReasonixUserInputRequest` (`RequestId` = the `ask-…-qN` tool call id, one `Question` with `Id`, text, `Header`, `MultiSelect` as Reasonix reported it, and the options without `qN:cancel`); tool approvals (`gate-*`) are answered exactly as before.
  The handler returns a `ReasonixUserInputResponse` with the chosen label per question text. ACP carries one option id per answer, so the questions are **single-choice with no free text**: exactly one offered label (matched exactly and as a whole, so a label may contain a comma; with duplicate labels the first option wins) is sent as `selected qN:<i>`. A `null` response, an empty answer, text that is not an offered label, more than one value, a throwing handler and `ReasonixOptions.QuestionTimeout` (default 30 minutes, `Timeout.InfiniteTimeSpan` for no limit; any other non-positive value throws `ArgumentOutOfRangeException` before a process starts when a handler is set) passing decline the question with `selected qN:cancel`, or the `cancelled` outcome when the request offers no cancel option. Reasonix 2.41.0 does not continue after either: it fails the `ask` call with `ask: context canceled` and ends the turn with stop reason `error`. The provider records its own decline and reports such a turn (stop reason `error` or `cancelled`) as `terminal.completed` with `question_declined: true` and the original `stop_reason`; a turn that ends with `error` and no declined question is still `terminal.failed`, and a turn that continues after a decline ends with its own result. Reasonix accepted an answer after 120 seconds. Cancelling the call cancels the handler token, answers a pending question `cancelled` while the transport is still open, records no decline and ends the Reasonix process; the call ends with `OperationCanceledException`.
  Each routed question is also yielded to the caller as a `question.request` stream message (`ReasonixUserInputRequest.MessageType`) after the `tool.call` of the `ask` call and in the position it arrived; `ReasonixUserInputRequest.TryFromCliMessage` reads it back from its `params`. The `ask` call itself stays an ordinary `tool.call` / `tool.update` pair whose `toolu_…` id differs from the question's `RequestId`, so a consumer that shows the question another way hides it by the tool title `ask` (`ReasonixUserInputRequest.QuestionToolName`). The shared `AcpJsonRpcClient` now serializes its transport writes, so a delayed answer cannot interleave with another request, and a hook that is still waiting when the client is disposed is cancelled without an error reply. The default suite replays transcripts recorded from Reasonix 2.41.0 (`tests/HagiCode.Libs.Providers.Tests/Fixtures/Reasonix/v2.41.0/ask-*.jsonl`) and needs no Reasonix; `ReasonixProviderRealCliUserQuestionTests` asks real questions when `HAGICODE_REAL_CLI_TESTS=1` (optionally `HAGICODE_REAL_CLI_REASONIX_MODEL`).

## Shared pooling

Built-in providers now participate in a shared pooling architecture:

- ACP providers (`CodeBuddy`, `Gemini`, `Hermes`, `Kimi`, `Kiro`, `QoderCLI`) lease warm ACP sessions from the shared `CliProviderPoolCoordinator`.
- `Claude Code` keeps native `SessionId` / `Resume` continuity but always starts a fresh local CLI transport for each invocation and disposes it after the terminal message. A `result` is terminal only once no background subagent is still running and every started turn has produced its `result`; the turn that launches a background subagent ends with its own `result`, and the subagent's output plus the follow-up turn keep streaming afterwards. A subagent can also finish its turn while a background command or monitor it started (a task with a `parent_task_id`) is still running; the CLI resumes that subagent when the task ends, so the run stays open for it, bounded by `ClaudeCodeOptions.SubagentBackgroundWorkTimeout` (default 20 minutes, `Timeout.InfiniteTimeSpan` for no limit). Background work the main agent armed with a declared timeout is awaited too: a `Monitor` (`timeout_ms`) or a `Bash` command run in the background with an explicit `timeout`. The agent ends its turn to wait for their events or exit, and closing the stream would kill them, so the run stays open until the task's own timeout plus `ClaudeCodeOptions.BackgroundWorkGracePeriod` (default 1 minute) has passed, which the CLI enforces by ending the task. Both appear as plain `local_bash` tasks and are recognised by their tool call. A background `Bash` command with no `timeout` is awaited as well, because an agent that starts a build this way and ends its turn to wait for it would otherwise lose the build and its notification when the stream closes; the CLI has no limit for such a command, so the bound is the host's, `ClaudeCodeOptions.UntimedBackgroundCommandTimeout` (default 30 minutes, counted from the command's start, without the grace period; `Timeout.InfiniteTimeSpan` for no limit, `TimeSpan.Zero` to never await it). A command meant to run for good, such as a dev server, therefore keeps the run open until that limit passes or the run is stopped. A subagent or background command that finishes while the main agent is still writing its final answer is reported mid-turn, but the CLI can only hand it to the agent by starting another turn right after the `result` (it attaches notifications to the request after a tool result, and there is none left); the run therefore waits for that follow-up turn to start, bounded by `ClaudeCodeOptions.NotificationTurnTimeout` (default 10 seconds), instead of closing the stream first. A notification that was already handed over (a tool result of the main agent follows it, or the CLI replayed it with `origin.kind` `task-notification`) needs no follow-up turn.
  While a run waits, `ClaudeCodeProvider` also yields synthetic `agent_work_snapshot` messages (`HagiCode.Libs.Core.AgentWork.AgentWorkSnapshot`, read back with `AgentWorkSnapshot.TryFromCliMessage`) that tell the caller what the host is waiting on. Each snapshot is the complete item list: a `subagent` for every `local_agent` task, and a `monitor` for a `Monitor`, a timed background `Bash` command, or any task a subagent owns (nested under its subagent through `parentId`). An item carries its title, a truncated command, its start and `deadlineAt`, the subagent's final summary, and `blocksCompletion`, which is true exactly while the completion tracker keeps the run open for it. A snapshot is placed right after the CLI message that changed it, at most one per message and only on change; it holds no raw CLI JSON and never alters when the run completes. Status events for a task that never started in the run (a resumed session replays stopped notifications of its old tasks) create no item. Callers that persist or parse every message of the stream must recognize the type and skip it.
  `ClaudeCodeOptions.UserInputHandler` lets the agent ask the user questions during a run. It is `null` by default and then nothing changes. When it is set, the provider adds `--permission-prompt-tool stdio` to the CLI arguments, which makes the CLI register `AskUserQuestion` and send its permission prompts to the host as `control_request` messages. An `AskUserQuestion` request (`ClaudeUserInputRequest`: request and tool-use ids, questions with header, options and `multiSelect`, and the original input) is passed to the handler in a background task, so the stream keeps flowing while the question is pending. The handler returns a `ClaudeUserInputResponse` with the chosen option labels or free text for each question text; the provider writes `control_response` `allow` with `updatedInput` equal to the original input plus an `answers` object, and joins the values of a multi-select question with a comma and a space. Returning `null`, a handler that throws, `ClaudeCodeOptions.QuestionTimeout` (default 30 minutes, `Timeout.InfiniteTimeSpan` for no limit) passing, or a caller cancellation answer `deny` with a message, so the CLI reports the call under `permission_denials` and the run still reaches its normal result (a cancellation ends the stream with `OperationCanceledException` as before, after a best-effort deny). While a question is pending the other wait bounds (`PromptStartTimeout`, `SubagentBackgroundWorkTimeout`, the monitor deadline plus `BackgroundWorkGracePeriod`) do not end the run; they apply again once it is resolved. Every other `can_use_tool` request is denied at once with a fixed message and never reaches the handler, which is what the CLI does today for a tool its permission mode does not approve. The CLI echoes the host's own `control_response` on stdout; the provider drops that echo. The request is identified by the tool name because CLI 2.1.159 does not send `requires_user_interaction`, which 2.1.292 does. The interactive request is also yielded to the caller as a `control_request` stream message, in the position it arrived and after the output that preceded it, and `ClaudeUserInputRequest.TryFromCliMessage` reads it back. Like `agent_work_snapshot`, callers that persist or parse every message of the stream must recognize the type and skip or handle it; requests the provider answers itself are not passed on.
- `Codex` runs each request in a fresh local CLI process. Resume continuity depends on an explicit `ThreadId`; there is no Codex-specific runtime pooling.
- `Copilot` does not participate in the shared runtime pool. Each invocation creates a fresh SDK runtime, and continuity depends only on provider-native `SessionId` resume semantics.

Providers that actually participate in pooling expose `PoolSettings` so callers can disable pooling or tune provider-level behavior. `ClaudeCodeOptions` intentionally does not expose provider-level pool controls anymore:

```csharp
var options = new CodebuddyOptions
{
    SessionId = "demo-session",
    WorkingDirectory = "/path/to/repo",
    PoolSettings = new CliPoolSettings
    {
        Enabled = true,
        IdleTimeout = TimeSpan.FromMinutes(15),
        MaxActiveSessions = 50,
        KeepAnonymousSessions = false
    }
};
```

Operational notes:

- Warm reuse only occurs when the logical session key and compatibility fingerprint still match.
- Each pooled entry executes one prompt at a time through an execution lock.
- Idle entries are reaped before capacity failures are reported; if a provider is still full after TTL cleanup, the oldest idle entry is evicted next.
- Faulted transports and broken ACP sessions are removed immediately instead of being returned to the pool.
- `CliAcpSessionPool.GetDiagnosticsSnapshot()` now reports global plus provider-scoped hit/miss/evict/fault counters, along with the latest eviction/fault reason; the pool also emits structured logs and `System.Diagnostics.Metrics` counters for monitoring hooks.
- Default ACP idle TTL baselines remain provider-specific: `codebuddy/deepagents/gemini/kimi/kiro/qodercli=10m` and `hermes=24h`. `claude-code`, `codex`, `copilot`, and `opencode` do not register shared local-runtime TTL entries.
- Hosts can enable `CliProcessOwnershipOptions` to persist managed subprocess PID ownership. `hagicode-core` now defaults this to `DataDir/cli-owned-processes.json` and reaps matching orphaned CLI processes during startup recovery.

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

Set `SessionId` when you want provider-native Copilot resume semantics. The provider first attempts SDK resume for that id, falls back to creating a new session pinned to the requested id when nothing persisted yet, and emits `session.started` or `session.resumed` messages accordingly. Requests without `SessionId` stay anonymous and still create a fresh SDK runtime on every attempt. Legacy `PoolSettings` values are ignored for compatibility.

Codex execution options cover the common CLI settings without forcing raw command lines:

```csharp
var options = new CodexSessionOptions
{
    ThreadOptions = new ThreadOptions
    {
        Model = "gpt-5-codex",
        SandboxMode = SandboxMode.WorkspaceWrite,
        ApprovalPolicy = ApprovalMode.Never,
        WorkingDirectory = "/path/to/repo",
        AdditionalDirectories = ["/path/to/repo"],
        SkipGitRepoCheck = true,
    }
};

await using var codexSession = await codex.CreateSessionAsync(options);
var result = await codexSession.Thread.RunAsync("Reply with exactly the word 'pong'");
Console.WriteLine(result.FinalResponse);
```

For Codex sessions, pass a stable `CodexSessionOptions.ThreadId` when you want to resume a previous conversation. Requests without `ThreadId` stay anonymous and always start a fresh local CLI process.

Hermes execution options cover the managed `hermes acp` bootstrap path without forcing raw process wiring. `SessionId` is treated as an in-memory conversation key for the current provider instance, rather than a cross-process resume token:

```csharp
var options = new HermesOptions
{
    Model = "hermes/default",
    WorkingDirectory = "/path/to/repo",
    SessionId = "hermes-session-123",
    ModeId = "bypassPermissions",
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

Two GitHub Actions workflows cover CLI discovery. They are kept separate on purpose: the first checks invocation errors, the second checks discovery.

| Workflow | What it runs | Lanes |
|----------|--------------|-------|
| `.github/workflows/cli-discovery-cross-platform.yml` | `TestCategory=RealCliInvocationContract`, the invocation error contracts of the installed CLIs. It installs with `CiSetup.Console --install` (npm). | 4 (Linux, macOS, Windows, and a Windows zh-CN lane) |
| `.github/workflows/agent-cli-discovery-integration.yml` | Installs each CLI through one install strategy, checks discovery with the install directory on `PATH` and, when the strategy has a well-known location, with it removed, then runs `TestCategory=RealCliDiscovery`. Runs on path-filtered push and pull request, on `workflow_dispatch`, and weekly. | 14 |

### CLI install registry

All CLI metadata is centralized in `HagiCode.Libs.Core.Discovery.CliInstallRegistry`. Each `CliInstallDescriptor` records the provider name, npm package, pinned version, executable candidates, and whether the CLI is publicly installable. Descriptors may also declare version argument sets and install strategies. This registry is the single source of truth: CI install and verify, discovery, and this table all read it. A repository test (`CliInstallRegistryTests`) fails when a package or pinned version in this table differs from the registry.

| Provider | npm Package | Pinned Version | CI-Covered |
|----------|-------------|----------------|------------|
| Claude Code | `@anthropic-ai/claude-code` | 2.1.168 | Yes |
| Copilot | `@github/copilot` | 1.0.60 | Yes |
| Codex | `@openai/codex` | 0.115.0 | Yes |
| CodeBuddy | (private) | — | No |
| Gemini | (local / explicit validation) | — | No |
| Hermes | (private) | — | No |
| Kimi | (local / explicit validation) | — | No |
| Pi | `@earendil-works/pi-coding-agent` (local / explicit validation, Node >=22.19.0) | — | No |
| QoderCLI | (private) | — | No |

### CI-covered vs. locally-installed CLIs

CLIs with `IsPubliclyInstallable = true` are automatically installed and tested in CI. Today that public set is Claude Code, Copilot, and Codex. CLIs marked as not publicly installable (CodeBuddy, Gemini, Hermes, Kimi, Pi, QoderCLI) are skipped during CI setup and require local installation for validation.

### Install strategies (Claude Code)

Discovery reads each strategy's operating systems, well-known directories, and pin support from the registry. `~` expands to `HOME` on Unix and to `USERPROFILE` on Windows. `%NAME%` expands from the supplied environment. A directory whose variable is missing is skipped.

| Strategy (`--strategy`) | Operating systems | Well-known directories | Can pin a version | Lane status |
|-------------------------|-------------------|------------------------|-------------------|-------------|
| `npm` | Linux, macOS, Windows | `%APPDATA%\npm` (Windows only; Unix finds npm's bin on `PATH`) | Yes (`@<version>`) | Blocking |
| `native` (`NativeInstaller`) | Linux, macOS, Windows | `~/.local/bin` (the launcher may be a symlink into `~/.local/share/claude/versions`) | Yes (`bash -s <version>`; PowerShell scriptblock argument) | Non-blocking until green |
| `homebrew` (`Homebrew`) | macOS | `/opt/homebrew/bin`, `/usr/local/bin` | No (cask `claude-code`) | Non-blocking |
| `winget` (`WinGet`) | Windows | `%LOCALAPPDATA%\Microsoft\WinGet\Links` | Yes (`--version`) | Non-blocking |
| legacy (`LegacyLocal`) | Linux, macOS, Windows | `~/.claude/local` | No | Not in CI (fake-directory unit tests only) |

Copilot and Codex declare an `npm` strategy and their pinned version. Their lanes are blocking.

Discovery also reports the install kind (`Npm`, `NativeInstaller`, `Homebrew`, `WinGet`, `LegacyLocal`, or `Unknown`). This is best-effort attribution from the strategy directories, the symlink target, and a `node_modules/<package>` directory. It is reported separately from the source (`Configured`, `AgentCliPath`, `Path`, or `WellKnownLocation`).

### Lane status and promotion

A lane is blocking only after its installer has passed on every operating system it targets. The blocking lanes today are the `npm` lanes for Claude Code, Copilot, and Codex on all three operating systems. The native, Homebrew, and WinGet lanes are non-blocking. Their workflow jobs carry `continue-on-error`, so a failure still appears in that lane's status and logs. Each lane's status and the installer contracts it depends on are recorded in `docs/agent-cli-discovery-guide.md`. Promote a lane by changing `blocking` in the workflow matrix and updating that document in the same change.

### Local reproduction

Run these from the `repos/Hagicode.Libs` directory.

```bash
# Install all publicly available CLIs through npm (the default), then check that they are on PATH
dotnet run --project src/HagiCode.Libs.CiSetup.Console -- --install --verify

# Install one provider through one strategy. npm is the default strategy.
dotnet run --project src/HagiCode.Libs.CiSetup.Console -- --install --provider ClaudeCode --strategy native

# Verify discovery: prints the path, source, install kind, and version; exits 3 (not found), 4 (version unavailable), 5 (version mismatch), or 6 (install kind mismatch)
dotnet run --project src/HagiCode.Libs.CiSetup.Console -- --verify-discovery --provider ClaudeCode --strategy native --expect-install-kind NativeInstaller

# Verify that the well-known location alone resolves the CLI (removes the strategy directory from PATH)
dotnet run --project src/HagiCode.Libs.CiSetup.Console -- --verify-discovery --provider ClaudeCode --strategy native --without-install-dir-on-path

# Run the real-install discovery tests. Without HAGICODE_REAL_CLI_TESTS=1 they skip and start no CLI.
HAGICODE_REAL_CLI_TESTS=1 HAGICODE_REAL_CLI_PROVIDER=ClaudeCode HAGICODE_REAL_CLI_STRATEGY=native dotnet test tests/HagiCode.Libs.Core.Tests/HagiCode.Libs.Core.Tests.csproj --filter "TestCategory=RealCliDiscovery"

# Run the provider-level real-CLI validation (Claude Code + Copilot + Codex)
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

**Dsh** (requires an installed and authenticated `dsh` whose `DSH_HOME` can serve Haiku 5.5):
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~Dsh"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Dsh"
dotnet run --project src/HagiCode.Libs.Dsh.Console -- --test-provider-full --provider omniroute --model claude/claude-haiku-5-5 --workspace .
```
The real-run tests deliberately use Haiku 5.5 to keep token use low. `claude/claude-haiku-5-5` is the shared test model, and `omniroute` is the dsh provider that owns it in the local OmniRoute setup. Confirm both values appear in your authenticated `DSH_HOME` model list before running these commands; the provider and model are sent as dsh's `["provider","model"]` selector, so a mismatch is rejected by dsh. The real-run gate is opt-in and never runs by default.

**Junie** (requires local installation and any required local auth bootstrap):
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~Junie"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Junie"
dotnet run --project src/HagiCode.Libs.Junie.Console -- --test-provider-full --repo .
```

**Kimi** (requires local installation and any required local auth bootstrap):
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~Kimi"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Kimi"
dotnet run --project src/HagiCode.Libs.Kimi.Console -- --test-provider-full --repo .
```

**Pi** (requires local installation):
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~PiProviderTests"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Pi"
HAGICODE_LIBS_REAL_CLI_PI_USER_QUESTION=1 HAGICODE_LIBS_REAL_CLI_PI_USER_QUESTION_MODEL=<cheap model selector> dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~PiProviderRealCliUserQuestionTests"
dotnet run --project src/HagiCode.Libs.Pi.Console -- --test-provider pi-cli
dotnet run --project src/HagiCode.Libs.Pi.Console -- --test-provider-full --provider omniroute --model glm/glm-4.7 --repo .
```

**QoderCLI** (requires local installation):
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~QoderCli"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~QoderCli"
dotnet run --project src/HagiCode.Libs.QoderCli.Console -- --test-provider-full --repo .
```

**Reasonix** (requires local installation; targets the 2.x ACP line, verified with `v2.41.0`; `ReasonixConsoleIntegrationTests` and the resume, unknown-model and `enableYolo` real-CLI tests need `HAGICODE_REAL_CLI_TESTS=1` and `HAGICODE_REAL_CLI_REASONIX_MODEL=<provider>/<model>`; `ReasonixProviderRealCliUserQuestionTests` needs only `HAGICODE_REAL_CLI_TESTS=1` and uses the model ref when it is set):
```bash
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.Providers.Tests/HagiCode.Libs.Providers.Tests.csproj --filter "FullyQualifiedName~Reasonix"
HAGICODE_REAL_CLI_TESTS=1 dotnet test tests/HagiCode.Libs.ConsoleTesting.Tests/HagiCode.Libs.ConsoleTesting.Tests.csproj --filter "FullyQualifiedName~Reasonix"
dotnet run --project src/HagiCode.Libs.Reasonix.Console -- --test-provider-full --repo .
```

## Design goals

- Zero heavy framework dependencies in the reusable libraries.
- Cross-platform CLI process management for Windows, macOS, and Linux.
- Stream-oriented provider integrations that can power higher-level HagiCode tools.
- Simple provider registration for DI and non-DI consumers.