using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Tests;

// Real Claude Code stream-json captures (sanitized: session ids, uuids and output paths replaced; assistant messages cut
// down to their tool_use block; unrelated system messages dropped) used to pin the shape of task events that the agent
// work snapshot is built from. Captured 2026-10-11 against CLI 2.1.292 (subagent, failed command, resume) and 2.1.296
// (Monitor, timed and unbounded background Bash) through a stream-json harness started with --replay-user-messages.
//
// Findings recorded for openspec change agent-work-snapshot-contract (tasks 1.2 and 1.3):
//  * local_agent "task_started" carries tool_use_id, description, subagent_type, prompt, run_id, is_backgrounded and
//    spawn_depth, so the subagent title is available from the start event itself. The launching "Agent" tool call input
//    (description, prompt, subagent_type, run_in_background when set) is the documented fallback.
//  * "task_notification" status vocabulary seen on the wire: completed, failed (command exited non-zero), stopped
//    (timed background Bash reaching its time limit; a stopped notification for a task of an earlier session on resume).
//    "task_updated" patch.status additionally uses "killed", which the notification reports as "stopped". The summary is
//    free text. A "usage" object { total_tokens, tool_uses, duration_ms } is present on local_agent notifications only.
//  * "task_progress" carries a running usage and a last_tool_name and fires on every tool call; it is not an item state
//    change and is ignored by the snapshot.
//  * A subagent that ends its turn while its own background command runs is reported "completed"; when the command
//    finishes the SAME task_id starts again ("task_started" with a new run_id) and completes later. An item must therefore
//    reopen when a terminal task id starts again.
//  * "background_tasks_changed" precedes the "task_started" of the task it lists and has no tool_use_id.
//  * A subagent requested without run_in_background still emits task events, as a backgrounded local_agent.
//  * Resuming a session replays only "task_notification" (status stopped) for tasks that were running when the old CLI
//    died. No "task_started" or "background_tasks_changed" is replayed, so ignoring notifications of unknown task ids is
//    enough to keep ghost items out and no extra rule is needed.
internal static class ClaudeAgentWorkCapture
{
    public static IReadOnlyList<CliMessage> Parse(string jsonLines)
    {
        var messages = new List<CliMessage>();
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var content = JsonDocument.Parse(line).RootElement.Clone();
            messages.Add(new CliMessage(content.GetProperty("type").GetString()!, content));
        }

