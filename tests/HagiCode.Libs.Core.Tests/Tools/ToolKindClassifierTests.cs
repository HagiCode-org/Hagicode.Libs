using HagiCode.Libs.Core.Tools;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Tools;

public sealed class ToolKindClassifierTests
{
    // Each row is one rule of the D3 resolution order. Rows are grouped by the step they exercise.
    // Step 1: explicit provider kind other than "other".
    [TestCase("execute", null, null, null, "shell", null)]
    [TestCase("read", "anything", null, null, "read", null)]
    [TestCase("search", null, null, null, "search", null)]
    [TestCase("fetch", null, null, null, "fetch", null)]
    [TestCase("edit", "Edit", "{\"path\":\"a.txt\",\"oldText\":\"a\",\"newText\":\"b\"}", null, "edit", null)]
    [TestCase("delete", "bash", "{\"command\":\"rm a\"}", null, "other", null)]
    [TestCase("move", null, null, null, "other", null)]
    [TestCase("think", null, null, null, "other", null)]
    [TestCase("switch_mode", null, null, null, "other", null)]
    [TestCase("frobnicate", "Bash", "{\"command\":\"ls\"}", null, "other", null)]
    [TestCase("other", "bash", "{\"command\":\"ls\"}", null, "shell", null)]
    [TestCase("todo", null, null, null, "todo", null)]
    [TestCase("SHELL", null, null, null, "shell", null)]
    // Step 2: MCP identity.
    [TestCase(null, "mcp__shadcn__search_items_in_registries", null, null, "mcp", "shadcn")]
    [TestCase(null, "list_items_in_registries", null, "shadcn", "mcp", "shadcn")]
    [TestCase(null, "mcp__", null, null, "mcp", null)]
    [TestCase(null, "mcp__shadcn", null, null, "mcp", null)]
    [TestCase(null, "Bash", null, null, "shell", null)]
    [TestCase("read", "mcp__shadcn__x", null, null, "read", null)]
    // Step 3: case-insensitive raw-name alias.
    [TestCase(null, "Bash", "{\"command\":\"ls\"}", null, "shell", null)]
    [TestCase(null, "bash", null, null, "shell", null)]
    [TestCase(null, "Read", "{\"file_path\":\"a\"}", null, "read", null)]
    [TestCase(null, "Write", "{\"file_path\":\"a\",\"content\":\"x\"}", null, "write", null)]
    [TestCase(null, "write", "{\"file_path\":\"a\",\"content\":\"x\"}", null, "write", null)]
    [TestCase(null, "Edit", "{\"file_path\":\"a\",\"old_string\":\"a\",\"new_string\":\"b\"}", null, "edit", null)]
    [TestCase(null, "Glob", "{\"pattern\":\"*.cs\"}", null, "glob", null)]
    [TestCase(null, "Grep", "{\"pattern\":\"x\"}", null, "search", null)]
    [TestCase(null, "WebSearch", "{\"query\":\"x\"}", null, "search", null)]
    [TestCase(null, "WebFetch", "{\"url\":\"https://example.com\"}", null, "fetch", null)]
    [TestCase(null, "Task", "{}", null, "subagent", null)]
    [TestCase(null, "TodoWrite", null, null, "todo", null)]
    [TestCase(null, "command_execution", null, null, "shell", null)]
    // No alias for mcp_tool_call: a Codex MCP item reports its server, so step 2 classifies it.
    [TestCase(null, "mcp_tool_call", null, null, "other", null)]
    [TestCase(null, "list_items_in_registries", null, "shadcn", "mcp", "shadcn")]
    // Step 4: narrow input shape, reached only when the name is unknown.
    [TestCase(null, "unknown_tool", "{\"command\":\"ls\"}", null, "shell", null)]
    [TestCase(null, "unknown_tool", "{\"oldText\":\"a\",\"newText\":\"b\"}", null, "edit", null)]
    [TestCase(null, "unknown_tool", "{\"old_string\":\"a\",\"new_string\":\"b\"}", null, "edit", null)]
    [TestCase(null, "unknown_tool", "{\"url\":\"https://example.com\"}", null, "fetch", null)]
    [TestCase(null, "unknown_tool", "{\"pattern\":\"x\"}", null, "search", null)]
    [TestCase(null, "unknown_tool", "{\"path\":\"a.txt\"}", null, "other", null)]
    [TestCase(null, "unknown_tool", "{\"command\":42}", null, "other", null)]
    // Step 5: edit with a path and full content but no old or new text becomes write.
    [TestCase("edit", "write_file", "{\"path\":\"permission-probe.txt\",\"content\":\"ok\\n\"}", null, "write", null)]
    [TestCase("edit", null, "{\"file_path\":\"a.txt\",\"content\":\"x\"}", null, "write", null)]
    [TestCase("edit", null, "{\"path\":\"a.txt\",\"content\":\"x\",\"old_string\":\"a\"}", null, "edit", null)]
    // Step 6 and the fallbacks.
    [TestCase(null, "unknown_tool", null, null, "other", null)]
    [TestCase(null, null, null, null, "other", null)]
    [TestCase(null, "unknown_tool", "not json", null, "other", null)]
    [TestCase(null, "unknown_tool", "[1,2]", null, "other", null)]
    [TestCase(null, "", "", null, "other", null)]
    public void Resolve_matches_the_d3_resolution_order(
        string? providerKind,
        string? rawName,
        string? inputJson,
        string? server,
        string expectedKind,
        string? expectedServer)
    {
        var resolution = ToolKindClassifier.Resolve(providerKind, rawName, inputJson, server);

        resolution.Kind.ShouldBe(expectedKind);
        resolution.Server.ShouldBe(expectedServer);
        ToolKinds.All.ShouldContain(resolution.Kind);
    }

