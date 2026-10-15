# Reasonix v2.41.0 ACP transcripts

Redacted ACP transcripts recorded from the installed `reasonix` 2.41.0 binary (`reasonix acp --model omniroute/cc/claude-haiku-4-5-20251001`) in a scratch working directory with a scrubbed environment. Each line is `{"dir":"in"|"out","msg":{...}}`. Redaction: working directory `<WORKDIR>`, home `<HOME>`, session id `fixture-session-01`, transcript path `fixture-transcript.jsonl`, command descriptions `placeholder`. `ReasonixFixtureHygieneTests` checks these rules.

## Question transcripts (`ask-*`)

| File | What it records |
| --- | --- |
| `ask-single` | One question, answered `q1:2`. The turn ends with `end_turn`. |
| `ask-two-questions` | Two questions in one `ask` call. Question 2's `session/request_permission` is sent only after question 1 is answered. Answers `q1:1` and `q2:2`. |
| `ask-multi` | `multi: true` in `rawInput`. The options are still single-pick (`q1:1`, `q1:2`, `q1:cancel`), so the answer is one `optionId`. |
| `ask-reject-option` | Answered `q1:cancel` (`reject_once`, name `Cancel`). The `ask` tool call fails with `ask: context canceled` and the prompt ends with `stopReason: "error"`. |
| `ask-cancelled-outcome` | Answered `{outcome:"cancelled"}`. Same result as `ask-reject-option`: failed tool call, `stopReason: "error"`. |
| `ask-two-turns` | Two prompts in one session, one question each. Ask ids are `ask-<hex>-1-q1`, then `ask-<hex>-2-q1`. |
| `ask-delayed-answer` | The answer is sent 120 seconds after the request. Reasonix still accepts it: `The user answered: - Color: Blue`, `end_turn`. |
| `ask-stop-pending` | `session/cancel` is sent while the question is pending, then the request is answered `cancelled`. The prompt ends with `stopReason: "cancelled"`. |
| `ask-resume-pending-part1` | First process. The question is left unanswered, then the process is stopped. |
| `ask-resume-pending` | Second process. `session/resume` returns the session with no history replay. The next prompt ends with `end_turn`. The unanswered `ask` is not sent again as a permission request. |

## Facts settled from these transcripts

- **Decline outcome (design D5):** `q1:cancel` and `cancelled` behave the same. Each fails the `ask` call with `ask: context canceled` and ends the turn with `stopReason: "error"`. Reasonix does not continue the turn after a decline. The provider keeps `q1:cancel` as the first choice and normalizes the error stop reason after a decline (D6).
- **Timeout:** no timeout was seen for a 120-second wait. The run was not waited for 30 minutes, so the Libs default of 30 minutes is still the only limit.
- **Question ids (design D8):** the id is `ask-<hex>-<turn>-qN`. Inside one session the turn number increases, so ids do not repeat there (`ask-two-turns`). A new question after `session/resume` is not captured yet.
- **Raw input keys:** the option keys are `Label` and `Description`, with `Description` set to an empty string when none is given. The `header` is not in `rawInput`. It is the text of the request's `toolCall.content`.
- **Tool call id:** the `tool_call` update uses a `toolu_...` id that differs from the permission request's `ask-...` id. Only the `title: "ask"` on the tool call identifies the card to hide.
- **Stop with a pending question:** `session/cancel` answered with `cancelled` gives `stopReason: "cancelled"`, not `error`.
- **Resume:** a session that holds an unanswered `ask` resumes without error, and the next prompt completes normally.
