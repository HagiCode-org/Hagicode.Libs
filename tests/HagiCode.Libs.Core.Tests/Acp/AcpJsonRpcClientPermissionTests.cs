using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HagiCode.Libs.Core.Acp;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Acp;

public sealed class AcpJsonRpcClientPermissionTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private const string PermissionParams = """
        {"sessionId":"session-1","toolCall":{"toolCallId":"call-1","title":"Write file","rawInput":{"path":"a.txt"}},"options":[{"optionId":"reject","name":"Reject","kind":"reject_once"},{"optionId":"allow-once","name":"Allow once","kind":"allow_once"},{"optionId":"allow-always","name":"Allow always","kind":"allow_always"}]}
        """;

    [Test]
    [Category("CriticalRegression")]
    public async Task Without_a_handler_the_permission_request_gets_the_default_answer_after_its_notification()
    {
        var transport = new PermissionTestTransport();
        await using var client = new AcpJsonRpcClient(transport);
        await client.ConnectAsync();

        transport.Deliver(PermissionRequest(7));

        var notification = await ReadFirstNotificationAsync(client);
        notification.Method.ShouldBe("session/request_permission");
        notification.Parameters.GetProperty("toolCall").GetProperty("toolCallId").GetString().ShouldBe("call-1");
        (await transport.WaitForLineAsync(0)).ShouldBe(
            """{"jsonrpc":"2.0","id":7,"result":{"outcome":{"outcome":"selected","optionId":"allow-always"}}}""");
    }

    [Test]
    public async Task A_selected_decision_answers_with_that_option_and_the_handler_sees_the_parsed_request()
    {
        var transport = new PermissionTestTransport();
        AcpPermissionRequest? seen = null;
        await using var client = new AcpJsonRpcClient(transport, (request, _) =>
        {
            seen = request;
            return Task.FromResult<AcpPermissionDecision?>(AcpPermissionDecision.Select("allow-once"));
        });
        await client.ConnectAsync();

        transport.Deliver(PermissionRequest(8));

        (await transport.WaitForLineAsync(0)).ShouldBe(
            """{"jsonrpc":"2.0","id":8,"result":{"outcome":{"outcome":"selected","optionId":"allow-once"}}}""");
        seen.ShouldNotBeNull();
        seen.ToolCallId.ShouldBe("call-1");
        seen.Options.ShouldBe(
        [
            new AcpPermissionOption("reject", "Reject", "reject_once"),
            new AcpPermissionOption("allow-once", "Allow once", "allow_once"),
            new AcpPermissionOption("allow-always", "Allow always", "allow_always")
        ]);
        seen.Parameters.GetProperty("sessionId").GetString().ShouldBe("session-1");
    }

    [Test]
    public async Task A_cancelled_decision_answers_with_the_cancelled_outcome()
    {
        var transport = new PermissionTestTransport();
        await using var client = new AcpJsonRpcClient(
            transport,
            (_, _) => Task.FromResult<AcpPermissionDecision?>(AcpPermissionDecision.Cancelled));
        await client.ConnectAsync();

        transport.Deliver(PermissionRequest("req-9"));

        (await transport.WaitForLineAsync(0)).ShouldBe(
            """{"jsonrpc":"2.0","id":"req-9","result":{"outcome":{"outcome":"cancelled"}}}""");
    }

    [Test]
    public async Task A_null_decision_falls_through_to_the_default_answer()
    {
        var transport = new PermissionTestTransport();
        var calls = 0;
        await using var client = new AcpJsonRpcClient(transport, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<AcpPermissionDecision?>(null);
        });
        await client.ConnectAsync();

        transport.Deliver(PermissionRequest(10));

        (await transport.WaitForLineAsync(0)).ShouldBe(
            """{"jsonrpc":"2.0","id":10,"result":{"outcome":{"outcome":"selected","optionId":"allow-always"}}}""");
        calls.ShouldBe(1);
    }

    [Test]
    public async Task The_handler_runs_after_the_notification_is_published()
    {
        var transport = new PermissionTestTransport();
        var notificationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new AcpJsonRpcClient(transport, async (_, token) =>
        {
            await notificationSeen.Task.WaitAsync(WaitTimeout, token);
            return AcpPermissionDecision.Select("reject");
        });
        await client.ConnectAsync();

        transport.Deliver(PermissionRequest(11));
        (await ReadFirstNotificationAsync(client)).Method.ShouldBe("session/request_permission");
        notificationSeen.SetResult();

        (await transport.WaitForLineAsync(0)).ShouldContain("\"optionId\":\"reject\"");
    }

    [Test]
    public async Task A_delayed_answer_written_while_another_request_is_sent_arrives_as_two_complete_lines()
    {
        var transport = new PermissionTestTransport { SplitWrites = true };
        var releaseAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new AcpJsonRpcClient(transport, async (_, token) =>
        {
            await releaseAnswer.Task.WaitAsync(WaitTimeout, token);
            return AcpPermissionDecision.Select("allow-once");
        });
        await client.ConnectAsync();
        transport.Deliver(PermissionRequest(12));
        await ReadFirstNotificationAsync(client);

        // Release the pending answer as soon as the request below is half written.
        transport.OnFirstHalfWritten = () => releaseAnswer.TrySetResult();
        var invoke = client.InvokeAsync<JsonElement>("session/cancel_probe", new { sessionId = "session-1" });
        await transport.WaitForLineAsync(1);
        await invoke.WaitAsync(WaitTimeout);

        var lines = transport.RawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2, transport.RawOutput);
        var documents = lines.Select(static line => JsonDocument.Parse(line)).ToArray();
        documents[0].RootElement.GetProperty("method").GetString().ShouldBe("session/cancel_probe");
        documents[1].RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString().ShouldBe("allow-once");
    }

    [Test]
    public async Task Disposing_with_a_pending_handler_cancels_it_and_completes_without_a_reply_or_fault()
    {
        var transport = new PermissionTestTransport();
        var handlerStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AcpJsonRpcClient(transport, async (_, token) =>
        {
            handlerStarted.TrySetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return AcpPermissionDecision.Select("allow-once");
        });
        await client.ConnectAsync();
        transport.Deliver(PermissionRequest(13));
        var handlerToken = await handlerStarted.Task.WaitAsync(WaitTimeout);

        await client.DisposeAsync().AsTask().WaitAsync(WaitTimeout);

        handlerToken.IsCancellationRequested.ShouldBeTrue();
        transport.Lines.ShouldBeEmpty();
        transport.IsDisposed.ShouldBeTrue();
    }

    [Test]
    public async Task Disposing_with_a_handler_that_ignores_its_token_completes_without_a_reply()
    {
        var transport = new PermissionTestTransport();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<AcpPermissionDecision?>();
        var client = new AcpJsonRpcClient(transport, (_, _) =>
        {
            handlerStarted.TrySetResult();
            return never.Task;
        });
        await client.ConnectAsync();
        transport.Deliver(PermissionRequest(14));
        await handlerStarted.Task.WaitAsync(WaitTimeout);

        await client.DisposeAsync().AsTask().WaitAsync(WaitTimeout);

        // A late decision after disposal must not be written or fault anywhere.
        never.SetResult(AcpPermissionDecision.Select("allow-once"));
        await Task.Delay(50);
        transport.Lines.ShouldBeEmpty();
    }

    // The option shape CodeBuddy sends for a view-image tool call (probe 1.4): the one-time allowance is optionId "allow", kind "allow_once".
    private const string CodebuddyPermissionOptions = """
        [{"kind":"allow_always","name":"Always Allow","optionId":"allow_always"},{"kind":"allow_once","name":"Allow","optionId":"allow"},{"kind":"reject_once","name":"Reject","optionId":"reject"}]
        """;

    [Test]
    public async Task Without_the_preference_both_allowances_offered_still_select_allow_always()
    {
        var transport = new PermissionTestTransport();
        await using var client = new AcpJsonRpcClient(transport);
        await client.ConnectAsync();

        transport.Deliver(PermissionRequestWithOptions(21, CodebuddyPermissionOptions));

        (await transport.WaitForLineAsync(0)).ShouldBe(
            """{"jsonrpc":"2.0","id":21,"result":{"outcome":{"outcome":"selected","optionId":"allow_always"}}}""");
    }

    [Test]
    public async Task With_the_preference_both_allowances_offered_select_allow_once_matched_by_kind()
    {
        var transport = new PermissionTestTransport();
        await using var client = new AcpJsonRpcClient(transport, preferOneTimePermission: true);
        await client.ConnectAsync();

        transport.Deliver(PermissionRequestWithOptions(22, CodebuddyPermissionOptions));

        (await transport.WaitForLineAsync(0)).ShouldBe(
            """{"jsonrpc":"2.0","id":22,"result":{"outcome":{"outcome":"selected","optionId":"allow"}}}""");
    }

    [Test]
    public async Task With_the_preference_and_only_a_permanent_allowance_select_it_rather_than_the_first_listed_option()
    {
        var transport = new PermissionTestTransport();
        await using var client = new AcpJsonRpcClient(transport, preferOneTimePermission: true);
        await client.ConnectAsync();

        transport.Deliver(PermissionRequestWithOptions(23,
            """[{"kind":"reject_once","name":"Reject","optionId":"reject"},{"kind":"allow_always","name":"Always Allow","optionId":"allow_always"}]"""));

        (await transport.WaitForLineAsync(0)).ShouldBe(
            """{"jsonrpc":"2.0","id":23,"result":{"outcome":{"outcome":"selected","optionId":"allow_always"}}}""");
    }

    [Test]
    public async Task With_or_without_the_preference_a_request_with_neither_allowance_selects_the_first_option()
    {
        var options = """[{"kind":"custom","name":"Custom","optionId":"opt-x"},{"kind":"reject_once","name":"Reject","optionId":"reject"}]""";

        foreach (var preferOneTimePermission in new[] { false, true })
        {
            var transport = new PermissionTestTransport();
            await using var client = new AcpJsonRpcClient(transport, preferOneTimePermission: preferOneTimePermission);
            await client.ConnectAsync();

            transport.Deliver(PermissionRequestWithOptions(24, options));

            (await transport.WaitForLineAsync(0)).ShouldBe(
                """{"jsonrpc":"2.0","id":24,"result":{"outcome":{"outcome":"selected","optionId":"opt-x"}}}""");
        }
    }

    [Test]
    public async Task Without_options_the_answer_falls_back_to_allow_with_or_without_the_preference()
    {
        foreach (var preferOneTimePermission in new[] { false, true })
        {
            var transport = new PermissionTestTransport();
            await using var client = new AcpJsonRpcClient(transport, preferOneTimePermission: preferOneTimePermission);
            await client.ConnectAsync();

            transport.Deliver(PermissionRequestWithOptions(25, null));

            (await transport.WaitForLineAsync(0)).ShouldBe(
                """{"jsonrpc":"2.0","id":25,"result":{"outcome":{"outcome":"selected","optionId":"allow"}}}""");
        }
    }

    [Test]
    public async Task The_preference_applies_to_the_default_answer_of_an_agent_session_client_built_with_it()
    {
        var transport = new PermissionTestTransport();
        await using var client = new AcpSessionClient(transport, preferOneTimePermission: true);
        await client.ConnectAsync();

        transport.Deliver(PermissionRequestWithOptions(26, CodebuddyPermissionOptions));

        (await transport.WaitForLineAsync(0)).ShouldBe(
            """{"jsonrpc":"2.0","id":26,"result":{"outcome":{"outcome":"selected","optionId":"allow"}}}""");
    }

    private static string PermissionRequestWithOptions(object id, string? optionsJson)
    {
        var parameters = optionsJson is null
            ? """{"sessionId":"session-1","toolCall":{"toolCallId":"call-1"}}"""
            : """{"sessionId":"session-1","toolCall":{"toolCallId":"call-1"},"options":""" + optionsJson.Trim() + "}";
        return "{\"jsonrpc\":\"2.0\",\"id\":" + JsonSerializer.Serialize(id) + ",\"method\":\"session/request_permission\",\"params\":" + parameters + "}";
    }

    private static string PermissionRequest(object id)
    {
        return "{\"jsonrpc\":\"2.0\",\"id\":" + JsonSerializer.Serialize(id) + ",\"method\":\"session/request_permission\",\"params\":" +
               PermissionParams.Trim() + "}";
    }

    private static async Task<AcpNotification> ReadFirstNotificationAsync(AcpJsonRpcClient client)
    {
        using var timeout = new CancellationTokenSource(WaitTimeout);
        await using var enumerator = client.ReceiveNotificationsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    // Steering writes a second request while session/prompt is pending (codebuddy-run-steering, task 3A.1). The writes must stay
    // whole and each reply must reach the request it answers, even when the two writes are split like a stream writer's.
    [Test]
    public async Task A_request_written_while_another_is_pending_stays_whole_and_each_reply_reaches_its_own_request()
    {
        var transport = new PermissionTestTransport { SplitWrites = true, AutoRespond = false };
        await using var client = new AcpJsonRpcClient(transport);
        await client.ConnectAsync();

        var prompt = client.InvokeAsync<JsonElement>("session/prompt", new { sessionId = "session-1", prompt = Array.Empty<object>() });
        var steer = client.InvokeAsync<JsonElement>("session/steer", new { sessionId = "session-1", contentBlocks = new[] { new { type = "text", text = "steer" } } });

        var promptLine = await transport.WaitForLineAsync(0);
        var steerLine = await transport.WaitForLineAsync(1);
        JsonDocument.Parse(promptLine).RootElement.GetProperty("method").GetString().ShouldBe("session/prompt");
        JsonDocument.Parse(steerLine).RootElement.GetProperty("method").GetString().ShouldBe("session/steer");

        transport.Deliver("""{"jsonrpc":"2.0","id":2,"result":{"steered":true,"ownerRequestId":"owner-1"}}""");
        transport.Deliver("""{"jsonrpc":"2.0","id":1,"result":{"stopReason":"end_turn"}}""");

        (await steer).GetProperty("steered").GetBoolean().ShouldBeTrue();
        (await prompt).GetProperty("stopReason").GetString().ShouldBe("end_turn");
        foreach (var line in transport.Lines)
        {
            Should.NotThrow(() => JsonDocument.Parse(line).Dispose());
        }
    }

    /// <summary>
    /// Delivers scripted agent frames and records every line the client writes. With <see cref="SplitWrites" /> each write is
    /// two awaited halves, like a stream writer's write followed by a flush, so unserialized writes would interleave.
    /// </summary>
    private sealed class PermissionTestTransport : IAcpTransport
    {
        private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
        private readonly StringBuilder _output = new();
        private readonly object _sync = new();
        private readonly List<string> _lines = [];
        private readonly List<(int Index, TaskCompletionSource<string> Waiter)> _lineWaiters = [];

        public bool SplitWrites { get; init; }

        /// <summary>When true (the default), every request written is answered with an empty result at once.</summary>
        public bool AutoRespond { get; init; } = true;

        public Action? OnFirstHalfWritten { get; set; }

        public bool IsConnected { get; private set; }

        public bool IsDisposed { get; private set; }

        public string RawOutput
        {
            get
            {
                lock (_sync)
                {
                    return _output.ToString();
                }
            }
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_sync)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Deliver(string frame) => _inbound.Writer.TryWrite(frame);

        public Task<string> WaitForLineAsync(int index)
        {
            lock (_sync)
            {
                if (index < _lines.Count)
                {
                    return Task.FromResult(_lines[index]);
                }

                var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _lineWaiters.Add((index, waiter));
                return waiter.Task.WaitAsync(WaitTimeout);
            }
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            if (!SplitWrites)
            {
                Append(message + "\n");
                RespondTo(message);
                return;
            }

            var half = message.Length / 2;
            Append(message[..half]);
            var onFirstHalfWritten = OnFirstHalfWritten;
            OnFirstHalfWritten = null;
            onFirstHalfWritten?.Invoke();
            await Task.Delay(150, CancellationToken.None);
            Append(message[half..] + "\n");
            RespondTo(message);
        }

        public async IAsyncEnumerable<string> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in _inbound.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            _inbound.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            IsConnected = false;
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private void Append(string text)
        {
            List<TaskCompletionSource<string>> ready = [];
            List<string> completedLines = [];
            lock (_sync)
            {
                _output.Append(text);
                var all = _output.ToString();
                var lines = all.Split('\n');
                for (var index = _lines.Count; index < lines.Length - 1; index++)
                {
                    _lines.Add(lines[index]);
                }

                for (var index = _lineWaiters.Count - 1; index >= 0; index--)
                {
                    var (lineIndex, waiter) = _lineWaiters[index];
                    if (lineIndex < _lines.Count)
                    {
                        ready.Add(waiter);
                        completedLines.Add(_lines[lineIndex]);
                        _lineWaiters.RemoveAt(index);
                    }
                }
            }

            for (var index = 0; index < ready.Count; index++)
            {
                ready[index].TrySetResult(completedLines[index]);
            }
        }

        /// <summary>Answers a client request (a frame with a method and an id) with an empty result.</summary>
        private void RespondTo(string message)
        {
            if (!AutoRespond)
            {
                return;
            }

            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var id))
            {
                _inbound.Writer.TryWrite("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":{}}");
            }
        }
    }
}
