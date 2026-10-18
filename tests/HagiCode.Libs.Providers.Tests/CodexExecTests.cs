using System.Text;
using HagiCode.Libs.Core.AgentWork;
using HagiCode.Libs.Providers.Codex.AppServer;
using ManagedCode.CodexSharpSDK.Client;
using ManagedCode.CodexSharpSDK.Configuration;
using ManagedCode.CodexSharpSDK.Execution;
using ManagedCode.CodexSharpSDK.Internal;
using ManagedCode.CodexSharpSDK.Models;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CodexExecTests
{
    [Test]
    public void CreateStartInfo_redirects_streams_and_forces_utf8_without_bom()
    {
        var invocation = new CodexProcessInvocation(
            "/tmp/codex",
            ["exec", "--json"],
            new Dictionary<string, string>
            {
                ["PATH"] = "/tmp/runtime"
            },
            "你好，Codex");

        var startInfo = DefaultCodexProcessRunner.CreateStartInfo(invocation);

        startInfo.FileName.ShouldBe("/tmp/codex");
        startInfo.ArgumentList.ShouldBe(["exec", "--json"]);
        startInfo.RedirectStandardInput.ShouldBeTrue();
        startInfo.RedirectStandardOutput.ShouldBeTrue();
        startInfo.RedirectStandardError.ShouldBeTrue();
        startInfo.StandardInputEncoding.ShouldNotBeNull();
        startInfo.StandardInputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        startInfo.StandardInputEncoding.GetPreamble().ShouldBeEmpty();
        startInfo.StandardOutputEncoding.ShouldNotBeNull();
        startInfo.StandardOutputEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        startInfo.StandardOutputEncoding.GetPreamble().ShouldBeEmpty();
        startInfo.StandardErrorEncoding.ShouldNotBeNull();
        startInfo.StandardErrorEncoding.WebName.ShouldBe(Encoding.UTF8.WebName);
        startInfo.StandardErrorEncoding.GetPreamble().ShouldBeEmpty();
        startInfo.Environment["PATH"].ShouldBe("/tmp/runtime");
    }

    [Test]
    public async Task ResumeThread_allows_same_thread_id_with_different_model_and_local_provider()
    {
        var runner = new RecordingCodexProcessRunner();
        var exec = new CodexExec(
            executablePath: "/tmp/codex",
            environmentOverride: new Dictionary<string, string>(),
            configOverrides: null,
            processRunner: runner);
        using var client = new CodexClient(
            new CodexClientOptions
            {
                AutoStart = true,
                CodexOptions = new CodexOptions
                {
                    CodexExecutablePath = "/tmp/codex"
                }
            },
            exec);

        var firstThread = client.ResumeThread("thread-123", new ThreadOptions
        {
            Model = "gpt-5-codex",
            UseOss = true,
            LocalProvider = OssProvider.Ollama,
        });
        var secondThread = client.ResumeThread("thread-123", new ThreadOptions
        {
            Model = "gpt-5-mini",
            UseOss = true,
            LocalProvider = OssProvider.LmStudio,
        });

        var firstResult = await firstThread.RunAsync("first prompt");
        var secondResult = await secondThread.RunAsync("second prompt");

        firstResult.FinalResponse.ShouldBe("ok");
        secondResult.FinalResponse.ShouldBe("ok");
        runner.Invocations.Count.ShouldBe(2);
        runner.Invocations[0].Input.ShouldBe("first prompt");
        runner.Invocations[0].Arguments.ShouldBe([
            "exec",
            "--json",
            "--oss",
            "--local-provider",
            "ollama",
            "--model",
            "gpt-5-codex",
            "--config",
            "ephemeral=false",
            "resume",
            "thread-123"
        ]);
        runner.Invocations[1].Input.ShouldBe("second prompt");
        runner.Invocations[1].Arguments.ShouldBe([
            "exec",
            "--json",
            "--oss",
            "--local-provider",
            "lmstudio",
            "--model",
            "gpt-5-mini",
            "--config",
            "ephemeral=false",
            "resume",
            "thread-123"
        ]);
    }

    [Test]
    public async Task Exec_collaboration_items_publish_a_running_then_completed_subagent_without_usage()
    {
        var client = CreateClient(new ScriptedCodexProcessRunner(
            ThreadStarted,
            CollabItem("item.started", "spawn_agent", "in_progress", """{"T1":{"status":"running"}}""", receivers: ["T1"], prompt: "Find issues"),
            CollabItem("item.completed", "spawn_agent", "completed", """{"T1":{"status":"running"}}""", receivers: ["T1"], prompt: "Find issues"),
            CollabItem("item.completed", "wait", "completed", """{"T1":{"status":"completed","message":"Found 2 issues"}}"""),
            AgentMessage("done"),
            TurnCompleted));

        var snapshots = (await CollectAsync(client.StartThread().RunStreamedAsync("prompt")))
            .OfType<CodexAgentWorkSnapshotEvent>()
            .Select(threadEvent => threadEvent.Snapshot)
            .ToList();

        snapshots.Select(snapshot => snapshot.Items.Single().Status).ShouldBe([AgentWorkStatuses.Running, AgentWorkStatuses.Completed]);
        var completed = snapshots[^1].Items.Single();
        completed.Id.ShouldBe("codex:T1");
        completed.Summary.ShouldBe("Found 2 issues");
        completed.Usage.ShouldBeNull();
    }

    [Test]
    public async Task Exec_collaboration_values_the_vendored_models_lack_do_not_end_the_run()
    {
        const string interruptLine = "{\"type\":\"item.completed\",\"item\":{\"id\":\"c-2\",\"type\":\"collab_tool_call\",\"tool\":\"interrupt_agent\",\"status\":\"completed\",\"sender_thread_id\":\"thread-1\",\"receiver_thread_ids\":[\"T1\"],\"agents_states\":{\"T1\":{\"status\":\"interrupted\"}}}}";
        Should.Throw<InvalidOperationException>(() => ThreadEventParser.Parse(interruptLine));

        var client = CreateClient(new ScriptedCodexProcessRunner(
            ThreadStarted,
            CollabItem("item.completed", "spawn_agent", "completed", """{"T1":{"status":"running"}}""", receivers: ["T1"], prompt: "Find issues"),
            interruptLine,
            AgentMessage("done"),
            TurnCompleted));

        var events = await CollectAsync(client.StartThread().RunStreamedAsync("prompt"));

        events.OfType<CodexAgentWorkSnapshotEvent>().Last().Snapshot.Items.Single().Status.ShouldBe(AgentWorkStatuses.Stopped);
        events.OfType<TurnCompletedEvent>().Single();
        events.OfType<ItemCompletedEvent>().Select(completed => completed.Item).OfType<AgentMessageItem>().Single().Text.ShouldBe("done");
    }

    [Test]
    public async Task Run_async_result_has_the_same_thread_items_with_agent_work_snapshots_in_the_stream()
    {
        var client = CreateClient(new ScriptedCodexProcessRunner(
            ThreadStarted,
            CollabItem("item.completed", "spawn_agent", "completed", """{"T1":{"status":"running"}}""", receivers: ["T1"], prompt: "Find issues"),
            CollabItem("item.completed", "wait", "completed", """{"T1":{"status":"completed","message":"Found 2 issues"}}"""),
            AgentMessage("done"),
            TurnCompleted));

        var result = await client.StartThread().RunAsync("prompt");

        result.FinalResponse.ShouldBe("done");
        result.Items.Select(item => item.GetType()).ShouldBe([typeof(CollabToolCallItem), typeof(CollabToolCallItem), typeof(AgentMessageItem)]);
        result.Usage.ShouldNotBeNull();
    }

    private const string ThreadStarted = "{\"type\":\"thread.started\",\"thread_id\":\"thread-1\"}";
    private const string TurnCompleted = "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":1}}";

    private static CodexClient CreateClient(ICodexProcessRunner runner)
    {
        var exec = new CodexExec(
            executablePath: "/tmp/codex",
            environmentOverride: new Dictionary<string, string>(),
            configOverrides: null,
            processRunner: runner);
        return new CodexClient(
            new CodexClientOptions
            {
                AutoStart = true,
                CodexOptions = new CodexOptions
                {
                    CodexExecutablePath = "/tmp/codex"
                }
            },
            exec);
    }

    private static string AgentMessage(string text) =>
        "{\"type\":\"item.completed\",\"item\":{\"id\":\"msg-1\",\"type\":\"agent_message\",\"text\":\"" + text + "\"}}";

    private static string CollabItem(
        string eventType,
        string tool,
        string status,
        string agentsStates,
        string[]? receivers = null,
        string? prompt = null)
    {
        var receiverJson = string.Join(",", (receivers ?? []).Select(receiver => "\"" + receiver + "\""));
        var promptJson = prompt is null ? "null" : "\"" + prompt + "\"";
        return "{\"type\":\"" + eventType + "\",\"item\":{\"id\":\"c-" + tool + "\",\"type\":\"collab_tool_call\",\"tool\":\""
               + tool + "\",\"status\":\"" + status + "\",\"sender_thread_id\":\"thread-1\",\"receiver_thread_ids\":["
               + receiverJson + "],\"prompt\":" + promptJson + ",\"agents_states\":" + agentsStates + "}}";
    }

    private static async Task<List<ThreadEvent>> CollectAsync(Task<RunStreamedResult> streamed)
    {
        var events = new List<ThreadEvent>();
        await foreach (var threadEvent in (await streamed).Events)
        {
            events.Add(threadEvent);
        }

        return events;
    }

    private sealed class ScriptedCodexProcessRunner(params string[] lines) : ICodexProcessRunner
    {
        public async IAsyncEnumerable<string> RunAsync(
            CodexProcessInvocation invocation,
            Microsoft.Extensions.Logging.ILogger logger,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var line in lines)
            {
                yield return line;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class RecordingCodexProcessRunner : ICodexProcessRunner
    {
        public List<CodexProcessInvocation> Invocations { get; } = [];

        public async IAsyncEnumerable<string> RunAsync(
            CodexProcessInvocation invocation,
            Microsoft.Extensions.Logging.ILogger logger,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            yield return "{\"type\":\"item.completed\",\"item\":{\"id\":\"msg-1\",\"type\":\"agent_message\",\"text\":\"ok\"}}";
            yield return "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":1}}";
            await Task.CompletedTask;
        }
    }
}
