using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Tests;

// Real Claude Code stream-json captures of the interactive question protocol (sanitized: session ids, request ids and tool
// ids replaced by stable fixtures; assistant messages cut down to their tool_use or text block; thinking, usage, uuids
// and timestamps dropped; the host's own writes kept apart in the *HostResponse members). Captured 2026-10-12 with
// --permission-prompt-tool stdio and --replay-user-messages, haiku, through a stdin/stdout harness. Two versions are
// pinned because they differ on the wire: 2.1.159 is the Claude CLI the desktop manifest ships today and 2.1.292 is the
// one on the development machine.
//
// Findings recorded for openspec change claude-cli-interactive-ask-user-question-support (task 1.2 and 1.3):
//  * Both versions accept --permission-prompt-tool stdio and register AskUserQuestion (system/init tools).
//  * The control_request of AskUserQuestion carries requires_user_interaction=true on 2.1.292 only. 2.1.159 sends the same
//    can_use_tool request without that member, so the tool name, not the flag, identifies a question. A permission request
//    for another tool (Write) carries no flag on either version.
//  * The control_request arrives right after the assistant message that holds the tool_use block, with the same tool_use_id.
//  * The CLI echoes the host's control_response on stdout, right after the request, as a top-level control_response
//    message (the host's own JSON, unchanged). It is not a replayed user message, so IsPromptReplay does not match it.
//  * A multi-select answer written as one string "Dark theme, Mobile layout, Custom thing" is accepted unchanged and the
//    tool_result repeats it. 2.1.159 words the tool_result "Your questions have been answered: ...", 2.1.292 "The user
//    answered: ...", so a test must not pin that wording.
//  * A deny with a message becomes a tool_result with is_error=true and the message as its content, and the terminal
//    result message lists the denied call (question or Write) under permission_denials. The run itself ends normally.
internal static class ClaudeUserQuestionCapture
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

    /// <summary>Capture V2159Single, CLI 2.1.159: Single-choice question answered with the second option. Messages the CLI wrote to stdout, in order.</summary>
    public static IReadOnlyList<CliMessage> V2159Single => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session","claude_code_version":"2.1.159"}
{"type":"user","message":{"role":"user","content":"Use the AskUserQuestion tool to ask me which colour I prefer: red or blue (2 options). After I answer, reply with exactly: ANSWER=<my choice>. Do not use any other tool."},"session_id":"fixture-session","isReplay":true}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu-fixture-1","name":"AskUserQuestion","input":{"questions":[{"question":"Which colour do you prefer?","header":"Colour","options":[{"label":"Red","description":"The color red"},{"label":"Blue","description":"The color blue"}],"multiSelect":false}]}}]},"session_id":"fixture-session"}
{"type":"control_request","request_id":"req-fixture-1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Which colour do you prefer?","header":"Colour","options":[{"label":"Red","description":"The color red"},{"label":"Blue","description":"The color blue"}],"multiSelect":false}]},"tool_use_id":"toolu-fixture-1"}}
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"allow","updatedInput":{"questions":[{"question":"Which colour do you prefer?","header":"Colour","options":[{"label":"Red","description":"The color red"},{"label":"Blue","description":"The color blue"}],"multiSelect":false}],"answers":{"Which colour do you prefer?":"Blue"}}}}}
{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"Your questions have been answered: \"Which colour do you prefer?\"=\"Blue\". You can now continue with these answers in mind.","tool_use_id":"toolu-fixture-1"}]},"session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"text","text":"ANSWER=Blue"}]},"session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"ANSWER=Blue","session_id":"fixture-session","permission_denials":[],"terminal_reason":"completed"}
""");

    /// <summary>The <c>control_response</c> the host wrote during capture V2159Single.</summary>
    public static CliMessage V2159SingleHostResponse => Parse("""
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"allow","updatedInput":{"questions":[{"question":"Which colour do you prefer?","header":"Colour","options":[{"label":"Red","description":"The color red"},{"label":"Blue","description":"The color blue"}],"multiSelect":false}],"answers":{"Which colour do you prefer?":"Blue"}}}}}
""")[0];

    /// <summary>Capture V2159Multi, CLI 2.1.159: Multi-select question answered with three values joined by a comma and a space (the third is free text). Messages the CLI wrote to stdout, in order.</summary>
    public static IReadOnlyList<CliMessage> V2159Multi => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session","claude_code_version":"2.1.159"}
{"type":"user","message":{"role":"user","content":"Use the AskUserQuestion tool exactly once with ONE question that has multiSelect true and three options: Dark theme, Mobile layout, Offline mode. Ask which features I want. After I answer, reply with exactly: ANSWER=<the answer string you received>. Do not use any other tool."},"session_id":"fixture-session","isReplay":true}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu-fixture-1","name":"AskUserQuestion","input":{"questions":[{"question":"Which features do you want?","header":"Features","multiSelect":true,"options":[{"label":"Dark theme","description":"Enable dark color scheme"},{"label":"Mobile layout","description":"Optimize for mobile devices"},{"label":"Offline mode","description":"Support offline functionality"}]}]}}]},"session_id":"fixture-session"}
{"type":"control_request","request_id":"req-fixture-1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Which features do you want?","header":"Features","options":[{"label":"Dark theme","description":"Enable dark color scheme"},{"label":"Mobile layout","description":"Optimize for mobile devices"},{"label":"Offline mode","description":"Support offline functionality"}],"multiSelect":true}]},"tool_use_id":"toolu-fixture-1"}}
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"allow","updatedInput":{"questions":[{"question":"Which features do you want?","header":"Features","options":[{"label":"Dark theme","description":"Enable dark color scheme"},{"label":"Mobile layout","description":"Optimize for mobile devices"},{"label":"Offline mode","description":"Support offline functionality"}],"multiSelect":true}],"answers":{"Which features do you want?":"Dark theme, Mobile layout, Custom thing"}}}}}
{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"Your questions have been answered: \"Which features do you want?\"=\"Dark theme, Mobile layout, Custom thing\". You can now continue with these answers in mind.","tool_use_id":"toolu-fixture-1"}]},"session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"text","text":"ANSWER=Dark theme, Mobile layout, Custom thing"}]},"session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"ANSWER=Dark theme, Mobile layout, Custom thing","session_id":"fixture-session","permission_denials":[],"terminal_reason":"completed"}
""");

    /// <summary>The <c>control_response</c> the host wrote during capture V2159Multi.</summary>
    public static CliMessage V2159MultiHostResponse => Parse("""
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"allow","updatedInput":{"questions":[{"question":"Which features do you want?","header":"Features","options":[{"label":"Dark theme","description":"Enable dark color scheme"},{"label":"Mobile layout","description":"Optimize for mobile devices"},{"label":"Offline mode","description":"Support offline functionality"}],"multiSelect":true}],"answers":{"Which features do you want?":"Dark theme, Mobile layout, Custom thing"}}}}}
""")[0];

    /// <summary>Capture V2159Denied, CLI 2.1.159: Question denied with a message. Messages the CLI wrote to stdout, in order.</summary>
    public static IReadOnlyList<CliMessage> V2159Denied => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session","claude_code_version":"2.1.159"}
{"type":"user","message":{"role":"user","content":"Use the AskUserQuestion tool to ask me which colour I prefer: red or blue (2 options). After I answer, reply with exactly: ANSWER=<my choice>. Do not use any other tool."},"session_id":"fixture-session","isReplay":true}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu-fixture-1","name":"AskUserQuestion","input":{"questions":[{"question":"Which color do you prefer?","header":"Color","options":[{"label":"Red","description":"A warm, vibrant color"},{"label":"Blue","description":"A cool, calming color"}],"multiSelect":false}]}}]},"session_id":"fixture-session"}
{"type":"control_request","request_id":"req-fixture-1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Which color do you prefer?","header":"Color","options":[{"label":"Red","description":"A warm, vibrant color"},{"label":"Blue","description":"A cool, calming color"}],"multiSelect":false}]},"tool_use_id":"toolu-fixture-1"}}
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"deny","message":"User declined to answer"}}}
{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"User declined to answer","is_error":true,"tool_use_id":"toolu-fixture-1"}]},"session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"text","text":"You declined to answer the question."}]},"session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"You declined to answer the question.","session_id":"fixture-session","permission_denials":[{"tool_name":"AskUserQuestion","tool_use_id":"toolu-fixture-1","tool_input":{"questions":[{"question":"Which color do you prefer?","header":"Color","options":[{"label":"Red","description":"A warm, vibrant color"},{"label":"Blue","description":"A cool, calming color"}],"multiSelect":false}]}}],"terminal_reason":"completed"}
""");

    /// <summary>The <c>control_response</c> the host wrote during capture V2159Denied.</summary>
    public static CliMessage V2159DeniedHostResponse => Parse("""
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"deny","message":"User declined to answer"}}}
""")[0];

    /// <summary>Capture V2159WriteDenied, CLI 2.1.159: A Write permission request denied by the host. Messages the CLI wrote to stdout, in order.</summary>
    public static IReadOnlyList<CliMessage> V2159WriteDenied => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session","claude_code_version":"2.1.159"}
{"type":"user","message":{"role":"user","content":"Create a file named hello.txt containing the text hi using the Write tool exactly once, then reply with exactly: DONE. Do not use any other tool."},"session_id":"fixture-session","isReplay":true}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu-fixture-1","name":"Write","input":{"file_path":"/tmp/claude-1000/-home-newbe36524-repos-hagi-hagicode-mono/534c8b96-c1c8-4069-9ad9-9bc955afef97/scratchpad/qa/work/hello.txt","content":"hi"}}]},"session_id":"fixture-session"}
{"type":"control_request","request_id":"req-fixture-1","request":{"subtype":"can_use_tool","tool_name":"Write","input":{"file_path":"/tmp/fixture/hello.txt","content":"hi"},"description":"hello.txt","permission_suggestions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}],"tool_use_id":"toolu-fixture-1"}}
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"deny","message":"User declined to answer"}}}
{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"User declined to answer","is_error":true,"tool_use_id":"toolu-fixture-1"}]},"session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"text","text":"The Write tool permission was denied, so I cannot complete the task as requested."}]},"session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"The Write tool permission was denied, so I cannot complete the task as requested.","session_id":"fixture-session","permission_denials":[{"tool_name":"Write","tool_use_id":"toolu-fixture-1","tool_input":{"file_path":"/tmp/fixture/hello.txt","content":"hi"}}],"terminal_reason":"completed"}
""");

    /// <summary>The <c>control_response</c> the host wrote during capture V2159WriteDenied.</summary>
    public static CliMessage V2159WriteDeniedHostResponse => Parse("""
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"deny","message":"User declined to answer"}}}
""")[0];

    /// <summary>Capture V2292Single, CLI 2.1.292: Single-choice question answered with the second option. Messages the CLI wrote to stdout, in order.</summary>
    public static IReadOnlyList<CliMessage> V2292Single => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session","claude_code_version":"2.1.292"}
{"type":"user","message":{"role":"user","content":"Use the AskUserQuestion tool to ask me which colour I prefer: red or blue (2 options). After I answer, reply with exactly: ANSWER=<my choice>. Do not use any other tool."},"session_id":"fixture-session","isReplay":true}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu-fixture-1","name":"AskUserQuestion","input":{"questions":[{"question":"Which colour do you prefer?","header":"Colour choice","multiSelect":false,"options":[{"label":"Red","description":"The colour red"},{"label":"Blue","description":"The colour blue"}]}]}}]},"session_id":"fixture-session"}
{"type":"control_request","request_id":"req-fixture-1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Which colour do you prefer?","header":"Colour choice","options":[{"label":"Red","description":"The colour red"},{"label":"Blue","description":"The colour blue"}],"multiSelect":false}]},"tool_use_id":"toolu-fixture-1","requires_user_interaction":true}}
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"allow","updatedInput":{"questions":[{"question":"Which colour do you prefer?","header":"Colour choice","options":[{"label":"Red","description":"The colour red"},{"label":"Blue","description":"The colour blue"}],"multiSelect":false}],"answers":{"Which colour do you prefer?":"Blue"}}}}}
{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"Your questions have been answered: \"Which colour do you prefer?\"=\"Blue\". You can now continue with these answers in mind.","tool_use_id":"toolu-fixture-1"}]},"session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"text","text":"ANSWER=Blue"}]},"session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"ANSWER=Blue","session_id":"fixture-session","permission_denials":[],"terminal_reason":"completed"}
""");

    /// <summary>The <c>control_response</c> the host wrote during capture V2292Single.</summary>
    public static CliMessage V2292SingleHostResponse => Parse("""
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"allow","updatedInput":{"questions":[{"question":"Which colour do you prefer?","header":"Colour choice","options":[{"label":"Red","description":"The colour red"},{"label":"Blue","description":"The colour blue"}],"multiSelect":false}],"answers":{"Which colour do you prefer?":"Blue"}}}}}
""")[0];

    /// <summary>Capture V2292Multi, CLI 2.1.292: Multi-select question answered with three values joined by a comma and a space (the third is free text). Messages the CLI wrote to stdout, in order.</summary>
    public static IReadOnlyList<CliMessage> V2292Multi => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session","claude_code_version":"2.1.292"}
{"type":"user","message":{"role":"user","content":"Use the AskUserQuestion tool exactly once with ONE question that has multiSelect true and three options: Dark theme, Mobile layout, Offline mode. Ask which features I want. After I answer, reply with exactly: ANSWER=<the answer string you received>. Do not use any other tool."},"session_id":"fixture-session","isReplay":true}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu-fixture-1","name":"AskUserQuestion","input":{"questions":[{"question":"Which features do you want?","header":"Features","multiSelect":true,"options":[{"label":"Dark theme","description":"Enable dark theme for the interface"},{"label":"Mobile layout","description":"Optimize layout for mobile devices"},{"label":"Offline mode","description":"Enable offline functionality"}]}]}}]},"session_id":"fixture-session"}
{"type":"control_request","request_id":"req-fixture-1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Which features do you want?","header":"Features","options":[{"label":"Dark theme","description":"Enable dark theme for the interface"},{"label":"Mobile layout","description":"Optimize layout for mobile devices"},{"label":"Offline mode","description":"Enable offline functionality"}],"multiSelect":true}]},"tool_use_id":"toolu-fixture-1","requires_user_interaction":true}}
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"allow","updatedInput":{"questions":[{"question":"Which features do you want?","header":"Features","options":[{"label":"Dark theme","description":"Enable dark theme for the interface"},{"label":"Mobile layout","description":"Optimize layout for mobile devices"},{"label":"Offline mode","description":"Enable offline functionality"}],"multiSelect":true}],"answers":{"Which features do you want?":"Dark theme, Mobile layout, Custom thing"}}}}}
{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"The user answered: \"Which features do you want?\"=\"Dark theme, Mobile layout, Custom thing\". Read the answers carefully — they may request clarification, changes, or that you not proceed — and follow what they actually say.","tool_use_id":"toolu-fixture-1"}]},"session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"text","text":"ANSWER=Dark theme, Mobile layout, Custom thing"}]},"session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"ANSWER=Dark theme, Mobile layout, Custom thing","session_id":"fixture-session","permission_denials":[],"terminal_reason":"completed"}
""");

    /// <summary>The <c>control_response</c> the host wrote during capture V2292Multi.</summary>
    public static CliMessage V2292MultiHostResponse => Parse("""
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"allow","updatedInput":{"questions":[{"question":"Which features do you want?","header":"Features","options":[{"label":"Dark theme","description":"Enable dark theme for the interface"},{"label":"Mobile layout","description":"Optimize layout for mobile devices"},{"label":"Offline mode","description":"Enable offline functionality"}],"multiSelect":true}],"answers":{"Which features do you want?":"Dark theme, Mobile layout, Custom thing"}}}}}
""")[0];

    /// <summary>Capture V2292Denied, CLI 2.1.292: Question denied with a message. Messages the CLI wrote to stdout, in order.</summary>
    public static IReadOnlyList<CliMessage> V2292Denied => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session","claude_code_version":"2.1.292"}
{"type":"user","message":{"role":"user","content":"Use the AskUserQuestion tool to ask me which colour I prefer: red or blue (2 options). After I answer, reply with exactly: ANSWER=<my choice>. Do not use any other tool."},"session_id":"fixture-session","isReplay":true}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu-fixture-1","name":"AskUserQuestion","input":{"questions":[{"question":"Which color do you prefer?","header":"Color","options":[{"label":"Red","description":"Warm, vibrant, and energetic"},{"label":"Blue","description":"Cool, calm, and serene"}],"multiSelect":false}]}}]},"session_id":"fixture-session"}
{"type":"control_request","request_id":"req-fixture-1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Which color do you prefer?","header":"Color","options":[{"label":"Red","description":"Warm, vibrant, and energetic"},{"label":"Blue","description":"Cool, calm, and serene"}],"multiSelect":false}]},"tool_use_id":"toolu-fixture-1","requires_user_interaction":true}}
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"deny","message":"User declined to answer"}}}
{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"User declined to answer","is_error":true,"tool_use_id":"toolu-fixture-1"}]},"session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"text","text":"You declined to answer. Please select either red or blue so I can provide your answer."}]},"session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"You declined to answer. Please select either red or blue so I can provide your answer.","session_id":"fixture-session","permission_denials":[{"tool_name":"AskUserQuestion","tool_use_id":"toolu-fixture-1","tool_input":{"questions":[{"question":"Which color do you prefer?","header":"Color","options":[{"label":"Red","description":"Warm, vibrant, and energetic"},{"label":"Blue","description":"Cool, calm, and serene"}],"multiSelect":false}]}}],"terminal_reason":"completed"}
""");

    /// <summary>The <c>control_response</c> the host wrote during capture V2292Denied.</summary>
    public static CliMessage V2292DeniedHostResponse => Parse("""
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"deny","message":"User declined to answer"}}}
""")[0];

    /// <summary>Capture V2292WriteDenied, CLI 2.1.292: A Write permission request denied by the host. Messages the CLI wrote to stdout, in order.</summary>
    public static IReadOnlyList<CliMessage> V2292WriteDenied => Parse("""
{"type":"system","subtype":"init","session_id":"fixture-session","claude_code_version":"2.1.292"}
{"type":"user","message":{"role":"user","content":"Create a file named hello.txt containing the text hi using the Write tool exactly once, then reply with exactly: DONE. Do not use any other tool."},"session_id":"fixture-session","isReplay":true}
{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu-fixture-1","name":"Write","input":{"file_path":"/tmp/claude-1000/-home-newbe36524-repos-hagi-hagicode-mono/534c8b96-c1c8-4069-9ad9-9bc955afef97/scratchpad/qa/work/hello.txt","content":"hi"}}]},"session_id":"fixture-session"}
{"type":"control_request","request_id":"req-fixture-1","request":{"subtype":"can_use_tool","tool_name":"Write","input":{"file_path":"/tmp/fixture/hello.txt","content":"hi"},"description":"hello.txt","permission_suggestions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}],"tool_use_id":"toolu-fixture-1"}}
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"deny","message":"User declined to answer"}}}
{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"User declined to answer","is_error":true,"tool_use_id":"toolu-fixture-1"}]},"session_id":"fixture-session"}
{"type":"assistant","message":{"content":[{"type":"text","text":"The Write tool was declined. I cannot complete the task as instructed."}]},"session_id":"fixture-session"}
{"type":"result","subtype":"success","is_error":false,"result":"The Write tool was declined. I cannot complete the task as instructed.","session_id":"fixture-session","permission_denials":[{"tool_name":"Write","tool_use_id":"toolu-fixture-1","tool_input":{"file_path":"/tmp/fixture/hello.txt","content":"hi"}}],"terminal_reason":"completed"}
""");

    /// <summary>The <c>control_response</c> the host wrote during capture V2292WriteDenied.</summary>
    public static CliMessage V2292WriteDeniedHostResponse => Parse("""
{"type":"control_response","response":{"subtype":"success","request_id":"req-fixture-1","response":{"behavior":"deny","message":"User declined to answer"}}}
""")[0];
}
