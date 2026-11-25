# Antigravity CLI feature matrix

Records, for every `AgentCliFeatureCatalog` feature, what the Antigravity CLI (`agy`) actually does and the test that proves it. The backend preset may declare a feature only when its row here is **supported** or **partial** with a passing proving test (design D1a). A feature that is **not supported**, or still **pending**, stays undeclared.

- CLI version verified against: `agy 1.5.17` (`agy --version`), Linux, `~/.local/bin/agy`.
- Transport: ACP over stdio, `agy acp --stdio` (see the Antigravity CLI probe in `src/HagiCode.Libs.Providers/README.md`).
- Sign-in state of the verifying machine: **not signed in**. `session/new` returns JSON-RPC `-32000` "Authentication required", and `agy models` asks for sign-in.

## Blocker

Every verdict below needs a real session. Real sessions need a Google sign-in (`agy acp --login`, interactive) or another sign-in method that the CLI advertises. Until the user signs in on the verifying machine, all eight rows stay **pending**, and the 2A tests stay unticked. A pending row is not a negative verdict.

## Matrix

| Catalog key | Verdict | Evidence so far (from the probe, not a session) | Proving test (planned) | Status |
| --- | --- | --- | --- | --- |
| `chat` | pending | `initialize` answers; `session/new` is refused while not signed in. No prompt has run. | `AntigravityChatFeatureTests` (2A.1) | blocked on sign-in |
| `image` | pending | `initialize` advertises `promptCapabilities.image: true`. Whether the pixels reach the model, or only a path, is not known. | `AntigravityImageFeatureTests` (2A.2) | blocked on sign-in |
| `resume` | pending | `initialize` advertises `agentCapabilities.loadSession: true` and `sessionCapabilities.resume`. Continuation and the unknown-id error are not verified. | `AntigravityResumeFeatureTests` (2A.3) | blocked on sign-in |
| `ask_user_question` | pending | Nothing advertised about an ask or question method. Not probed. | `AntigravityAskUserQuestionFeatureTests` (2A.4) | blocked on sign-in |
| `built_in_skills` | pending | Nothing probed. The loading mechanism is unknown. | `AntigravityBuiltInSkillsFeatureTests` (2A.5) | blocked on sign-in |
| `steer` | pending | Nothing probed. | `AntigravitySteerFeatureTests` (2A.6) | blocked on sign-in |
| `magpie` | pending | Requirements not yet written down (2A.7). | `AntigravityMagpieFeatureTests` (2A.7) | blocked on sign-in |
| `task_agent_family` | pending | Implicit feature. Its requirement is not yet checked against the CLI (2A.7). | matrix entry plus `AntigravityMagpieFeatureTests` (2A.7) | blocked on sign-in |

## Behaviour checks the features depend on (2A.8)

| Behaviour | Status | Notes |
| --- | --- | --- |
| Tool-call event shape | not checked | Libs uses the synthetic fixture `Fixtures/ToolCalls/antigravity/tool-turn.jsonl`, which is not recorded from `agy`. |
| Permission prompts | not checked | `--dangerously-skip-permissions` is an `agy acp` flag. Its behaviour without that flag is unknown. |
| Working-directory handling | not checked | `session/new` takes `cwd`. Whether `agy` honours it has not been seen in a session. |
| Exit codes on failure | not checked | Failure path observed only as the JSON-RPC error above, not as a process exit code. |

Gaps found so far: none. The matrix is incomplete, so no gap list can be final.

## Update rule

When a real run is recorded, replace the pending row with the verdict, the evidence (session transcript path or fixture name), and the test that now passes. Record the `agy` version for each verdict, and rerun the real-CLI tests whenever the version changes.
