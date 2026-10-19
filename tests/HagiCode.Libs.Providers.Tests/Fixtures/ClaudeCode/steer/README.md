# Claude CLI steer fixtures

Captured with Claude Code CLI **2.1.303** (model `haiku`, `--input-format stream-json --output-format stream-json --verbose
--replay-user-messages --permission-mode bypassPermissions`) in a scratch working directory that is not a developer session.

Each `*.jsonl` file is the CLI's stdout, one message per line, with strings longer than 300 characters trimmed and the
home directory replaced by `/home/fixture-user`. Each `*.stdin.json` file lists the lines the probe wrote to stdin, with
the time offset (seconds) at which each was written, so the order of host writes and CLI output can be read side by side.

## What the captures show

| Capture | Steer written | What the CLI did |
| --- | --- | --- |
| `bash-steer-during-tool` | While a background `Bash` command was running, after its `tool_use` | The steer was injected after the `tool_result` of the launching call, in the same turn. Its echo is a `user` message with `isReplay: true` and the steer's own `uuid`. The echo arrived before the turn's `result`. |
| `text-steer-during-generation` | Before the prompt replay, while the first turn was generating text, with no tool call | The first turn finished without the steer. The steer was echoed only after the next `system/init`, in a second turn of its own. The echo arrived after the first `result`. |
| `steer-after-result` | Written at the same moment as the `result` of a one-word turn, with stdin still open | The CLI started a new turn (`system/init`) for the steer, echoed it with `isReplay: true` and its `uuid`, and produced a second `result`. |

## Facts the tracker relies on

- **The echo carries the steer's `uuid`.** The tracker matches a steer echo by `uuid`. The text fallback in design D3 is
  only a safety net for a CLI that drops the field.
- **The echo does not carry `priority`.** The `priority: "next"` written on stdin is not visible on the echo. It is
  accepted (the CLI ran the steer) but cannot be confirmed from the output, so the tracker never reads priority back.
- **A prompt echo has a CLI-generated `uuid` too.** The host prompt has no `uuid` of its own, and the CLI assigns one to
  its replay. A steer echo therefore never collides with the prompt echo, and `isReplay` alone cannot tell them apart;
  the steer is identified by its own `uuid`.
- **A steer written during plain text generation is consumed in a follow-up turn.** The first `result` arrives with the
  steer still unconsumed, so the run must stay open until the echo and the follow-up turn's `result`. This is the case
  `PendingWait.SteerDelivery` covers.
- **A steer written after a `result` starts a new turn** while stdin is open. A run that has no pending steer still ends at
  its `result`, as before.
- **A background `Bash` command can finish during the steer's turn.** The `task_notification` and a further `system/init`
  appear after the first `result` in the tool capture. The existing notification logic handles that, and it does not
  consume the steer.

Related: the design is `openspec/changes/steer-active-claude-cli-runs/design.md` (D3, D5, D6).
