# dsh ACP capability probe

Probe of `dsh 0.2.0-rc.2` (`@deepseek-ai/dsh`) run on 2026-10-10 against an empty scratch `DSH_HOME` (no credentials). No prompts were sent, so no model tokens were spent.

| Item | Observed |
| --- | --- |
| Version | `dsh --version` prints `0.2.0-rc.2` only. |
| Identity | `dsh --help` prints the banner `dsh: boot a DeepSeek Harness profile` on stdout line 4, not line 1. ACP `initialize` returns `agentInfo.name = "deepseek-harness-acp"`. |
| Auth methods | `authMethods: []`. dsh owns credentials in `$DSH_HOME`. |
| Session restore | `agentCapabilities.sessionCapabilities.resume` is advertised. `loadSession` is not. `session/load` returns Method not found. `session/resume` with an existing id succeeds in a fresh process. An unknown id returns "session is not resumable". |
| Session id | `session/new` returns a top-level `sessionId`. |
| Session list | `session/list` returns `sessionId` and `cwd` per session. |
| Model selection | `session/set_model` returns Method not found. `session/set_config_option` with `configId: "model"` accepts the advertised value. Advertised values are JSON-encoded `["group","name"]` strings, for example `["deepseek-official","deepseek-v4-flash"]`. The bare display name `deepseek-v4-flash` is rejected. An empty home advertises DeepSeek models only (display names `deepseek-v4-flash`, `DeepSeek-V41-Flash`, `DeepSeek-V4-Pro`). `claude-haiku-5-5` is rejected with "unknown model option". |

## Consequences for HagiCode.Libs

- Session restore uses `session/resume` by explicit id, not the shared client's `session/load`. `DshProvider` starts sessions through its own wrapper over `IAcpSessionClient` so the shared ACP runtime stays unchanged.
- Model selection uses `session/set_config_option` (configId `model`) instead of `session/set_model`. `DshOptions.Provider` is paired with `DshOptions.Model`, which is forwarded unsplit. `DshAcpSessionClient` sends the advertised `["<provider>","<model>"]` value.
- Ping identity uses the `--help` banner, searched across all stdout. `--version` alone cannot distinguish dsh from a distributed-shell `dsh`.
- `claude-haiku-5-5` must be selectable in the dedicated test `DSH_HOME` before the real-run gate can pass. The empty home does not offer it, so its advertised value is still unknown.
