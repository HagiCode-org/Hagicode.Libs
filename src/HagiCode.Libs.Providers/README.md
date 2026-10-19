# HagiCode.Libs.Providers

`HagiCode.Libs.Providers` builds on `HagiCode.Libs.Core` and adds reusable provider abstractions plus built-in integrations for Claude Code, Copilot, Codex, DeepAgents, CodeBuddy, Gemini, Junie, Hermes, Dsh, Kimi, Kiro, OpenCode, Pi, OMP, QoderCLI, and Reasonix.

## What is included

- The `ICliProvider` and `ICliProvider<TOptions>` contracts for provider-oriented integrations
- Built-in provider implementations for the supported HagiCode CLI backends
- `AddHagiCodeLibs()` for dependency injection registration
- A provider registry for resolving providers by name or alias
- Registration of the shared `ICliExecutionFacade` for provider-side probes or adapters
- Registration of the shared `CliProviderPoolCoordinator`, Hermes pool defaults, and ACP/runtime reuse backends
- Single-attempt provider primitives that preserve native thread or session context for upstream retry orchestration

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
using HagiCode.Libs.Providers.Dsh;
using HagiCode.Libs.Providers.Gemini;
using HagiCode.Libs.Providers.Junie;
using HagiCode.Libs.Providers.Hermes;
using HagiCode.Libs.Providers.Kimi;
using HagiCode.Libs.Providers.Kiro;
using HagiCode.Libs.Providers.OpenCode;
using HagiCode.Libs.Providers.Omp;
using HagiCode.Libs.Providers.Pi;
using HagiCode.Libs.Providers.Reasonix;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHagiCodeLibs();

await using var serviceProvider = services.BuildServiceProvider();
var executionFacade = serviceProvider.GetRequiredService<ICliExecutionFacade>();
var codebuddy = serviceProvider.GetRequiredService<ICliProvider<CodebuddyOptions>>();
var copilot = serviceProvider.GetRequiredService<ICliProvider<CopilotOptions>>();
var codex = serviceProvider.GetRequiredService<ICodexProvider>();
var deepAgents = serviceProvider.GetRequiredService<ICliProvider<DeepAgentsOptions>>();
var dsh = serviceProvider.GetRequiredService<ICliProvider<DshOptions>>();
var gemini = serviceProvider.GetRequiredService<ICliProvider<GeminiOptions>>();
var junie = serviceProvider.GetRequiredService<ICliProvider<JunieOptions>>();
var hermes = serviceProvider.GetRequiredService<ICliProvider<HermesOptions>>();
var kimi = serviceProvider.GetRequiredService<ICliProvider<KimiOptions>>();
var kiro = serviceProvider.GetRequiredService<ICliProvider<KiroOptions>>();
var openCode = serviceProvider.GetRequiredService<ICliProvider<OpenCodeOptions>>();
var pi = serviceProvider.GetRequiredService<ICliProvider<PiOptions>>();
var omp = serviceProvider.GetRequiredService<ICliProvider<OmpOptions>>();
var reasonix = serviceProvider.GetRequiredService<ICliProvider<ReasonixOptions>>();
```

The same DI graph also exposes the shared pool services for Hermes diagnostics or explicit cleanup:

```csharp
using HagiCode.Libs.Providers.Pooling;

