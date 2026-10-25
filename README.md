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
- `src/HagiCode.Libs.Providers` - provider abstractions, the Claude Code/Copilot/Codex/DeepAgents/Dsh/CodeBuddy/Gemini/Hermes/Junie/Kimi/Kiro/OpenCode/Omp/Pi/QoderCLI/Reasonix providers, and optional DI registration.
- `src/HagiCode.Libs.Skills` - skills-oriented infrastructure. Its first shipped capability is a typed online API client for search, well-known discovery, audit, telemetry, and GitHub metadata/tree requests.
- `src/HagiCode.Libs.Imaging` - in-process AVIF conversion (PNG, JPEG, WebP and GIF first frame to AVIF) with bundled libvips native libraries. No `ffmpeg`, `avifenc` or other tool is used or looked up. Tests: `tests/HagiCode.Libs.Imaging.Tests`. Backend integration guide: `docs/avif-backend-integration-guide.md`.
- `src/HagiCode.Libs.Git` - typed Git client (`IGitClient`) for repository discovery and state inspection. Backend integration guide: `docs/git-backend-integration-guide.md`.
- `tests/*` - NUnit coverage for each project. Shared test settings, the NUnit and Shouldly packages and the global usings live in `tests/Directory.Build.props`; each test project lists only its own references.

## Getting started

```bash
dotnet build HagiCode.Libs.slnx
dotnet test HagiCode.Libs.slnx
dotnet test tests/HagiCode.Libs.Imaging.Tests/HagiCode.Libs.Imaging.Tests.csproj  # AVIF suite; native tests fail on supported platforms when the runtime cannot load
```

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

`ProviderRegistry` also exposes compatibility aliases so tests and higher-level factories share one naming surface:

- `claude-code` -> `claude`, `claudecode`, `anthropic-claude`
- `codebuddy` -> `codebuddy-cli`
- `hermes` -> `hermes-cli`
- `opencode` -> `open-code`, `opencode-cli`

## OpenCode install metadata

`CliInstallRegistry` now tracks OpenCode as a publicly installable CLI descriptor:

- npm package: `opencode-ai@1.3.3` (predates the OpenCode 2.0 runtime; see "OpenCode 2.0 runtime compatibility")
- executable candidates: `opencode`
- public installability: `true`

The descriptor is aligned with the official OpenCode install guidance (`npm i -g opencode-ai@latest`) plus the pinned npm version snapshot resolved for this repository on `2026-03-28`.

## OpenCode 2.0 runtime compatibility

`HagiCode.Libs.Providers` supports **OpenCode 2.0** (verified with `v2.0.32`). OpenCode 1.x is no longer supported: its routes answer with the Web UI HTML shell and HTTP 200, which the provider reports as an unsupported API version instead of a JSON error.

- **Owned runtime.** The provider launches `opencode serve`. OpenCode 2.0 prints `server listening on <url>` and `server password <value>` at startup; the provider accepts both lines, captures the password, and sends it as HTTP Basic credentials (username `opencode`) on every `/api/*` request. No setting is needed.
- **Attach mode.** When `OpenCodeOptions.BaseUrl` points at an externally managed runtime that requires a password, set `OpenCodeOptions.ServerPassword`. Without it, the health probe reports `authentication failed` in the `health_probe` stage.
- **Password handling.** The password is never returned in `CapturedOutput`, lifecycle `DiagnosticOutput`, exception messages, or the runtime key (only a SHA-256 digest of an attach password takes part in the key). `OpenCodeOptions` and `OpenCodeStandaloneServerOptions` do not print it in `ToString()`.
- **Prompts.** OpenCode 2.0 accepts the prompt as a queued inbox item. The assistant reply is assembled from `GET /api/event`: a turn completes on `session.execution.succeeded`, fails on `session.execution.failed`, and its text comes from `session.text.ended`. The model is set on the session through `POST /api/session/{id}/model`, not on the prompt.
- **Inline files.** `OpenCodeSessionPromptRequest.FromTextWithFiles(text, files)` adds a `files` array to the prompt body, each entry `{ "mime", "uri", "name" }` with `uri` a `data:<mime>;base64,…` URL (`OpenCodePromptFile`). A request without files omits `files`. OpenCode 2.0 rejects a bare string entry, a `url` key and a non-data URI with HTTP 400. `ToString()` prints lengths, counts and names only, never the data URL.
- **Model input capabilities.** `OpenCodeHttpClient.ListModelsAsync()` calls `GET /api/model` and returns `OpenCodeModelInfo` entries (`ProviderId`, `Id`, `InputCapabilities` from `capabilities.input`). `AcceptsImage` is true when `image` is listed. A model without `capabilities` has no input capabilities. A failing listing throws only to its caller.

