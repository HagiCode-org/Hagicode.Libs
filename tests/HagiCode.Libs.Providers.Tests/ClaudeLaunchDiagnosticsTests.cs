using System.Text;
using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ClaudeLaunchDiagnosticsTests
{
    [Test]
    public void DescribeArguments_names_secret_bearing_flags_without_their_values()
    {
        var description = ClaudeLaunchDiagnostics.DescribeArguments(
        [
            "--output-format", "stream-json",
            "--system-prompt", "SYSTEM-PROMPT-SECRET",
            "--append-system-prompt", "APPENDED-PROMPT-SECRET",
            "--mcp-config", "{\"mcpServers\":{\"x\":{\"env\":{\"K\":\"MCP-SECRET\"}}}}"
        ]);

        description.ShouldContain("--system-prompt");
        description.ShouldContain("--append-system-prompt");
        description.ShouldContain("--mcp-config");
        description.ShouldNotContain("SYSTEM-PROMPT-SECRET");
        description.ShouldNotContain("APPENDED-PROMPT-SECRET");
        description.ShouldNotContain("MCP-SECRET");
    }

    [Test]
    public void DescribeArguments_shows_allowlisted_values_including_the_plugin_directory()
    {
        var description = ClaudeLaunchDiagnostics.DescribeArguments(
            ["--plugin-dir", "/plugins/hagi", "--model", "claude-sonnet", "--add-dir", "/repo", "--debug-file", "/data/claude-debug/x.log"]);

        description.ShouldContain("--plugin-dir /plugins/hagi");
        description.ShouldContain("--model claude-sonnet");
        description.ShouldContain("--add-dir /repo");
        description.ShouldContain("--debug-file /data/claude-debug/x.log");
    }

    [Test]
    public void DescribeArguments_masks_inline_settings_json_and_shows_a_settings_path()
    {
        const string inlineSettings = "{\"env\":{\"ANTHROPIC_AUTH_TOKEN\":\"INLINE-TOKEN\"}}";

        var inlineDescription = ClaudeLaunchDiagnostics.DescribeArguments(["--settings", inlineSettings]);
        var pathDescription = ClaudeLaunchDiagnostics.DescribeArguments(["--settings", "/data/settings.json"]);

        inlineDescription.ShouldBe($"--settings inline-json({inlineSettings.Length} chars)");
        inlineDescription.ShouldNotContain("INLINE-TOKEN");
        pathDescription.ShouldBe("--settings /data/settings.json");
    }

    [Test]
    public void DescribeArguments_does_not_print_a_value_that_looks_like_a_flag_with_spaces()
    {
        var description = ClaudeLaunchDiagnostics.DescribeArguments(
            ["--append-system-prompt", "--ignore all previous instructions", "--verbose"]);

        description.ShouldBe("--append-system-prompt --verbose");
    }

    [Test]
    public void DescribeEnvironment_lists_sorted_names_and_states_credential_presence_without_values()
    {
        var description = ClaudeLaunchDiagnostics.DescribeEnvironment(new Dictionary<string, string?>
        {
            ["CLAUDE_CODE_ENTRYPOINT"] = "sdk-csharp",
            ["PATH"] = "/usr/bin",
            ["ANTHROPIC_AUTH_TOKEN"] = "sk-live-TOKEN-VALUE",
            ["ANTHROPIC_BASE_URL"] = "https://example.test"
        });

        description.ShouldBe(
            "CLAUDE_CODE_ENTRYPOINT, PATH; ANTHROPIC_AUTH_TOKEN=present, ANTHROPIC_API_KEY=absent, ANTHROPIC_BASE_URL=present");
        description.ShouldNotContain("sk-live-TOKEN-VALUE");
        description.ShouldNotContain("example.test");
    }

    [Test]
    public void DescribeEnvironment_counts_null_and_empty_credentials_as_absent_and_omits_removed_names()
    {
        var description = ClaudeLaunchDiagnostics.DescribeEnvironment(new Dictionary<string, string?>
        {
            ["ANTHROPIC_AUTH_TOKEN"] = string.Empty,
            ["ANTHROPIC_API_KEY"] = null,
            ["REMOVED_VARIABLE"] = null
        });

        description.ShouldBe("(none); ANTHROPIC_AUTH_TOKEN=absent, ANTHROPIC_API_KEY=absent, ANTHROPIC_BASE_URL=absent");
    }

    [Test]
    public void DescribeStdinMessage_states_type_and_utf8_byte_length_without_content()
    {
        var message = Message("user", new { type = "user", message = new { role = "user", content = "PROMPT-TEXT-SECRET 中文" } });

        var description = ClaudeLaunchDiagnostics.DescribeStdinMessage(message);

        description.ShouldBe($"type=user bytes={Encoding.UTF8.GetByteCount(message.Content.GetRawText())}");
        description.ShouldNotContain("PROMPT-TEXT-SECRET");
    }

    [Test]
    public void DescribeStdinMessage_reads_the_nested_subtype_of_control_requests_and_responses()
    {
        var request = Message("control_request", new { type = "control_request", request_id = "r1", request = new { subtype = "can_use_tool" } });
        var response = Message("control_response", new { type = "control_response", response = new { subtype = "success", request_id = "r1" } });

        ClaudeLaunchDiagnostics.DescribeStdinMessage(request)
            .ShouldBe($"type=control_request subtype=can_use_tool bytes={Encoding.UTF8.GetByteCount(request.Content.GetRawText())}");
        ClaudeLaunchDiagnostics.DescribeStdinMessage(response)
            .ShouldBe($"type=control_response subtype=success bytes={Encoding.UTF8.GetByteCount(response.Content.GetRawText())}");
    }

    private static CliMessage Message(string type, object payload) => new(type, JsonSerializer.SerializeToElement(payload));
}