var poolCoordinator = serviceProvider.GetRequiredService<CliProviderPoolCoordinator>();
var poolDefaults = serviceProvider.GetRequiredService<CliProviderPoolConfigurationRegistry>();
```

## Shared adapter parity

`Claude Code`、`CodeBuddy`、`Hermes` 现在与 `hagicode-core` 的对应 provider 薄适配层共享同一套 libs-backed 实现。重点是：

- `Claude Code` 继续保留 raw stream / resume 语义，但 `ClaudeCodeProvider` 现在固定按次启动并回收本地 CLI 进程，不再做 warm transport reuse
- `ClaudeCodeOptions.UserInputHandler`（默认 `null`，此时行为不变）让代理在运行中向用户提问：设置后 provider 会给 CLI 追加 `--permission-prompt-tool stdio`，CLI 因此注册 `AskUserQuestion` 并把权限询问以 `control_request` 发给宿主。`AskUserQuestion` 请求（`ClaudeUserInputRequest`）在后台任务里交给 handler，等待回答期间流不会被阻塞；handler 返回每个问题文本对应的选项 label 或自由文本，provider 写回 `control_response`（`allow` + `updatedInput` = 原输入加 `answers`，多选用逗号加空格拼成一个字符串）。handler 返回 `null`、抛异常、超过 `QuestionTimeout`（默认 30 分钟）都会以 `deny` 加说明回复，CLI 把该调用记入 `permission_denials`，运行仍正常结束；调用方取消则尽力发送 `deny` 后以 `OperationCanceledException` 结束。提问挂起期间，`PromptStartTimeout`、`SubagentBackgroundWorkTimeout` 与监视器截止时间加 `BackgroundWorkGracePeriod` 不会结束运行，问题解决后重新生效。其他 `can_use_tool` 请求不经过 handler，立即以固定文案 `deny`，与 CLI 不批准该工具时的结果一致；CLI 对宿主 `control_response` 的回显会被丢弃。提问按工具名识别（CLI 2.1.159 不带 `requires_user_interaction`，2.1.292 带）。交互请求同时以 `control_request` 流消息、按到达位置交给调用方（`ClaudeUserInputRequest.TryFromCliMessage` 可读回），与 `agent_work_snapshot` 一样，持久化或解析整条流的调用方必须识别并跳过或处理它；provider 自行回复的请求不会向外传递。是否设置 `UserInputHandler` 由宿主按每次运行决定，Libs 不施加自己的策略。
- `PiOptions.UserInputHandler`（默认 `null`，此时命令行、关闭的 stdin、按进程退出结束的行为与扩展都保持不变）让 Pi 代理向用户提问：设置后（且未设置 `DisableAllTools`，否则忽略 handler）provider 改用 `pi --mode rpc`，通过 `-e` 加载内置的 `ask_user_question` 扩展，prompt 作为 RPC `prompt` 命令发送，其余选项含义不变；会话 id 由 `get_state` 取得，以 `agent_settled` 而非进程退出作为运行结束，其余事件仍交给同一个 `PiJsonEventMapper`。问题经一个带版本标记 `hagicode:ask_user_question:v1` 的 `input` 对话框传递（标题 = 标记 + 换行 + JSON，回答 = 值里的 JSON），在后台任务交给 handler；多选用逗号加空格拼成一个字符串。handler 返回 `null`、抛异常或超过 `QuestionTimeout`（默认 30 分钟）都以取消回复该对话框，代理得知用户拒绝回答，运行仍正常结束；其他扩展的对话框一律回复取消，纯通知类请求忽略。调用方取消会先取消未决对话框、发送 `abort`、关闭 stdin，超时仍未退出则终止进程，随后以 `OperationCanceledException` 结束。用户扩展同样注册了 `ask_user_question` 时 Pi 会启动失败（退出码 1、无输出），provider 识别后记录警告并回退到 JSON print 模式（无提问）。问题同时以 `extension_ui_request` 流消息按到达位置交给调用方（`PiUserInputRequest.TryFromCliMessage` 可读回），提问工具自身的调用与结果仍是普通 `tool.*` 消息，由调用方决定是否隐藏。详见根目录 README 的 Pi provider 一节。
- `OmpOptions.UserInputHandler`（默认 `null`，此时命令行、关闭的 stdin、按进程退出结束的行为都保持不变）让 OMP 代理通过 OMP 内置的 `ask` 工具向用户提问：设置后（且未设置 `DisableAllTools`，否则忽略 handler）provider 改用 `omp --mode rpc-ui`（不加 `--no-ui`、不加扩展；非空的 `AllowedTools` 会追加 `ask`），其余选项含义不变；先发送 `set_ask_dialog`（OMP 每个进程默认关闭结构化提问），成功后用 `get_state` 取会话 id，再以 RPC `prompt` 命令发送提示词；运行以 `session_settled` 而非进程退出结束（`prompt_result` 之后后台任务仍可能唤醒代理），结果按 `prompt_result.status` 判定，其余事件仍交给同一个 `OmpJsonEventMapper`（`rpc-ui` 附在 `message_update` 上、序列化时已包含后续增量的消息快照会先去掉，文本与思考事件与 JSON 模式完全一致；工具调用事件保留 `partial`，因此首条 `tool.call` 就带有真实的调用 id 与工具名）。每个 `ask` 请求在后台任务交给 handler，回答按问题顺序写成 `{id, selectedOptions, customInput?}`：单选只有文本等于某个选项 label 时才选中该项，多选只有文本能按逗号加空格拆成互不重复的选项 label（优先匹配最长 label）时才选中，其余一律作为 `customInput`，从不发送 OMP 会拒绝的形状。handler 返回 `null`、缺少某个问题的回答、抛异常或超过 `QuestionTimeout`（默认 30 分钟）都回复 `{cancelled:true}`；**与 Claude、Pi 不同，OMP 随即以 `aborted` 结束本次运行**，provider 把它当作正常结束（`terminal.completed`，`stop_reason: "aborted"`）；不发送 `timedOut` 标记（OMP 会替用户自动选推荐项并继续）。其他扩展的对话框一律回复取消，纯通知类请求忽略；OMP 自己发来的 `cancel` 帧会取消对应 handler 且不回复。调用方取消只发送 `abort`（不为未决问题单独回复），等 OMP 结束并 settle 后关闭 stdin，超时仍未退出则终止进程，随后以 `OperationCanceledException` 结束。OMP 拒绝 `set_ask_dialog`（没有该命令的旧版本）时不发送提示词，provider 记录带版本的警告并回退到 JSON print 模式（无提问）。问题同时以 `extension_ui_request` 流消息按到达位置交给调用方（`OmpUserInputRequest.TryFromCliMessage` 可读回），`ask` 工具自身的调用与结果仍是普通 `tool.*` 消息。已在 omp 18.10.4 上验证；详见根目录 README 的 OMP provider 一节。
- 调用方取消（`CancellationToken`）会以 `OperationCanceledException` 结束流，不会被当作正常的流结束；只有子代理等待计时器触发的静默停止会被吞掉。取消后 transport 会被释放，进程随之回收
- `CodeBuddy` 的 tool update 归一化与 permission-mode 映射统一落在 `CodebuddyProvider`
- `Hermes` 的 ACP session reuse、fallback 文本聚合与 lifecycle 诊断统一落在 `HermesProvider`
- `HagiCode.Libs.Providers` 只负责单次执行并保留 provider-native 会话上下文；如果上层需要重试，必须在 provider 调用边界之外显式编排

`ProviderRegistry` 的规范名称与兼容别名也已统一：

- `claude-code` -> `claude`, `claudecode`, `anthropic-claude`
- `codebuddy` -> `codebuddy-cli`
- `hermes` -> `hermes-cli`
- `pi` -> `pi-cli`

## Provider usage

```csharp
using HagiCode.Libs.Providers.Copilot;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Codebuddy;
using HagiCode.Libs.Providers.DeepAgents;
using HagiCode.Libs.Providers.Hermes;
using HagiCode.Libs.Providers.Kimi;
using HagiCode.Libs.Providers.Kiro;
using HagiCode.Libs.Providers.Omp;
using HagiCode.Libs.Providers.Pi;
using HagiCode.Libs.Providers.Reasonix;

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
    ModeId = "bypassPermissions",
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