        return messages;
    }

    /// <summary>Capture: BackgroundSubagentWithOwnedBash.</summary>
    public static IReadOnlyList<CliMessage> BackgroundSubagentWithOwnedBash => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01EYYedyRE8K6XRMQCWbW8Qx","name":"Agent","input":{"description":"Count files and wait","prompt":"Run the Bash command `ls | wc -l` in the foreground. Then start ONE Bash command with run_in_background true and timeout 30000 whose command is `sleep 12; echo SUB-DONE`, end your turn, and when it finishes report the word SUB-DONE.","subagent_type":"general-purpose","run_in_background":true}}]},"session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"a94d8c6601245aedc","run_id":"0mv4cgt25-4552279d","task_type":"local_agent","subagent_type":"general-purpose","description":"Count files and wait"}],"session_id":"fixture-session"}
{"type":"system","subtype":"task_started","task_id":"a94d8c6601245aedc","run_id":"0mv4cgt25-4552279d","tool_use_id":"toolu_01EYYedyRE8K6XRMQCWbW8Qx","description":"Count files and wait","subagent_type":"general-purpose","is_backgrounded":true,"spawn_depth":1,"task_type":"local_agent","prompt":"Run the Bash command `ls | wc -l` in the foreground. Then start ONE Bash command with run_in_background true and timeout 30000 whose command is `sleep 12; echo SUB-DONE`, end your turn, and when it finishes report the word SUB-DONE.","session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01FSAjsVt4e12RYZuSVFf2gV","name":"Bash","input":{"command":"ls | wc -l","description":"Count entries in current directory"}}]},"session_id":"fixture-session"}
{"type":"system","subtype":"task_progress","task_id":"a94d8c6601245aedc","run_id":"0mv4cgt25-4552279d","tool_use_id":"toolu_01EYYedyRE8K6XRMQCWbW8Qx","description":"Running Count entries in current directory","subagent_type":"general-purpose","usage":{"total_tokens":12564,"tool_uses":1,"duration_ms":997},"last_tool_name":"Bash","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_012SZ72v8KMwqexm5Smkxmoh","name":"Bash","input":{"command":"sleep 12; echo SUB-DONE","description":"Sleep 12 seconds then echo SUB-DONE","timeout":30000,"run_in_background":true}}]},"session_id":"fixture-session"}
{"type":"system","subtype":"task_progress","task_id":"a94d8c6601245aedc","run_id":"0mv4cgt25-4552279d","tool_use_id":"toolu_01EYYedyRE8K6XRMQCWbW8Qx","description":"Running Sleep 12 seconds then echo SUB-DONE","subagent_type":"general-purpose","usage":{"total_tokens":12627,"tool_uses":2,"duration_ms":1522},"last_tool_name":"Bash","session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"a94d8c6601245aedc","run_id":"0mv4cgt25-4552279d","task_type":"local_agent","subagent_type":"general-purpose","description":"Count files and wait"},{"task_id":"bmn7ig1yk","run_id":"0mv4cguy6-636022c8","task_type":"local_bash","description":"Sleep 12 seconds then echo SUB-DONE","parent_task_id":"a94d8c6601245aedc"}],"session_id":"fixture-session"}
{"type":"system","subtype":"task_started","task_id":"bmn7ig1yk","run_id":"0mv4cguy6-636022c8","owned_by_subagent":true,"tool_use_id":"toolu_012SZ72v8KMwqexm5Smkxmoh","description":"Sleep 12 seconds then echo SUB-DONE","is_backgrounded":true,"parent_task_id":"a94d8c6601245aedc","task_type":"local_bash","session_id":"fixture-session"}
{"type":"system","subtype":"task_updated","task_id":"a94d8c6601245aedc","run_id":"0mv4cgt25-4552279d","patch":{"status":"completed","end_time":1791754672050},"session_id":"fixture-session"}
{"type":"system","subtype":"task_notification","task_id":"a94d8c6601245aedc","run_id":"0mv4cgt25-4552279d","tool_use_id":"toolu_01EYYedyRE8K6XRMQCWbW8Qx","status":"completed","output_file":"/tmp/fixture/tasks/a94d8c6601245aedc.output","summary":"The foreground `ls | wc -l` returned `0`. The background command (`sleep 12; echo SUB-DONE`) is running, and I'll report SUB-DONE when it completes. I'm ending my turn now as requested.","usage":{"total_tokens":15192,"tool_uses":2,"duration_ms":5288},"session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"bmn7ig1yk","run_id":"0mv4cguy6-636022c8","task_type":"local_bash","description":"Sleep 12 seconds then echo SUB-DONE","parent_task_id":"a94d8c6601245aedc"}],"session_id":"fixture-session"}
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
{"type":"system","subtype":"task_updated","task_id":"bmn7ig1yk","run_id":"0mv4cguy6-636022c8","patch":{"status":"completed","end_time":1791754681805},"session_id":"fixture-session"}
{"type":"system","subtype":"task_notification","task_id":"bmn7ig1yk","run_id":"0mv4cguy6-636022c8","tool_use_id":"toolu_012SZ72v8KMwqexm5Smkxmoh","status":"completed","output_file":"/tmp/fixture/tasks/bmn7ig1yk.output","summary":"Background command \"Sleep 12 seconds then echo SUB-DONE\" completed (exit code 0)","session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[],"session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"a94d8c6601245aedc","run_id":"0mv4ch4o6-a5245968","task_type":"local_agent","subagent_type":"general-purpose","description":"Count files and wait"}],"session_id":"fixture-session"}
{"type":"system","subtype":"task_started","task_id":"a94d8c6601245aedc","run_id":"0mv4ch4o6-a5245968","tool_use_id":"toolu_01EYYedyRE8K6XRMQCWbW8Qx","description":"Count files and wait","subagent_type":"general-purpose","is_backgrounded":true,"spawn_depth":1,"task_type":"local_agent","prompt":"<task-notification>\n<task-id>bmn7ig1yk</task-id>\n<tool-use-id>toolu_012SZ72v8KMwqexm5Smkxmoh</tool-use-id>\n<output-file>/tmp/fixture/tasks/bmn7ig1yk.output</output-file>\n<status>completed</status>\n<summary>Background command \"Sleep 12 seconds then echo SUB-DONE\" completed (exit code 0)</summary>\n</task-notification>","session_id":"fixture-session"}
{"type":"system","subtype":"task_updated","task_id":"a94d8c6601245aedc","run_id":"0mv4ch4o6-a5245968","patch":{"status":"completed","end_time":1791754682832},"session_id":"fixture-session"}
{"type":"system","subtype":"task_notification","task_id":"a94d8c6601245aedc","run_id":"0mv4ch4o6-a5245968","tool_use_id":"toolu_01EYYedyRE8K6XRMQCWbW8Qx","status":"completed","output_file":"/tmp/fixture/tasks/a94d8c6601245aedc.output","summary":"SUB-DONE","usage":{"total_tokens":16031,"tool_uses":2,"duration_ms":16051},"session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[],"session_id":"fixture-session"}
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
""");

    /// <summary>Capture: ForegroundRequestedSubagent.</summary>
    public static IReadOnlyList<CliMessage> ForegroundRequestedSubagent => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01UvNk4BGk53ScNoeMie9tZ3","name":"Agent","input":{"description":"Say hello","prompt":"Reply with the single word HELLO and nothing else","subagent_type":"general-purpose"}}]},"session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"ac8077dcfa89e58b2","run_id":"0mv4cgsmd-e2d08452","task_type":"local_agent","subagent_type":"general-purpose","description":"Say hello"}],"session_id":"fixture-session"}
{"type":"system","subtype":"task_started","task_id":"ac8077dcfa89e58b2","run_id":"0mv4cgsmd-e2d08452","tool_use_id":"toolu_01UvNk4BGk53ScNoeMie9tZ3","description":"Say hello","subagent_type":"general-purpose","is_backgrounded":true,"spawn_depth":1,"task_type":"local_agent","prompt":"Reply with the single word HELLO and nothing else","session_id":"fixture-session"}
{"type":"system","subtype":"task_updated","task_id":"ac8077dcfa89e58b2","run_id":"0mv4cgsmd-e2d08452","patch":{"status":"completed","end_time":1791754667293},"session_id":"fixture-session"}
{"type":"system","subtype":"task_notification","task_id":"ac8077dcfa89e58b2","run_id":"0mv4cgsmd-e2d08452","tool_use_id":"toolu_01UvNk4BGk53ScNoeMie9tZ3","status":"completed","output_file":"/tmp/fixture/tasks/ac8077dcfa89e58b2.output","summary":"HELLO","usage":{"total_tokens":12473,"tool_uses":0,"duration_ms":1091},"session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[],"session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
""");

    /// <summary>Capture: MonitorToolCall.</summary>
    public static IReadOnlyList<CliMessage> MonitorToolCall => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01PkELACvXbijRydYU75Gx1y","name":"Monitor","input":{"description":"wait test","timeout_ms":120000,"command":"sleep 20; echo EVENT-ONE; sleep 10; echo EVENT-TWO"}}]},"session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"bafzsuvsb","run_id":"0mv452a8y-433be6a8","task_type":"local_bash","description":"wait test"}],"session_id":"fixture-session"}
{"type":"system","subtype":"task_started","task_id":"bafzsuvsb","run_id":"0mv452a8y-433be6a8","tool_use_id":"toolu_01PkELACvXbijRydYU75Gx1y","description":"wait test","is_backgrounded":true,"task_type":"local_bash","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
{"type":"system","subtype":"task_updated","task_id":"bafzsuvsb","run_id":"0mv452a8y-433be6a8","patch":{"status":"completed","end_time":1791742262128},"session_id":"fixture-session"}
{"type":"system","subtype":"task_notification","task_id":"bafzsuvsb","run_id":"0mv452a8y-433be6a8","tool_use_id":"toolu_01PkELACvXbijRydYU75Gx1y","status":"completed","output_file":"/tmp/fixture/tasks/bafzsuvsb.output","summary":"Monitor \"wait test\" stream ended","session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[],"session_id":"fixture-session"}
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
""");

    /// <summary>Capture: TimedBackgroundBashStopped.</summary>
    public static IReadOnlyList<CliMessage> TimedBackgroundBashStopped => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01LvsspGQwzhsbodt2uVC2Ca","name":"Bash","input":{"command":"sleep 40; echo DONE-BG","description":"Sleep 40 seconds then echo marker in background","timeout":8000,"run_in_background":true}}]},"session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"bochdwsg7","run_id":"0mv45m537-a01de8a2","task_type":"local_bash","description":"Sleep 40 seconds then echo marker in background"}],"session_id":"fixture-session"}
{"type":"system","subtype":"task_started","task_id":"bochdwsg7","run_id":"0mv45m537-a01de8a2","tool_use_id":"toolu_01LvsspGQwzhsbodt2uVC2Ca","description":"Sleep 40 seconds then echo marker in background","is_backgrounded":true,"task_type":"local_bash","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
{"type":"system","subtype":"task_updated","task_id":"bochdwsg7","run_id":"0mv45m537-a01de8a2","patch":{"status":"killed","end_time":1791743166341},"session_id":"fixture-session"}
{"type":"system","subtype":"task_notification","task_id":"bochdwsg7","run_id":"0mv45m537-a01de8a2","tool_use_id":"toolu_01LvsspGQwzhsbodt2uVC2Ca","status":"stopped","output_file":"/tmp/fixture/tasks/bochdwsg7.output","summary":"Background command \"Sleep 40 seconds then echo marker in background\" was stopped after reaching its background time limit","session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[],"session_id":"fixture-session"}
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
""");

    /// <summary>Capture: UnboundedBackgroundBash.</summary>
    public static IReadOnlyList<CliMessage> UnboundedBackgroundBash => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01K5HhP5N1ziCYAY3hpsFpsw","name":"Bash","input":{"command":"sleep 300; echo DONE-BG","description":"Sleep 300 seconds then echo DONE-BG","run_in_background":true}}]},"session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"b4caj0xb2","run_id":"0mv45m51j-9ac2a575","task_type":"local_bash","description":"Sleep 300 seconds then echo DONE-BG"}],"session_id":"fixture-session"}
{"type":"system","subtype":"task_started","task_id":"b4caj0xb2","run_id":"0mv45m51j-9ac2a575","tool_use_id":"toolu_01K5HhP5N1ziCYAY3hpsFpsw","description":"Sleep 300 seconds then echo DONE-BG","is_backgrounded":true,"task_type":"local_bash","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
""");

    /// <summary>Capture: FailedBackgroundBash.</summary>
    public static IReadOnlyList<CliMessage> FailedBackgroundBash => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_013KeYVYZGSPp1UPRZw9RPrn","name":"Bash","input":{"command":"sleep 3; echo BOOM; exit 3","description":"Run delayed echo command in background","timeout":20000,"run_in_background":true}}]},"session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"baaojhe3n","run_id":"0mv4civda-2618da33","task_type":"local_bash","description":"Run delayed echo command in background"}],"session_id":"fixture-session"}
{"type":"system","subtype":"task_started","task_id":"baaojhe3n","run_id":"0mv4civda-2618da33","tool_use_id":"toolu_013KeYVYZGSPp1UPRZw9RPrn","description":"Run delayed echo command in background","is_backgrounded":true,"task_type":"local_bash","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
{"type":"system","subtype":"task_updated","task_id":"baaojhe3n","run_id":"0mv4civda-2618da33","patch":{"status":"failed","end_time":1791754766551},"session_id":"fixture-session"}
{"type":"system","subtype":"task_notification","task_id":"baaojhe3n","run_id":"0mv4civda-2618da33","tool_use_id":"toolu_013KeYVYZGSPp1UPRZw9RPrn","status":"failed","output_file":"/tmp/fixture/tasks/baaojhe3n.output","summary":"Background command \"Run delayed echo command in background\" failed with exit code 3","session_id":"fixture-session"}
{"type":"system","subtype":"background_tasks_changed","tasks":[],"session_id":"fixture-session"}
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
""");

    /// <summary>Capture: ResumedSessionReplay.</summary>
    public static IReadOnlyList<CliMessage> ResumedSessionReplay => Parse("""
{"type":"system","subtype":"task_notification","task_id":"b4ed0pj4x","tool_use_id":"toolu_01KTGuVamwHRJbFEgf3Wn8WL","status":"stopped","output_file":"","summary":"Background shell command didn't finish before the previous session ended","session_id":"fixture-session"}
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
{"type":"system","subtype":"init","session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"(fixture turn result)","session_id":"fixture-session"}
""");
}
