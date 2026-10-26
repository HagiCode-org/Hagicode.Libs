namespace HagiCode.Libs.Providers.Tests;

// Real omp 18.10.4 --mode rpc-ui captures for openspec change omp-steer-support (tasks 1.1 to 1.8). Captured 2026-10-26 through a
// stdin/stdout harness: `omp --mode rpc-ui --no-session --model omniroute/claude/claude-haiku-5-5`, a clean environment (HOME and
// PATH only) and a scratch working directory outside the home directory. One haiku call per run; set_ask_dialog was enabled the
// way the provider enables it. Fixtures: Fixtures/Omp/steer-*.jsonl, the stdout frames of each run in order. Sanitized: session id,
// tool call ids and dialog ids replaced by stable names, probe request ids replaced by the ids OmpRpcProtocol writes
// (hagicode-prompt, hagicode-get-state, hagicode-set-ask-dialog, hagicode-set-interrupt-mode, hagicode-steer-<n>), the model
// renamed to mock-model and the provider to mock, host paths replaced. Otherwise the frames are as omp wrote them, including the
// message_update deltas and the available_commands_update and setWidget noise that the parser must ignore.
//
// GATE: verified, with two conditions the provider must keep. Tasks 3 and 5 apply; the disproved outcome (task 8) does not.
//  (a) Interrupt mode "wait" must be set (set_interrupt_mode) whenever a steering handle is attached. The default "immediate"
//      backgrounds a running bash tool when a steer arrives (P3).
//  (b) The accepting window closes at the yielding agent_end (prompt_result with agentInvoked true). A steer after it is refused and
//      never written. omp would still take such a steer (P4), so the refusal is the provider's job, not omp's.
//
// P1 Reply. A steer is answered with { id: "hagicode-steer-<n>", type: "response", command: "steer", success: true } and no data
//    field. A bad steer is answered success false with an error text (P6). The reply comes in about 1 ms mid-run and 11 to 18 ms
//    when sent right after a settle (steer-after-settled, steer-after-agent-end-background). A queue_update { steering: [...] }
//    lists the text just before the reply.
// P2 Consumption. A user message_start (and message_end) whose message has "steering": true and one text block equal to the steer
//    text is the signal. It starts the next turn (turn_start), after turn_end of the turn that was running, never inside it. A
//    queue_update that drops the text comes right before it, but the drop alone is not used. The run's own prompt echo has no
//    steering flag. Steers are delivered one per turn (steeringMode one-at-a-time).
// P3 Tool calls. get_state reports interruptMode "immediate" by default. A steer then backgrounds the running bash tool:
//    tool_execution_end carries "Backgrounded early to handle an incoming message; the command keeps running", the steered turn
//    starts while the command still runs, and the real output arrives later as a custom async-result message (steer-tool-call).
//    After set_interrupt_mode "wait" (answered success) the tool finishes with its real result (TOOLDONE, 8.37 s wall time) and
//    the steered turn starts only after tool_execution_end (steer-tool-call-wait). The steer reply is immediate in both modes.
// P4 Settling. prompt_result { sessionSettled: true } is followed at once by session_settled. A steer sent after session_settled
//    is answered success true and starts a new run at once (agent_start, then the steering user message; steer-after-settled).
//    With background work pending (prompt_result { sessionSettled: false }), a steer sent after the yield is also accepted and
//    consumed in a new run at once, not after the background job; the background result then wakes a turn and session_settled
//    follows (steer-after-agent-end-background). Closing stdin during a run (steer-eof): the steer was answered success true, the
//    turn was cut off, no steering message and no session_settled followed, and omp exited 0. An accepted steer still open at exit
//    is never consumed.
// P5 Open ask. With an ask dialog open (extension_ui_request, method "ask") the steer is answered success true. The dialog stays
//    open: no cancel frame, no change. The steer is consumed only after the answer's tool result ("User selected: Blue"), as a
//    steering user message, and the ask tool is not backgrounded by it (steer-ask-open).
// P6 Edge texts (steer-edge-cases, one run, steers sent while streaming):
//    - "" is answered success true but dropped: it never appears in a queue_update and is never injected. The provider refuses
//      blank text before any write.
//    - a missing message is answered success false, error "undefined is not an object (evaluating 'R.trim')"; a numeric message
//      42 is answered success false, error "R.trim is not a function. (In 'R.trim()', 'R.trim' is undefined)". Both are NotSteerable.
//    - two steers with the same id ("dup") are both answered success true and both injected, so omp does not reject a duplicate
//      id; the provider refuses the second one itself.
//    - two steers with identical text ("SAME-TEXT") are both queued and injected in order, one turn each (FIFO).
//    - a steer equal to the prompt text is queued and injected as a steering message; the flag tells it apart from the prompt echo.
//    - "/help" is injected unchanged: no rewrite, no command run.
//    Not observed: an injection that arrives before its reply. In every capture the reply came first, so the tracker's
//    injection-before-reply state is kept for safety, not for a recorded case.
internal static class OmpRpcCapture
{
    /// <summary>The steer captures in Fixtures/Omp. Each file is the stdout of one run, in order.</summary>
    public static IReadOnlyList<string> SteerFixtureNames { get; } =
    [
        "steer-baseline-no-steer",
        "steer-text-stream",
        "steer-tool-call",
        "steer-tool-call-wait",
        "steer-after-agent-end-background",
        "steer-after-settled",
        "steer-eof",
        "steer-ask-open",
        "steer-edge-cases"
    ];

    /// <summary>Reads one capture as its non-empty stdout lines.</summary>
    public static IReadOnlyList<string> ReadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Omp", name + ".jsonl");
        return File.ReadAllLines(path).Where(static line => line.Length > 0).ToArray();
    }
}