// Copilot tool-call turns always terminate with either a "result" or "error"
// message so upstream Orleans/session UIs can leave the running state deterministically.

// Reuse a persisted provider-native Copilot conversation on the next call.
var resumedOptions = copilotOptions with { SessionId = "copilot-session-123" };

// Without SessionId, Copilot requests stay anonymous; WorkingDirectory and permission
// flags only shape the compatibility fingerprint and do not preserve prior runtime state on their own.

var codexOptions = new CodexSessionOptions
{
    ThreadOptions = new ThreadOptions
    {
        WorkingDirectory = "/path/to/repo",
        Model = "gpt-5-codex",
        SandboxMode = SandboxMode.WorkspaceWrite,
        ApprovalPolicy = ApprovalMode.Never,
        AdditionalDirectories = ["/path/to/repo"],
        SkipGitRepoCheck = true,
    }
};

await using var codexSession = await codex.CreateSessionAsync(codexOptions);
var codexResult = await codexSession.Thread.RunAsync("Reply with exactly the word 'pong'");
Console.WriteLine(codexResult.FinalResponse);

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
// Compatibility flags such as --auto-approve may still be forwarded, but cross-call
// resume depends on provider-native session/load support.

// Reuse the same Codex thread id to continue a previous conversation by setting
// CodexSessionOptions.ThreadId before calling CreateSessionAsync.

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

var piOptions = new PiOptions
{
    WorkingDirectory = "/path/to/repo",
    Provider = "omniroute",
    Model = "glm/glm-4.7",
    DisableAllTools = true,
    NoSession = true,
    EnvironmentVariables = new Dictionary<string, string?>
    {
        ["PI_OFFLINE"] = "1"
    }
};

await foreach (var message in pi.ExecuteAsync(piOptions, "Plan a two-day Chongqing trip in three short bullet points."))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}

var reasonixOptions = new ReasonixOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "provider/model", // a provider or provider/model ref defined in your Reasonix configuration
    SessionId = "reasonix-session-123"
};

await foreach (var message in reasonix.ExecuteAsync(reasonixOptions, "Reply with exactly the word 'pong'"))
{
    Console.WriteLine($"{message.Type}: {message.Content}");
}
```

## Claude Code run steering

`ClaudeCodeOptions.Steering` (default `null`, nothing changes) lets the host send follow-up user messages to an execution that is already running. Create one `ClaudeRunSteering` per run and pass it in the options; `SteerAsync` is only meaningful while `ExecuteAsync` is being iterated:

```csharp
var steering = new ClaudeRunSteering();
steering.OutcomeObserved += (_, args) => RecordOutcome(args.SteerId, args.Outcome);

var options = new ClaudeCodeOptions
{
    Steering = steering,
    SteerDeliveryTimeout = TimeSpan.FromSeconds(30) // default; must be positive
};

// While the stream is being read:
var result = await steering.SteerAsync("steer-1", "switch to lakes"); // Sent or NotSteerable
```

- The steer is written as a `user` message whose `uuid` is the steer id and whose priority is `next`. The CLI injects it at the next tool boundary, or starts a follow-up turn for it when it was written during text-only generation. The CLI echoes the steer, and the echo is swallowed: it is not yielded as conversation and it does not count as the start of the main prompt.
- `Sent` means the line was written. Whether the CLI consumed it is reported later through `OutcomeObserved`, exactly once per accepted steer: `Consumed` when the echo arrives, `NotConsumed` when the run ends first. A run ends first on a timeout, on cancellation, when the CLI exits, or when an error result ends it.
- A `result` does not end the run while an accepted steer is unconsumed. The run waits up to `SteerDeliveryTimeout` for the echo and then ends with the steer reported as `NotConsumed`.
- `SteerAsync` returns `NotSteerable` and writes nothing when the handle is not attached (the CLI is not connected yet, or the run has ended), a steer with the same id is still outstanding, or the write fails.
- Only the Claude Code provider supports steering. Priority `now` and images in a steer are not offered.

## Pi interactive questions

Set `PiOptions.UserInputHandler` and the Pi agent can ask the user multiple-choice questions with the bundled `ask_user_question` tool (the input schema of Claude's `AskUserQuestion`). Without a handler Pi runs as before in `--mode json --print`; with one the provider runs `pi --mode rpc` and loads the extension from an embedded resource, so there is nothing to install.

```csharp
var options = new PiOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "omniroute/cc/claude-haiku-4-5-20251001",
    QuestionTimeout = TimeSpan.FromMinutes(10), // default 30 minutes, Timeout.InfiniteTimeSpan for no limit
    UserInputHandler = async (request, cancellationToken) =>
    {
        // request.ToolCallId identifies the agent's tool call; each question has Header, MultiSelect and Options.
        var answers = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var question in request.Questions)
        {
            // Labels of the chosen options, or free text. A multi-select answer is sent as "A, B".
            answers[question.Question] = [await AskTheUserAsync(question, cancellationToken)];
        }

        return new PiUserInputResponse(answers); // null declines the question; the agent is told the user declined
    }
};

