// verified except held-steer resume (provisional until P4, P6 and P7 are complete)
namespace HagiCode.Libs.Providers.Tests;

// Real CodeBuddy 2.169.0 captures for codebuddy-run-steering (tasks 1.1 to 1.9). The fixtures in Fixtures/Codebuddy are sanitized:
// session, request and trace ids are replaced by stable placeholders and the scratch working directory by <work>. Captured
// 2026-10-26 with model hy3 (free tier) through a throwaway Node harness outside the repositories (acp-probe.mjs, sj-probe.mjs,
// acp-cancel-probe.mjs, acp-edge-idle.mjs, acp-after-turn.mjs). Command lines: `codebuddy --acp -y --no-session-persistence
// --model hy3` (ACP) and `codebuddy -p --input-format stream-json --output-format stream-json --verbose -y
// --no-session-persistence --model hy3` (stream-json, with and without --replay-user-messages).
//
// Branch A (steer over ACP) is selected by the decision tree in design.md: the steer
// is reachable while session/prompt is pending, and the injection is observable. The one case not recorded is a held steer after
// the process is torn down and the session resumed, because --no-session-persistence stores nothing to resume.
//
// Findings (P1 to P7), each row with the fixture that shows it:
//  * P1 ACP reachability: session/steer is accepted while session/prompt is pending. During a bash tool call it answers
//    { steered: true, ownerRequestId } (steer-acp-tool-call.jsonl, id 4). Requests are answered concurrently; the prompt
//    reply comes later with its own id.
//  * P2 stream-json: a control_request { request_id, request: { subtype: "steer", session_id, content_blocks } } is answered by
//    control_response { subtype: "success", request_id, response: { session_id, steered: true } } (steer-stream-json-replay.jsonl).
//  * P3 consumption, ACP: no injection frame is sent unless session/steer carries clientUserMessageId. With it, the injection
//    arrives as session/update user_message_chunk, text equal to the steer, codebuddy.ai/messageId equal to the id, right after
//    the tool_call_update completed and before the next model output (steer-acp-tool-call.jsonl). Without it, no such frame
//    appears, although the model still acts on the steer (steer-acp-tool-call-no-client-id.jsonl).
//  * P3 consumption, stream-json: the injection is echoed as a user frame with isReplay true and the steer text only with
//    --replay-user-messages (steer-stream-json-replay.jsonl, after the tool_result). Without the flag there is no echo
//    (steer-stream-json-no-replay.jsonl). The echo uuid is not the request id, so match by text.
//  * P3 injection point: at a tool boundary (tool call) and at the next model request boundary during plain text generation
//    (steer-acp-text-stream.jsonl, injected at the boundary after the first model response; the turn then finished normally).
//  * P4 window: an idle session answers { steered: false, reason: "idle" } to a steer, both before any turn and after a turn's
//    last result with the process still up (steer-acp-idle-edges.jsonl). An unknown session answers JSON-RPC -32602.
//    Not recorded: a steer in the instant between the last model output and idle, and the moment just before stdin closes.
//  * P5 teardown: a steer accepted during a tool call, then session/cancel, produced no user_message_chunk, and the turn answered
//    stopReason cancelled. The next prompt in the same process answered "OK" only, so the held text did not run later
//    (steer-acp-cancel-held.jsonl). Esc is a TUI key with no ACP equivalent; session/cancel is the teardown recorded here.
//    Resume across processes is not recorded.
//  * P6 edge inputs on an idle session (no model turn): empty contentBlocks is JSON-RPC -32602 "Missing or empty contentBlocks
//    array"; empty text, missing text, non-string text, whitespace-only text and a leading "/" command each answer
//    { steered: false } with no reason, before the run-state check; valid text answers reason "idle".
//    Not recorded (need a live turn): two identical texts, a steer equal to the prompt, a steer while a permission request is pending.
//  * P7 version: `codebuddy --version` prints 2.169.0. The initialize result has no agentInfo.version field, so the version gate
//    cannot read it from ACP without an extra process. The lowest working version is not recorded: only 2.169.0 was probed.