> Note: the `CliInstallRegistry` descriptor still pins `opencode-ai@1.3.3` (see "OpenCode install metadata" above). That version is not compatible with this runtime and needs to move to an OpenCode 2.0 release before the install path can be used with it.

## Claude Code thinking display

`ClaudeCodeOptions.ThinkingDisplay` is forwarded to the CLI as `--thinking-display <mode>`. Supported modes are `summarized`, `omitted`, and `highlights` (case-insensitive, surrounding whitespace ignored); any other value throws `ArgumentException` before a process starts. When unset or empty no flag is sent, so the CLI and model default applies. Current Claude models then stream thinking blocks with empty text, and `summarized` is what makes the text come back. Display only changes visibility; summaries cost the same tokens as omitted thinking.

`--thinking-display` is hidden from `claude --help`. If a CLI exits at launch with an unknown-option error for exactly that flag, before any stream message, the provider starts one replacement process with the same arguments minus the flag, and remembers the executable as unsupported until the host process restarts. Any other failure, and any failure after the first message, is surfaced once with no retry. The `--settings` argument is never inspected or rewritten: `showThinkingSummaries` is ignored by the CLI in `--print` / stream-json mode.

Verified with Claude Code 2.1.292 and 2.1.295: `--thinking-display summarized` returns thinking text and omitting the flag returns empty thinking text. Older builds are covered only by the fake-CLI fallback tests.

## Pi provider

`PiProvider` runs `pi --mode json --print` and normalizes its JSONL stream into the shared `CliMessage` types. A request that sets `PiOptions.UserInputHandler` runs in Pi's RPC mode instead, so the agent can ask the user questions (see **Interactive questions** below).