await foreach (var message in pi.ExecuteAsync(options, "Ask me which database to use, then set it up."))
{
    if (message.Type == PiUserInputRequest.MessageType &&
        PiUserInputRequest.TryFromCliMessage(message, out var question))
    {
        // The question as it arrived in the stream (the handler is already running); show it, or ignore it.
    }
}
```

The handler runs in a background task per question, so the stream keeps flowing while the user thinks. Dialogs of other Pi extensions are answered cancelled and never reach it. Cancelling the call aborts the run and ends the Pi process. See the repository README (Pi provider) for the wire contract, the failure cases and the opt-in real-CLI tests.

## OMP interactive questions

Set `OmpOptions.UserInputHandler` and the OMP agent can ask the user questions with OMP's built-in `ask` tool (header, option descriptions, multi-select and free text). Without a handler OMP runs as before in `--mode json --print`; with one the provider runs `omp --mode rpc-ui` and enables OMP's structured ask dialog with `set_ask_dialog` before it sends the prompt. Nothing is installed or loaded. Verified against omp 18.10.4; a build that rejects `set_ask_dialog` runs the request in JSON print mode without questions and logs a warning.

```csharp
var options = new OmpOptions
{
    WorkingDirectory = "/path/to/repo", // not the home directory: omp switches to a temp dir there
    QuestionTimeout = TimeSpan.FromMinutes(10), // default 30 minutes, Timeout.InfiniteTimeSpan for no limit
    UserInputHandler = async (request, cancellationToken) =>
    {
        // request.RequestId identifies the ask call; each question has Id, Header, MultiSelect and Options.
        var answers = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var question in request.Questions)
        {
            // Offered labels select options (several for a multi-select question); any other text is sent as free text.
            answers[question.Question] = await AskTheUserAsync(question, cancellationToken);
        }

        return new OmpUserInputResponse(answers); // null declines: OMP then ends the run as aborted (a normal end)
    }
};

await foreach (var message in omp.ExecuteAsync(options, "Ask me which database to use, then set it up."))
{
    if (message.Type == OmpUserInputRequest.MessageType &&
        OmpUserInputRequest.TryFromCliMessage(message, out var question))
    {
        // The question as it arrived in the stream (the handler is already running); show it, or ignore it.
    }
}
```

A declined, unanswered or expired question ends the OMP run (`terminal.completed` with `stop_reason: "aborted"`), unlike Claude and Pi, whose agents continue; continue the session with a normal next message (`SessionId`). Dialogs of OMP extensions are answered cancelled and never reach the handler. Cancelling the call sends `abort`, lets OMP close the open question itself and ends the OMP process. See the repository README (OMP provider) for the wire contract, the answer mapping, the failure cases and the opt-in real-CLI tests.

## Reasonix interactive questions

Reasonix's built-in `ask` tool is always available in `reasonix acp`. Each question it asks arrives as its own ACP `session/request_permission` (tool call id `ask-<hex>-<turn>-qN`, options `qN:1`, `qN:2`, … plus `qN:cancel`), and the next question of the same `ask` call arrives only after the previous one is answered. Set `ReasonixOptions.UserInputHandler` and the provider routes those requests to the handler instead of answering them itself. Verified against Reasonix v2.41.0.

```csharp
var options = new ReasonixOptions
{
    WorkingDirectory = "/path/to/repo",
    Model = "provider/model",
    QuestionTimeout = TimeSpan.FromMinutes(10), // default 30 minutes, Timeout.InfiniteTimeSpan for no limit
    UserInputHandler = async (request, cancellationToken) =>
    {
        // One question per request: request.RequestId is the ask-…-qN tool call id; Question has Id, Header,
        // MultiSelect (as Reasonix reported it) and Options (label and description, without the cancel option).
        var label = await AskTheUserToPickOneAsync(request.Question, cancellationToken);
        return new ReasonixUserInputResponse(new Dictionary<string, IReadOnlyList<string>>
        {
            [request.Question.Question] = [label] // exactly one offered label; anything else declines
        });
    }
};

