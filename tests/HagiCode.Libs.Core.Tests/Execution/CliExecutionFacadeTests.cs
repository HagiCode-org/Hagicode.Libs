using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Core.Process;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Execution;

public sealed class CliExecutionFacadeTests
{
    private readonly CliExecutionFacade _facade = new(new CliProcessManager());

    [Fact]
    public async Task ExecuteAsync_returns_structured_success_result_for_buffered_execution()
    {
        var result = await _facade.ExecuteAsync(new CliExecutionRequest
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-lc", "printf 'hello'; printf 'oops' >&2"]
        });

        result.Status.ShouldBe(CliExecutionStatus.Success);
        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldBe("hello");
        result.StandardError.ShouldBe("oops");
        result.CommandPreview.ShouldContain("/bin/sh");
        result.CapturedOutput.ShouldContain(chunk => chunk.Channel == CliExecutionOutputChannel.StandardOutput && chunk.Text.Contains("hello", StringComparison.Ordinal));
        result.CapturedOutput.ShouldContain(chunk => chunk.Channel == CliExecutionOutputChannel.StandardError && chunk.Text.Contains("oops", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_returns_timeout_status_and_diagnostics()
    {
        var result = await _facade.ExecuteAsync(new CliExecutionRequest
        {
            ExecutablePath = "/bin/sh",
            Arguments = ["-lc", "sleep 2"],
            Timeout = TimeSpan.FromMilliseconds(100)
        });

        result.Status.ShouldBe(CliExecutionStatus.TimedOut);
        result.TimedOut.ShouldBeTrue();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "execution_timeout");
        result.StandardError.ShouldContain("timed out");
    }

    [Fact]
    public async Task ExecuteAsync_returns_rejected_result_when_policy_blocks_request()
    {
        var facade = new CliExecutionFacade(new CliProcessManager(), null, new RejectingPolicy());

        var result = await facade.ExecuteAsync(new CliExecutionRequest
        {
            ExecutablePath = "/bin/echo",
            Arguments = ["hello"]
        });

        result.Status.ShouldBe(CliExecutionStatus.Rejected);
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "blocked");
        result.ExitCode.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteStreamingAsync_emits_output_events_and_terminal_envelope()
    {
        var events = new List<CliExecutionEvent>();

        await foreach (var executionEvent in _facade.ExecuteStreamingAsync(new CliExecutionRequest
                       {
                           ExecutablePath = "/bin/sh",
                           Arguments = ["-lc", "printf 'hello'; printf 'oops' >&2"],
                           Options = new CliExecutionOptions
                           {
                               Mode = CliExecutionMode.Streaming
                           }
                       }))
        {
            events.Add(executionEvent);
        }

        events.ShouldContain(executionEvent => executionEvent.Kind == CliExecutionEventKind.StandardOutput && executionEvent.Text!.Contains("hello", StringComparison.Ordinal));
        events.ShouldContain(executionEvent => executionEvent.Kind == CliExecutionEventKind.StandardError && executionEvent.Text!.Contains("oops", StringComparison.Ordinal));

        var completed = events.Single(executionEvent => executionEvent.Kind == CliExecutionEventKind.Completed);
        completed.Result.ShouldNotBeNull();
        completed.Result.Status.ShouldBe(CliExecutionStatus.StreamingCompleted);
        completed.Result.StandardOutput.ShouldContain("hello");
        completed.Result.StandardError.ShouldContain("oops");
    }

    private sealed class RejectingPolicy : ICliExecutionPolicy
    {
        public ValueTask<CliExecutionPolicyDecision> EvaluateAsync(CliExecutionContext context, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(CliExecutionPolicyDecision.Reject(new CliExecutionDiagnostic("blocked", "Blocked by test policy.")));
        }
    }
}