    [TestCase("Dsh bash with ACP kind other", "other", "bash", "{\"command\":\"echo hi\",\"description\":\"d\",\"workdir\":\"/w\"}", "shell")]
    [TestCase("Reasonix write_file with ACP kind edit", "edit", "write_file", "{\"path\":\"permission-probe.txt\",\"content\":\"ok\\n\"}", "write")]
    public void Resolve_handles_the_recorded_acp_shapes(string scenario, string providerKind, string rawName, string inputJson, string expectedKind)
    {
        ToolKindClassifier.Resolve(providerKind, rawName, inputJson).Kind.ShouldBe(expectedKind, scenario);
    }

    [Test]
    public void Resolve_never_puts_the_server_on_a_non_mcp_call()
    {
        // An explicit non-MCP kind wins over the name and drops the server, so the call stays non-MCP.
        ToolKindClassifier.Resolve("execute", "mcp__shadcn__search", null, "shadcn").ShouldBe(new ToolKindResolution("shell"));
        ToolKindClassifier.Resolve(null, "Bash", null, null).ShouldBe(new ToolKindResolution("shell"));
    }

    [Test]
    public void Resolve_keeps_the_provided_server_as_the_mcp_server_when_the_name_also_carries_one()
    {
        ToolKindClassifier.Resolve(null, "mcp__other__tool", null, "shadcn").ShouldBe(new ToolKindResolution("mcp", "shadcn"));
    }

    [TestCase("shell", "shell")]
    [TestCase(" MCP ", "mcp")]
    [TestCase("Other", "other")]
    [TestCase("frobnicate", "other")]
    [TestCase("", "other")]
    [TestCase(null, "other")]
    public void Normalize_returns_the_canonical_value_and_falls_back_to_other(string? value, string expected)
    {
        ToolKinds.Normalize(value).ShouldBe(expected);
    }

    [Test]
    public void Vocabulary_lists_exactly_the_eleven_canonical_kinds()
    {
        ToolKinds.All.ShouldBe(["shell", "read", "write", "edit", "search", "glob", "fetch", "subagent", "todo", "mcp", "other"]);
    }
}