await foreach (var message in reasonix.ExecuteAsync(options, "Ask me which database to use, then set it up."))
{
    if (message.Type == ReasonixUserInputRequest.MessageType &&
        ReasonixUserInputRequest.TryFromCliMessage(message, out var question))
    {
        // The question as it arrived in the stream (the handler is already running); show it, or ignore it.
    }
}
```

- **Mapping:** an answer that is exactly one offered label is sent as `selected` with that option's id (`qN:<i>`). A `null` response, an empty answer, text that is not an offered label, more than one value, a handler that throws and `QuestionTimeout` passing all decline the question: `selected qN:cancel` when the request offers it, otherwise the `cancelled` outcome. Labels are matched exactly and as a whole, so a label may contain a comma; when two options share a label, the first one is selected.
- **Single choice, no free text:** ACP carries one option id per answer. A question that the agent marked `multi` is still answered with one label, and free text cannot be sent; show these questions as single-choice without a custom-answer field.
- **Decline:** Reasonix 2.41.0 does not continue after a decline. It fails the `ask` call (`ask: context canceled`) and ends the turn with stop reason `error`. Because the provider declined, it reports that turn as `terminal.completed` with `question_declined: true` and the original `stop_reason`; a turn that ends with `error` and no declined question is still `terminal.failed`. Continue the session with a normal next message (`SessionId`).
- **Stop:** cancelling the call cancels the handler token, answers a pending question `cancelled` when the transport is still open, records no decline, and ends the Reasonix process; the stream ends with `OperationCanceledException` as before.
- **No handler:** `UserInputHandler` is `null` by default and nothing changes: a question request is answered with its first option like any permission request, no question message is produced, and a warning naming the question is logged. Tool approvals (`gate-*`) are never routed to the handler and are answered as before, with or without one.
- The question message (`question.request`) carries the raw request in `params` and follows the `tool.call` of the `ask` call; the `ask` call itself (its `tool.call` and its completed or failed `tool.update`) stays an ordinary tool message whose `toolCallId` differs from the question's `RequestId`, so callers hide it by the tool title `ask` (`ReasonixUserInputRequest.QuestionToolName`).

## Copilot interactive questions

Copilot's built-in `ask_user` tool can ask the user a question during a run. Set `CopilotOptions.UserInputHandler` to answer those questions; when it is `null` (the default) the provider is unchanged: the derived `--no-ask-user` flag stays and the agent cannot ask.

```csharp
var options = new CopilotOptions
{
    QuestionTimeout = TimeSpan.FromMinutes(30), // default; Timeout.InfiniteTimeSpan waits without a limit
    UserInputHandler = async (question, cancellationToken) =>
    {
        // question.RequestId, question.Question, question.Choices and question.AllowFreeform describe the question.
        var answer = await AskTheUserAsync(question, cancellationToken);
        return new CopilotUserInputResponse(new Dictionary<string, IReadOnlyList<string>>
        {
            [question.Question] = [answer]
        });
    }
};

