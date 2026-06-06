using System.Text;
using ManagedCode.CodexSharpSDK.Client;
using ManagedCode.CodexSharpSDK.Configuration;
using ManagedCode.CodexSharpSDK.Execution;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CodexExecTests
{
    [Fact]
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

    [Fact]
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