- **Package and runtime:** the npm package is `@earendil-works/pi-coding-agent`, which requires Node `>=22.19.0`. The `CliInstallRegistry` entry records the package name but stays local-only (`IsPubliclyInstallable = false`, no pinned version).
- **Thinking levels:** `PiOptions.Thinking` accepts `off`, `minimal`, `low`, `medium`, `high`, `xhigh` and `max` (case-insensitive) and forwards the value as `--thinking`. Any other value throws `ArgumentException` before a process starts, and the message lists the valid values.
- **A provider needs a model:** setting `PiOptions.Provider` without `PiOptions.Model` (empty or whitespace counts as none) is a configuration error for every provider name, `omniroute` included. No process is started and the stream carries a single `terminal.failed` that names the provider, because Pi 1.x would exit with `--provider requires --model`.
- **Streaming formats:** Pi 0.84 and later emit `message_update` events that carry only the new fragment; earlier releases (0.78.x) carry a full `message` / `partial` snapshot. The provider recognizes the format per event, with no version probing. Incremental `assistant` and `assistant.thought` messages carry `delta: true` and contain only the new fragment, so a consumer appends them as they are: a whitespace-only fragment, or one equal to earlier text such as `The`, is real content. Messages from the snapshot format carry no `delta` marker and are still reconciled against earlier text. Tool input arrives as `tool.call` when Pi announces the call and as `tool.update` with the complete arguments; partial argument JSON is never forwarded.
- **New or resumed session:** the `session` header's `timestamp` is compared with the time the process was started. A header created by this run gives `session.started` with `resumeMode: "started"`, an older one gives `session.resumed`, and an id different from the requested one gives `restarted`. When the timestamp is missing or unreadable the provider falls back to comparing the returned id with the requested one.
- **Environment variables:** nothing is set by default. To avoid network activity at startup pass `PI_OFFLINE=1`, `PI_SKIP_VERSION_CHECK=1` and `PI_TELEMETRY=0` through `PiOptions.EnvironmentVariables`.
- **Interactive questions (`PiOptions.UserInputHandler`):** the handler is `null` by default and then nothing changes: the command line, the closed standard input, the completion on process exit and the absence of any extension are exactly what they were. JSON print mode has no UI, so a Pi that can ask must run in RPC mode. When a handler is set (and `DisableAllTools` is off; with every tool disabled the handler is ignored and the print path runs) the provider starts `pi --mode rpc` with the same model, prompt, session, thinking and tool options, adds the bundled `ask_user_question` extension with `-e` (and adds the tool to a non-empty `AllowedTools` allowlist), and sends the prompt as an RPC `prompt` command instead of an argument. Because RPC output has no session header, the session id is read with `get_state` and reported as the same `session.started` / `session.resumed` message the print path produces (RPC cannot tell a session it just created under a requested id from an existing one, so a requested id that Pi reports back is `session.resumed`); the run ends on Pi's `agent_settled` event rather than on process exit, after which standard input is closed and the process is given 5 seconds to exit. Every other Pi event goes through the same `PiJsonEventMapper`, so text, thinking, tool and usage messages are identical on both paths. A prompt that Pi rejects (`prompt` reply with `success: false`) or an exit before `agent_settled` ends the stream with a `terminal.failed` that keeps Pi's error text, exit code and standard error.
  The extension is an embedded resource of `HagiCode.Libs.Providers`, written once to `<temp>/hagicode-libs/pi-extensions/<content hash>/ask-user-question.ts` (temporary file, then move; an intact file is reused, so concurrent runs and upgrades agree) and needs no installation. It registers `ask_user_question` with the input schema of Claude's `AskUserQuestion` (`questions[]` with `question`, `header`, `multiSelect` and `options[]` of `label` / `description`). Pi's own dialogs cannot carry a header, option descriptions or a multi-select flag (`select` takes plain strings, `ctx.ui.custom()` is TUI only), so the whole question travels in one `input` dialog: the title is the wire marker `hagicode:ask_user_question:v1`, a newline and the JSON `{ "toolCallId": "...", "questions": [ ... ] }`; the answer is the JSON `{ "answers": { "<question text>": "<answer>" } }` in the dialog value, where a multi-select answer is one string with the values joined by a comma and a space and free text is passed through unchanged. A dialog with another marker version is not a question. When the dialog is cancelled, or the run is aborted (the extension hands Pi's abort signal to the dialog), the tool tells the agent that the user declined.
  The handler receives a `PiUserInputRequest` (the dialog `RequestId`, the agent's `ToolCallId` and the questions with header, `MultiSelect` and option labels and descriptions) and returns a `PiUserInputResponse` with the chosen labels or free text per question text. It runs in a background task, so the stream keeps flowing while a person thinks. Returning `null`, throwing, or `PiOptions.QuestionTimeout` (default 30 minutes, `Timeout.InfiniteTimeSpan` for no limit; any other non-positive value throws `ArgumentOutOfRangeException` before a process starts) passing answers the dialog as cancelled, the agent is told the user declined and the run goes on to its normal result. Every dialog gets one response. Dialogs of other extensions (`select`, `confirm`, `editor`, an `input` without the marker) are answered cancelled and never reach the handler; fire-and-forget UI requests (`notify`, `setStatus`, `setWidget`, `setTitle`, `set_editor_text`) are ignored. Pi has no tool timeout: an answer after more than 60 seconds worked end to end.
  The question is also yielded to the caller as an `extension_ui_request` stream message, in the position it arrived, and `PiUserInputRequest.TryFromCliMessage` reads it back; the call and result of the question tool are ordinary `tool.call` / `tool.update` / `tool.completed` messages, so a consumer that shows the question another way hides them (`PiUserInputRequest.QuestionToolName`). Callers that persist or parse every message of the stream must recognize the `extension_ui_request` type.
  Cancelling the call dismisses every open dialog, sends Pi an `abort`, closes standard input and, when Pi does not exit within the bound, stops the process; the call ends with `OperationCanceledException` and no Pi process outlives it. Pi refuses to start (exit code 1, no output) when a user extension registers `ask_user_question` as well; the provider recognizes that, logs a warning through its optional `ILogger<PiProvider>` and runs the request in JSON print mode without questions.

## OMP provider

`OmpProvider` runs `omp --mode json --print` and normalizes its JSONL stream into the shared `CliMessage` types. A request that sets `OmpOptions.UserInputHandler` runs in OMP's `rpc-ui` mode instead, so the agent can ask the user questions through OMP's built-in `ask` tool. Verified against omp 18.10.4.

- **Interactive questions (`OmpOptions.UserInputHandler`):** the handler is `null` by default and then nothing changes: the command line, the closed standard input and the completion on process exit are exactly what they were. When a handler is set (and `DisableAllTools` is off; with every tool disabled the handler is ignored and the print path runs) the provider starts `omp --mode rpc-ui` with the same model (omniroute selector handling included), system prompts, thinking, `--resume` / `--no-session`, tool and extra options, adds `ask` to a non-empty `AllowedTools` allowlist, and never passes `--no-ui`, `--print`, an extension or a positional prompt. Plain `--mode rpc` has no `ask` tool. Before the prompt it runs a sequential handshake: `{"type":"set_ask_dialog","enabled":true}` (OMP's structured ask dialog is off by default for every process), then `get_state`, whose `data.sessionId` is reported as the same `session.started` / `session.resumed` message the print path produces, and only then the RPC `prompt` command. OMP answers a run with `prompt_result` and ends it with `session_settled`; background work (an async `bash` job) can wake the agent after a `prompt_result` whose `sessionSettled` is false, so the provider keeps streaming until `session_settled`, then closes standard input and gives the process 5 seconds to exit. `prompt_result.status` decides the terminal message: `completed` gives the print path's completion, `error` a `terminal.failed` with `error.message` (and `provider`, `model`, `http_status`, `retryable`), `aborted` is described below. A prompt that OMP rejects, an exit before `session_settled` and output that is not JSON end the stream with a `terminal.failed` that keeps the diagnostic text. Every other event goes through the same `OmpJsonEventMapper`. `rpc-ui` attaches a message snapshot (`message`, `assistantMessageEvent.partial`) to `message_update` events that `--mode json` does not send, serialized after later deltas already reached it, so fed as it is the mapper would repeat text; the provider removes it from text and thinking events (they reach the mapper exactly as on the print path) and keeps only the `partial` of tool-call events, which gives the call its real id and tool name from its first `tool.call` (the print path's delta-only stream starts with a `tool_call` placeholder until `toolcall_end`). `ready`, `available_commands_update` and widget frames are ignored.
  An `ask` request (`extension_ui_request` with `method: "ask"`) becomes an `OmpUserInputRequest`: `RequestId` is the frame id (unique per `ask` call, stable for the run), and every question carries its OMP `Id`, text, `Header`, `MultiSelect` and option labels and descriptions; OMP's `preview`, `recommended` and the frame's `timeout` are not passed on. The handler returns an `OmpUserInputResponse` with the chosen labels or free text per question text, and the provider answers with one `{id, selectedOptions, customInput?}` per question, in request order. OMP rejects more than one option on a single-select question, an unknown label and a label selected twice (the `ask` tool then fails), so a single-select answer selects a label only when the text equals an offered label (case-sensitive), a multi-select answer only when the text splits on `", "` into distinct offered labels (the longest label is tried first, so a label may contain a comma), and anything else is sent as `customInput` with no selected option.
  The handler runs in a background task, so the stream keeps flowing while a person thinks. Returning `null`, leaving a question without an answer, throwing, or `OmpOptions.QuestionTimeout` (default 30 minutes, `Timeout.InfiniteTimeSpan` for no limit; any other non-positive value throws `ArgumentOutOfRangeException` before a process starts) passing answers the request `{"cancelled":true}`. **Unlike Claude and Pi, OMP then ends the run**: the tool reports `Ask tool was cancelled by the user` and `prompt_result.status` is `aborted`, which the provider reports as a normal `terminal.completed` with `stop_reason: "aborted"`, not as a failure; an aborted run the host did not cause (neither a cancelled answer nor a caller cancellation) is a `terminal.failed` saying OMP aborted the run. The `timedOut` marker of OMP's schema is never sent, because OMP answers a timed-out `ask` with the recommended (or first) options and lets the agent continue as if the user had chosen them. OMP has no ask timeout of its own (`ask.timeout` defaults to 0): an answer after more than 60 seconds worked end to end. Dialogs of extensions (`select`, `confirm`, `input`, `editor`) are answered cancelled and never reach the handler (they resolve to `undefined` / `false` and the run goes on); fire-and-forget requests (`notify`, `setStatus`, `setWidget`, `setTitle`, `set_editor_text`, `open_url`) are ignored. Every dialog gets at most one response.
  The question is also yielded to the caller as an `extension_ui_request` stream message, in the position it arrived, and `OmpUserInputRequest.TryFromCliMessage` reads it back; the call and result of the `ask` tool are ordinary `tool.call` / `tool.update` / `tool.completed` messages, so a consumer that shows the question another way hides them (`OmpUserInputRequest.QuestionToolName`). Cancelling the call sends OMP `abort` and nothing for an open question: OMP closes it with its own `{"method":"cancel","targetId":...}` frame (the provider cancels the handler's token and writes nothing), ends the run `aborted` and settles; the provider then closes standard input and stops the process when it does not exit within the bound, and the call ends with `OperationCanceledException`, so no OMP process outlives it. A build that answers `set_ask_dialog` with a failure (one without the command) gets no prompt: the provider logs a warning with the `omp --version` output through its optional `ILogger<OmpProvider>`, stops that process and runs the request in JSON print mode without questions.

## Reasonix provider

`ReasonixProvider` drives `reasonix acp` over the shared ACP session layer (`AcpSessionClient`). Verified against Reasonix v2.41.0.

- **Interactive questions (`ReasonixOptions.UserInputHandler`):** Reasonix's built-in `ask` tool is always available, and each of its questions arrives as its own ACP `session/request_permission` request: tool call id `ask-<hex>-<turn>-qN`, options `qN:1`, `qN:2`, … plus `qN:cancel` (`reject_once`), with the question text, the options (`Label` / `Description`, read case-insensitively) and the `multi` flag in `toolCall.rawInput` and the header in `toolCall.content`. The next question of the same `ask` call is sent only after the previous one is answered. The handler is `null` by default and then nothing changes: a question request is answered with its first option like any other permission request, no question message is produced, and a warning that names the question is logged through the optional `ILogger<ReasonixProvider>`. When a handler is set, the provider gives `AcpSessionClient` a permission hook (`AcpPermissionRequestHandler`, shared ACP core; the default answer applies when it returns `null`) that routes question requests, and only those, to the handler as a `ReasonixUserInputRequest` (`RequestId` = the `ask-…-qN` tool call id, one `Question` with `Id`, text, `Header`, `MultiSelect` as Reasonix reported it, and the options without `qN:cancel`); tool approvals (`gate-*`) are answered exactly as before.
  The handler returns a `ReasonixUserInputResponse` with the chosen label per question text. ACP carries one option id per answer, so the questions are **single-choice with no free text**: exactly one offered label (matched exactly and as a whole, so a label may contain a comma; with duplicate labels the first option wins) is sent as `selected qN:<i>`. A `null` response, an empty answer, text that is not an offered label, more than one value, a throwing handler and `ReasonixOptions.QuestionTimeout` (default 30 minutes, `Timeout.InfiniteTimeSpan` for no limit; any other non-positive value throws `ArgumentOutOfRangeException` before a process starts when a handler is set) passing decline the question with `selected qN:cancel`, or the `cancelled` outcome when the request offers no cancel option. Reasonix 2.41.0 does not continue after either: it fails the `ask` call with `ask: context canceled` and ends the turn with stop reason `error`. The provider records its own decline and reports such a turn (stop reason `error` or `cancelled`) as `terminal.completed` with `question_declined: true` and the original `stop_reason`; a turn that ends with `error` and no declined question is still `terminal.failed`, and a turn that continues after a decline ends with its own result. Reasonix accepted an answer after 120 seconds. Cancelling the call cancels the handler token, answers a pending question `cancelled` while the transport is still open, records no decline and ends the Reasonix process; the call ends with `OperationCanceledException`.
  Each routed question is also yielded to the caller as a `question.request` stream message (`ReasonixUserInputRequest.MessageType`) after the `tool.call` of the `ask` call and in the position it arrived; `ReasonixUserInputRequest.TryFromCliMessage` reads it back from its `params`. The `ask` call itself stays an ordinary `tool.call` / `tool.update` pair whose `toolu_…` id differs from the question's `RequestId`, so a consumer that shows the question another way hides it by the tool title `ask` (`ReasonixUserInputRequest.QuestionToolName`). The shared `AcpJsonRpcClient` now serializes its transport writes, so a delayed answer cannot interleave with another request, and a hook that is still waiting when the client is disposed is cancelled without an error reply.

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
- Hosts can enable `CliProcessOwnershipOptions` to persist managed subprocess PID ownership.

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

`SessionId` hits a compatible session in the shared ACP pool; `ModeId` is sent again after `session/new` or warm reuse so the permission and execution mode stay aligned with the caller's request.

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

Copilot image attachments are passed in `CopilotOptions.Images` as `CopilotImageAttachment` values (media type, bytes and an optional file name). The list is empty by default, and an empty list leaves the send unchanged. The images travel as inline `UserMessageAttachmentBlob` entries on the one `SendAndWaitAsync` call of a run, whether the session is fresh, resumed, or created after a failed resume. Session creation and resume configuration never carry images.

- `CopilotProvider` validates every image before any SDK client or process starts. A null entry, empty data, or a media type that does not start with `image/` throws an `InvalidOperationException` that names the position and the media type, never the bytes.
- `CopilotImageAttachment` and `CopilotOptions` print and serialize only the media type, file name, byte length and image count. The base64 string exists only for the SDK call.
- Each `user.message` echo or other raw event is serialized with image blob data replaced by a fixed marker that keeps the length, so the base64 string never reaches a raw event's `Content`.
- `ICopilotModelCapabilityReader.GetImageCapabilityAsync(options, model)` reads a model's image capability from the SDK model list. `CopilotProvider` implements it and `AddHagiCodeLibs()` registers it. It returns `CopilotModelImageCapability` (`SupportsVision`, `MaxPromptImages`, `MaxPromptImageSize`, `SupportedMediaTypes`), where a limit Copilot does not report, or reports as zero or negative, is `null`. It returns `null` (unknown) when no model is given, the model is not listed, its vision entry is missing, or the list cannot be read. The read starts a short-lived client and disposes it on every path; only cancellation of the caller's token is thrown.

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

Hermes `SessionId` still means a session reuse key within the current provider instance; `ModeId` takes part in the shared pool fingerprint and is re-applied after an ACP session is created or reused.

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

## Agent memory limits (Linux)

An agent CLI and the commands it starts can be capped at a memory limit. When a limit is active, `CliProcessManager` and the launchers that build their own start information (Codex exec, the Codex app-server and OpenCode, through `CgroupScopeLauncher`) start the CLI inside a systemd user scope. The scope charges the agent and every process it starts. `systemd-run --scope` replaces itself with the CLI, so the tracked process id, the standard streams and the exit code are the CLI's, and `ProcessResult.ResolvedExecutablePath` and `CommandPreview` still name the CLI.

The limit comes from `ICliResourceLimitProvider`. `AddHagiCodeLibs()` registers `EnvironmentCliResourceLimitProvider` with `TryAddSingleton`; it reads `AGENT_MEM_MAX`, a positive whole number with an optional `M`, `MB`, `G` or `GB` suffix (case-insensitive, megabytes by default). An empty, zero or negative value means no limit; a malformed value also means no limit and logs one warning. A host that keeps its own setting registers its provider with `Replace`, and then `AGENT_MEM_MAX` is not read. `new CliProcessManager()` has no provider and never applies a limit. The limit is read for each launch, so a change applies to agents started afterwards.

Every value is derived from the limit in whole megabytes, rounded down:

| Property | Value |
| --- | --- |
| `MemoryMax` | the limit |
| `MemoryHigh` | 85% of the limit |
| `MemorySwapMax` | `0` |
| `TasksMax` | `2000` |
| `NODE_OPTIONS` | adds `--max-old-space-size=` 75% of the limit; other content is kept, and a caller's own `--max-old-space-size` wins |

For example, this setting:

```bash
AGENT_MEM_MAX=4G
```

starts each agent CLI as:

```text
systemd-run --user --scope --quiet -p MemoryMax=4096M -p MemoryHigh=3481M -p MemorySwapMax=0 -p TasksMax=2000 -- <cli> <args>
NODE_OPTIONS=--max-old-space-size=3072
```

Requirements: Linux with cgroup v2, `systemd-run` on `PATH`, and a running systemd user manager that the process can reach (`XDG_RUNTIME_DIR` or `DBUS_SESSION_BUS_ADDRESS` must be set). `CgroupScopeCapabilityProbe` checks this once per process with a trial `systemd-run --user --scope --quiet -p MemoryMax=64M -- true` (5 s timeout) and reports `ok`, `not-linux`, `systemd-run-missing` or `user-scope-unavailable`. It never spawns anything on Windows or macOS. When the host cannot enforce the limit, agents start without it and one warning is logged per reason.

Version probes, discovery and CI setup set `ProcessStartContext.ApplyResourceLimits = false` (or `CliExecutionRequest.ApplyResourceLimits = false`) and are never limited.

## Guides

- AVIF conversion: [`docs/avif-backend-integration-guide.md`](docs/avif-backend-integration-guide.md)
- Git backend: [`docs/git-backend-integration-guide.md`](docs/git-backend-integration-guide.md)
- Skills online API: [`docs/skills-online-api-reference.md`](docs/skills-online-api-reference.md)
- CLI discovery: [`docs/agent-cli-discovery-guide.md`](docs/agent-cli-discovery-guide.md)

## Design goals

- Zero heavy framework dependencies in the reusable libraries.
- Cross-platform CLI process management for Windows, macOS, and Linux.
- Stream-oriented provider integrations that can power higher-level HagiCode tools.
- Simple provider registration for DI and non-DI consumers.