await foreach (var message in copilot.ExecuteAsync(options, "Ask me which color, then reply with it."))
{
    if (message.Type == CopilotUserInputRequest.MessageType &&
        CopilotUserInputRequest.TryFromCliMessage(message, out var asked))
    {
        // The question is also yielded here, in stream order, for callers that show it themselves.
    }
}
```

- **Stream messages:** each question is one `question.request` message (`request_id`, `question`, `choices`, `allow_freeform`) in the position it arrived. When Copilot completes a pending question itself, a `question.closed` message (`request_id`) follows. A declined question adds a `diagnostic` message that names the question id and the reason.
- **Limits:** one question per request, string choices without descriptions, no multi-select and exactly one string answer. A question without choices accepts any one non-empty value. An answer is refused (and the question declined) when it is empty, has more than one value, or is not an offered choice while `AllowFreeform` is `false`.
- **Question id:** the `ToolCallId` of the matching `user_input.requested` event, else its `RequestId`, else `copilot-input-{sessionId}-{n}`.
- **Decline:** a `null` response, a refused answer, a timeout, or a throwing handler declines the question (the handler's own exception is not rethrown). The SDK callback then fails for that question, which is how Copilot is told the user gave no answer; the run continues with its own result. The encoding is gate G2 below and is unverified. A session error that Copilot reports after a decline is not yet normalized by the provider and is reported as before.
- **Deadlines:** while a question is pending, the idle timeout and the request timeout do not run. The request deadline counts only the time with no pending question (a stall still ends with `[idle_timeout]`, a long run with `[request_timeout]`). With a handler the SDK wait has no limit of its own, so the request deadline is enforced by the provider.
- **Flags:** with a handler, `--no-ask-user` is never passed. An `AdditionalArgs` entry for it is dropped with a diagnostic. Tool approvals are unchanged and never reach the handler.
- **Stop:** cancelling the caller cancels a pending question and ends the run through the normal cancellation path; nothing is recorded as declined.
- **Options:** `CopilotOptions.ToString()` does not print `GitHubToken`.

**Verification status (SDK `GitHub.Copilot.SDK` 1.0.0-beta.10, Copilot CLI 1.0.92):**

- The live probe did not run: no Copilot credentials were exported to the environment (`COPILOT_GITHUB_TOKEN`, `GH_TOKEN` and `GITHUB_TOKEN` are unset), and the user's stored login was not copied into a scratch home. Gates G1, G2, G4 and G5 are **unverified**. Export one of the variables and run the opt-in tests below to settle them.
- G1 (callback and `user_input.requested` order, and whether the question text matches): unverified. The matching queue and the fallback id cover either order.
- G2 (decline encoding): unverified. The callback fails with an internal `CopilotUserInputDeclinedException` (see `CopilotUserInput.cs`); switching to an empty answer changes only that one place.
- G3 (infinite timeout): `SendAndWaitAsync` accepts `Timeout.InfiniteTimeSpan`. Checked from the SDK's IL: the timeout is only passed to `CancellationTokenSource.CancelAfter`, with no range check. Whether the CLI caps a long-waiting callback is unverified, so answers after 60 seconds and after several minutes are unverified.
- G4 (tool name in `ToolExecution*` events): `copilot --help` in CLI 1.0.92 describes `--no-ask-user` as "Disable the ask_user tool", so `ask_user` is the tool name. The event shape is unverified.
- G5 (resume with an unanswered `ask_user`): unverified.

**Opt-in real-CLI tests:** `ExecuteAsync_real_cli_asks_and_answers_one_question_when_opted_in` and `ExecuteAsync_real_cli_declines_one_question_and_completes_when_opted_in` in `tests/HagiCode.Libs.Providers.Tests/CopilotUserInputTests.cs` run only with `HAGICODE_REAL_CLI_TESTS=1` and `HAGICODE_REAL_CLI_COPILOT_USER_INPUT_TESTS=1` and a logged-in or token-authenticated Copilot CLI. They have not been run.

## OpenCode question forms (2.0.32 wire)

The OpenCode `question` tool runs on OpenCode's form service. A live probe against a scratch `opencode serve` (private `HOME`/`XDG_*`, own port) recorded the wire in `tests/HagiCode.Libs.Providers.Tests/Fixtures/OpenCode/v2.0.32/` (`events-question-*.jsonl`, `form-*.json`, `tool-result-*.json`, scrubbed).

- **Gate G1 (confirmed):** a real model called `question`. `form.created` carried the tool's questions as fields `q0` (`string`, single choice) and `q1` (`multiselect`), both `custom: true`. The turn ended with `session.execution.succeeded` after a reply and after a cancel with a message. The model saw `User has answered your questions: ...` after a reply and the tool error `The user declined to answer this question` after a cancel.
- **Gate G2 (confirmed live in an isolated `opencode serve`):** cancelling the pending question form with a message, then calling `POST /api/session/{id}/interrupt`, returned `{"interrupted":true}`. The event sequence ended with `form.cancelled`, `session.tool.failed` (carrying the cancel message), then `session.execution.interrupted` (`reason: "user"`). The scrubbed capture is `tests/HagiCode.Libs.Providers.Tests/Fixtures/OpenCode/v2.0.32/events-question-cancel-interrupt-probe.jsonl`. A turn loop must treat `session.execution.interrupted` as terminal.
- **Gate G3 (confirmed):** a list in a `string` field returns `400 FormInvalidAnswerError` ("Expected string for form field: q0"); a string in a `multiselect` field returns `400` ("Expected string array for form field: q1"). A 400 leaves the form pending. Free text is accepted as a string (`string` field) and as an extra list item (`multiselect`). A second reply returns `409 FormAlreadySettledError`.
- **Gate G4 (live probe attempted; not confirmed):** a scratch MCP server connected to OpenCode 2.0.32 and received `tools/call`, then sent a valid `elicitation/create` request. In headless `serve`, OpenCode did not emit `form.created` or expose a form through `GET /api/session/{id}/form`; the MCP tool call remained pending until the session was interrupted. The fixture test `OpenCodeHttpClientTests.Mcp_form_is_left_untouched_while_a_question_is_answered` confirms that the client ignores and leaves `metadata.kind: "mcp"` forms untouched when such a form is present, but the live G4 wire path could not be verified with this server mode.

## OpenCode interactive questions

The OpenCode `question` tool is answered through the form interface of OpenCode 2.0.32. A run that supplies a handler gets each question as a `question` message; a run without one declines the question at once.

**Handler.** Set `OpenCodeOptions.UserInputHandler` to a `Func<OpenCodeUserInputRequest, CancellationToken, Task<OpenCodeUserInputResponse?>>`. The request carries `RequestId` (the form id, `frm_...`), `SessionId`, `ToolCallId` and `Questions`; each `OpenCodeUserQuestion` has `Question` (the text, also the answer key), `Header`, `MultiSelect` and `Options` (`OpenCodeUserQuestionOption` with `Label` and `Description`). Answer with an `OpenCodeUserInputResponse` whose `Answers` map each question text to the chosen labels or free text. Return `null` to decline.

**Stream.** `OpenCodeProvider.ExecuteAsync` yields one `question` message (`OpenCodeUserInputRequest.MessageType`) per question, in the order the agent asked, before the assistant and terminal messages. The lifecycle message comes first. Without a handler the message sequence is the one documented above.

**Client.** `OpenCodeHttpClient.PromptAsync(sessionId, request, interaction, cancellationToken)` takes an `OpenCodeTurnInteraction(Handler, QuestionTimeout, OnQuestionAsked)`. The raw form routes are `ListFormsAsync`, `ReplyFormAsync` and `CancelFormAsync` (both return an `OpenCodeFormOutcome`, where `AlreadySettled` means the form was closed earlier) and `InterruptSessionAsync`.

**Answer encoding.** A single-choice question takes one string (several values are joined with `, `); a multi-select question takes a list of strings. A question with no answer is left out of the reply, so the agent sees it as unanswered. A reply that OpenCode refuses with `400 FormInvalidAnswerError` leaves the form pending, and the client then declines that question so the run goes on.

**Declines.** The agent receives the following message for each declined form (`OpenCodeUserInput`):

| Case | Constant | Message sent to the agent |
| --- | --- | --- |
| Handler returns `null`, throws, or the answer is refused | `DeclinedMessage` | "The user declined to answer this question." |
| No answer before `QuestionTimeout` | `TimeoutMessage` | "No answer arrived before the question timed out, so the question was closed." |
| Run has no handler | `NoHandlerMessage` | "The user cannot be asked questions in this run, so the question was closed without an answer." |
| Run ends while the question is pending | `TurnEndedMessage` | "The run ended before the user answered this question." |

The turn then continues to `session.execution.succeeded`, so a declined question is not a provider failure.

**Timeouts.** `OpenCodeOptions.QuestionTimeout` defaults to `OpenCodeOptions.DefaultQuestionTimeout` (30 minutes). `Timeout.InfiniteTimeSpan` waits without a limit. Any other value that is not positive is rejected with an `ArgumentOutOfRangeException` before a runtime is started. While a question is pending the request timeout is paused; the last answer re-arms it with the time that was left, so a stalled agent still times out.

**Stop.** When the caller cancels an interactive turn, the pending forms are cancelled with `TurnEndedMessage` and the session is interrupted (`POST /api/session/{id}/interrupt`), both on a short token of their own. `session.execution.interrupted` is terminal: a turn that OpenCode interrupts returns an error part with reason `user` instead of waiting for the request timeout.

**Not handled.** Forms whose kind is not `question` (for example MCP elicitation, `OpenCodeFormKinds.Mcp`) are left untouched. Forms of other sessions are ignored. OpenCode permission prompts are not part of this interface.

**Real-CLI test.** `OpenCodeRealCliUserQuestionTests` asks, answers and declines one question against a real `opencode` and model. It runs only when `HAGICODE_LIBS_REAL_CLI_OPENCODE_USER_QUESTION=1` is set; `HAGICODE_LIBS_REAL_CLI_OPENCODE_USER_QUESTION_MODEL` (`<provider>/<model>`) and `HAGICODE_LIBS_REAL_CLI_OPENCODE_USER_QUESTION_EXECUTABLE` are optional. The runs create sessions in the OpenCode data directory of the current user.

## OpenCode agent work

An OpenCode run reports the subagents the agent starts as agent work. Each child session whose `parentID` chain leads to the prompted session is one `subagent` item with the id `opencode:<childSessionId>`. The title is the task description OpenCode gives the child (its session `title`), the summary is the child's last text (or the failure message of a failed child), and `usage` carries the finished tool calls and the duration. Child text never enters the assistant text of the run. Sessions of other clients on a shared server are ignored, even when their events arrive on the same stream.

**Background commands.** A background tool call is listed only when OpenCode reports a timeout for it. OpenCode 2.0.32 reports none for a background call, so no `monitor` item is produced; a foreground call with a `timeout` is awaited by the agent and is not listed either.

**Hold-open rule.** OpenCode can report success for the prompted session while a background subagent still runs, and a subagent's completion then starts a follow-up turn of the prompted session. When the prompted session reports success while a tracked child is still active, the turn stays open: its items carry `blocksCompletion` `true`, the follow-up turn and its text join the same prompt result, and the turn returns once no child is active and the prompted session has been idle for the settle interval. The request timeout does not count the wait.

| Option | Default | Effect |
| --- | --- | --- |
| `AgentWorkWaitTimeout` | 10 minutes | Bounds the hold. When it passes, the items still running are reported `stopped`. `Timeout.InfiniteTimeSpan` waits without a limit. Zero or negative values are rejected before a request is sent. |
| `AgentWorkSettleInterval` | 2 seconds | How long the prompted session must stay idle, with no child active, before the held turn returns. Zero returns at once; negative or infinite values are rejected. |
| `EmitAgentWorkSnapshots` | `true` | Set to `false` to skip child tracking. The run then returns at the prompted session's own result, as before this capability, and emits no snapshot. |

A parent failure or interruption while children run ends the turn at once and reports the still-active items `stopped`.

**Message type.** When snapshots are on, the provider yields an `agent_work_snapshot` message (`AgentWorkSnapshot.MessageType`, read back with `AgentWorkSnapshot.TryFromCliMessage`) in stream order, next to the agent's output and before the terminal message. A snapshot is yielded only when the list changes, and a run without child sessions yields none. The message carries no raw provider JSON. Consumers that persist or parse the whole stream must skip or handle it.

**Known gap.** A background shell that finishes after the prompted session reported success can also start a follow-up turn. The hold tracks subagents only, so that follow-up is not awaited; it is a separate decision, because untimed background commands are deliberately not listed.

**Probe.** The wire facts above were recorded from OpenCode 2.0.32 (`Fixtures/OpenCode/v2.0.32/events-subagent-background.jsonl`). The completion notice is an inbox item of type `synthetic` (`metadata.source` `subagent`, `childID`, `state` `completed` or `cancelled`), not a `session.synthetic` event.

## Codex agent work

A Codex run reports the subagents it starts as agent work. Each subagent is one `subagent` item with the id `codex:<threadId>`, `blocksCompletion` `false`, and no monitor items. The host does not keep a Codex run open for subagents.

The run emits a `CodexAgentWorkSnapshotEvent` (`EventType` `agent_work_snapshot`) in its event stream, at the position where the change was observed. The event carries the complete `AgentWorkSnapshot` and no raw provider JSON. It is emitted only when the list changes, and an empty list is never the first snapshot, so a run without subagents emits none. `RunAsync` and the other consumers ignore it, so their results are unchanged.

Both transports produce snapshots, with different detail:

- **App-server (interactive) transport:** subagents are reported from `subAgentActivity` items, from the subagent's own thread (its turns and token usage, and its completed tool-like items), and from `collabAgentToolCall` items when the Codex version sends them. Child-thread notifications update the snapshot only; they never add transcript text, tool calls or statistics.
- **Exec transport (`codex exec --json`):** subagents are reported only from the agent states carried by `collab_tool_call` items. Exec items carry no `usage`.

Limits: notifications that arrive after the main turn's terminal event are not delivered, so the final snapshot shows the last observed state, and the host marks a subagent still running at the end of the run as `stopped`. Titles come from the spawn prompt when the stream carries one; codex-cli 0.163.0 does not, so the title falls back to the subagent's agent path.

## Vendored Codex SDK notice

The Codex integration vendors source from `ManagedCode.CodexSharpSDK` under `Codex/Vendored/`.
The upstream MIT license is preserved in source control at `Codex/Vendored/LICENSE.ManagedCode.CodexSharpSDK.txt` and is included in the NuGet package under `vendor/codex/`.

## Hermes pool controls

`Hermes` is the only built-in provider that still exposes `PoolSettings`:

```csharp
var hermesPoolOptions = new HermesOptions
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

- `Hermes` pools live ACP sessions.
- `Hermes` includes `ModeId` in its reuse fingerprint and re-applies it after `session/new` or warm reuse.
- Setting `HermesOptions.PoolSettings.Enabled = false` falls back to the one-shot path without changing message semantics.
- Non-Hermes providers now execute as one-shot transports or processes. `SessionId` remains a provider-native continuity hint only where that backend supports it.
- Idle eviction is lazy and deterministic; if a lease faults, the coordinator disposes that entry immediately rather than returning it to the warm set.

## Adoption boundaries

- Interactive provider transports still use `CliProcessManager` directly because they need open stdio sessions.
- The new execution facade is intended for provider-facing adapters, diagnostics, and one-shot probes such as version checks.
- Provider callers should continue passing structured option models; the new facade is additive and does not replace provider-specific option records.
- `gemini` is the canonical built-in provider name; `ProviderRegistry` and the dedicated console also accept `gemini-cli` as an alias.
- `junie` is the canonical built-in provider name; `ProviderRegistry` also accepts `junie-cli` as an alias and boots ACP through `junie --acp=true`.
- `dsh` is the canonical built-in provider name; `ProviderRegistry` also accepts `dsh-cli` as an alias and boots ACP through `dsh --profile acp`. Caller-supplied `--profile` arguments are rejected. Before any ACP start, the `dsh --help` banner (`DeepSeek Harness`) is checked so an unrelated distributed-shell `dsh` is not driven as an agent.
- `deepagents` is the canonical built-in provider name, and the managed runtime boots ACP through `deepagents --acp`.
- `kimi` is the canonical built-in provider name; `ProviderRegistry` and the dedicated console also accept `kimi-cli` as an alias.
- `kiro-cli` is the canonical built-in provider name across the shared provider registry and the dedicated console.
- `pi` is the canonical built-in provider name; `ProviderRegistry` also accepts `pi-cli` as a compatibility alias and the built-in provider runs Pi through `--mode json --print` one-shot execution, or through `--mode rpc` when `PiOptions.UserInputHandler` is set.
- `omp` is the canonical built-in provider name; `ProviderRegistry` also accepts `omp-cli` as a compatibility alias and the built-in provider runs OMP through `--mode json --print` one-shot execution, or through `--mode rpc-ui` when `OmpOptions.UserInputHandler` is set.
- `reasonix` is the canonical built-in provider name and targets the Reasonix 2.x ACP contract (npm `latest`, verified with `v2.41.0`). The 1.x line is supported on a best-effort basis without dedicated tests.
- Reasonix forwards only the startup-time `-model` selector. `Model` must be a provider or provider/model ref defined in your Reasonix configuration; an unknown ref fails session start with a readable `terminal.failed` message whose `failure_kind` is `unknown_model`.
- Resume uses `session/resume` when `initialize` advertises `sessionCapabilities.resume`, which restores the session without replaying history. Otherwise `session/load` is used, and the replayed history is filtered out of the turn output by message shape.
- An unknown session id fails with `failure_kind` `unknown_session`. `EnableYolo` maps to `session/set_config_option` with `tool_approval=yolo` when the session advertises that option; a missing option or a failed call is logged and the turn still runs.
- `Effort`, `BudgetUsd` and `TranscriptPath` are accepted for compatibility but not applied, because Reasonix 2.x has no ACP flag or option for them. Permissions, MCP servers and similar policy live in the Reasonix configuration.
- `ReasonixOptions.UserInputHandler` (default `null`, nothing changes) routes Reasonix `ask` questions to the host one at a time; see **Reasonix interactive questions** above for the mapping, the single-choice and no-free-text limits and the decline behavior. Question transcripts are recorded under `tests/HagiCode.Libs.Providers.Tests/Fixtures/Reasonix/v2.41.0/ask-*.jsonl`.
- Reasonix verification status: `reject_once` is verified (fixture `permission-reject-once`): the rejected tool update is `failed` and the turn still ends with `end_turn`. `agent_thought_chunk` is **unverified** against Reasonix 2.41.0: no reasoning chunk was observed for `claude-haiku-5-5` or `claude-opus-5` in ACP transcripts, so the assistant-thought mapping is covered by unit tests only. Image prompts are **unverified**: `promptCapabilities.image` is advertised, but the Reasonix provider sends text prompts only.
- Kiro discovery is strict: the shared provider only supports `kiro-cli` executables. Generic `kiro` launchers are not accepted for implicit discovery, explicit `ExecutablePath`, or readiness checks.
- `CliInstallRegistry` now treats DeepAgents as local-only validation metadata because the managed runtime expects a `deepagents` executable (or `uvx --from deepagents-cli deepagents --acp`) rather than the legacy `deepagents-acp` npm package.
