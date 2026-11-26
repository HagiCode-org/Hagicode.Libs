// Shared harness for the Antigravity feature tests (2A.1-2A.8).
//
// SYNTHETIC: the scripted agent is not agy. It answers the ACP v1 methods HagiCode calls with values shaped after the
// verified CLI 1.5.17 facts in docs/antigravity-feature-matrix.md, and it replays turn fixtures from
// Fixtures/Antigravity/, which are labelled provenance "synthetic". The provider and the real AcpSessionClient / JSON-RPC
// code run unchanged on top of it, so these tests check how HagiCode drives the protocol. They never prove what agy does.
// Real-CLI checks live in the gated tests of the same feature classes (HAGICODE_REAL_CLI_TESTS=1 plus agy on PATH).
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Antigravity;
using HagiCode.Libs.Tests.Shared;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>What the scripted agent sends in answer to one <c>session/prompt</c> request.</summary>
internal sealed record AntigravityTurnScript
{
    /// <summary>Frames sent before the answer, in order: session/update notifications and agent-to-client requests.</summary>
    public IReadOnlyList<string> Frames { get; init; } = [];

    /// <summary>The <c>session/prompt</c> result, or null.</summary>
    public JsonNode? Result { get; init; }

    /// <summary>The JSON-RPC error that answers <c>session/prompt</c>, or null.</summary>
    public JsonNode? Error { get; init; }

    /// <summary>How long the answer waits after the frames are sent. Zero answers at once.</summary>
    public TimeSpan ResponseDelay { get; init; }

    /// <summary>A turn with neither a result nor an error stays open until the client stops waiting.</summary>
    public bool StaysOpen => Result is null && Error is null;

    public static AntigravityTurnScript EndTurn(params string[] frames) =>
        new() { Frames = frames, Result = JsonNode.Parse("""{"stopReason":"end_turn"}""") };

    public static AntigravityTurnScript StopReason(string stopReason, params string[] frames) =>
        new() { Frames = frames, Result = JsonNode.Parse($$"""{"stopReason":"{{stopReason}}"}""") };

    /// <summary>
    /// Loads a turn from <c>Fixtures/Antigravity/&lt;fileName&gt;</c>. The first line is the fixture header. A line with a
    /// <c>promptResult</c> or <c>promptError</c> property is the answer to <c>session/prompt</c>. Every other line is sent
    /// verbatim as a frame.
    /// </summary>
    public static AntigravityTurnScript FromFixture(string fileName)
    {
        var path = AntigravityFixtures.PathOf(fileName);
        var lines = File.ReadAllLines(path).Where(static line => !string.IsNullOrWhiteSpace(line)).ToArray();
        lines.Length.ShouldBeGreaterThan(1, $"{fileName} needs a header and at least one line");

        using (var header = JsonDocument.Parse(lines[0]))
        {
            header.RootElement.TryGetProperty("fixtureHeader", out var headerElement).ShouldBeTrue($"{fileName}: first line must be fixtureHeader");
            // "synthetic": written by hand from the ACP shapes. "recorded": captured from agy 1.5.17 by agy-recorder/record_agy.py.
            var provenance = headerElement.GetProperty("provenance").GetString();
            (provenance is "synthetic" or "recorded").ShouldBeTrue($"{fileName}: unknown provenance '{provenance}'");
        }

        var frames = new List<string>();
        JsonNode? result = null;
        JsonNode? error = null;
        foreach (var line in lines.Skip(1))
        {
            var node = JsonNode.Parse(line)!.AsObject();
            if (node.TryGetPropertyValue("promptResult", out var promptResult))
            {
                result = promptResult;
            }
            else if (node.TryGetPropertyValue("promptError", out var promptError))
            {
                error = promptError;
            }
            else
            {
                frames.Add(line);
            }
        }

        return new AntigravityTurnScript { Frames = frames, Result = result, Error = error };
    }
}

/// <summary>Locates the Antigravity fixtures copied next to the test assembly.</summary>
internal static class AntigravityFixtures
{
    public const string SessionId = "antigravity-session-1";

    public static string PathOf(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Antigravity", fileName);
}

/// <summary>
/// A scripted ACP agent. It answers <c>initialize</c>, <c>session/new</c>, <c>session/load</c> and <c>session/prompt</c>, and
/// it records every frame the client writes so tests can check the wire.
/// </summary>
internal sealed class AntigravityScriptedAgent : IAcpTransport
{
    /// <summary>The initialize result shaped after the verified 1.5.17 capabilities (synthetic values).</summary>
    private const string InitializeResultJson = """
        {"protocolVersion":1,"agentInfo":{"name":"Antigravity ACP Server","version":"1.5.17"},
         "agentCapabilities":{"loadSession":true,"promptCapabilities":{"image":true},
           "sessionCapabilities":{"resume":{},"close":{},"load":{},"list":{}}},
         "authMethods":[{"id":"oauth-personal","name":"oauth-personal"},{"id":"oauth-business","name":"oauth-business"},
           {"id":"gemini-api-key","name":"gemini-api-key"},{"id":"agent-platform","name":"agent-platform"}]}
        """;

    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private readonly object _gate = new();
    private readonly List<JsonObject> _sent = [];
    private readonly Queue<AntigravityTurnScript> _turns = new();
    private readonly Queue<JsonNode> _openTurnIds = new();
    private readonly HashSet<string> _unknownSessionIds = new(StringComparer.Ordinal);

    /// <summary>When true, a prompt that arrives while a turn is open steers that turn, as the Antigravity docs describe.</summary>
    public bool SteerOpenTurns { get; init; }

    public string SessionId { get; init; } = AntigravityFixtures.SessionId;

    public bool IsConnected { get; private set; }

    /// <summary>Queues the script for the next <c>session/prompt</c>. Without a queued script the turn ends with one chunk.</summary>
    public AntigravityScriptedAgent Enqueue(params AntigravityTurnScript[] turns)
    {
        lock (_gate)
        {
            foreach (var turn in turns)
            {
                _turns.Enqueue(turn);
            }
        }

        return this;
    }

    /// <summary>Makes <c>session/load</c> fail with JSON-RPC -32002 for this id, the way the CLI does for an unknown session.</summary>
    public AntigravityScriptedAgent WithUnknownSession(string sessionId)
    {
        lock (_gate)
        {
            _unknownSessionIds.Add(sessionId);
        }

        return this;
    }

    /// <summary>Every frame the client wrote, in write order.</summary>
    public IReadOnlyList<JsonObject> SentFrames
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToArray();
            }
        }
    }

    /// <summary>The client requests with the given method (frames that have both a method and an id).</summary>
    public IReadOnlyList<JsonObject> RequestsFor(string method) =>
        SentFrames.Where(frame => frame["method"]?.GetValue<string>() == method && frame["id"] is not null).ToArray();

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var frame = JsonNode.Parse(message)!.AsObject();
        lock (_gate)
        {
            _sent.Add(frame);
        }

        var method = frame["method"]?.GetValue<string>();
        var id = frame["id"];
        if (method is null || id is null)
        {
            // An answer to an agent request, or a notification such as session/cancel. Recorded, not answered.
            return Task.CompletedTask;
        }

        switch (method)
        {
            case "initialize":
                Respond(id, JsonNode.Parse(InitializeResultJson));
                break;
            case "session/new":
                Respond(id, JsonSerializer.SerializeToNode(new { sessionId = SessionId, models = new { currentModelId = "agy-default", availableModels = Array.Empty<object>() }, modes = new { currentModeId = "default", availableModes = Array.Empty<object>() } }));
                break;
            case "session/load":
                HandleLoad(id, frame);
                break;
            case "session/prompt":
                HandlePrompt(id, frame);
                break;
            default:
                Respond(id, new JsonObject());
                break;
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_inbound.Reader.TryRead(out var message))
            {
                yield return message;
            }
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
        IsConnected = false;
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void HandleLoad(JsonNode id, JsonObject frame)
    {
        var sessionId = frame["params"]?["sessionId"]?.GetValue<string>() ?? string.Empty;
        bool unknown;
        lock (_gate)
        {
            unknown = _unknownSessionIds.Contains(sessionId);
        }

        if (unknown)
        {
            RespondError(id, -32002, $"Session not found: {sessionId}");
            return;
        }

        Respond(id, JsonNode.Parse("""{"models":{"currentModelId":"agy-default","availableModels":[]},"modes":{"currentModeId":"default","availableModes":[]}}"""));
    }

    private void HandlePrompt(JsonNode id, JsonObject frame)
    {
        lock (_gate)
        {
            if (SteerOpenTurns && _openTurnIds.Count > 0)
            {
                // A prompt during a running turn steers it: the open turn reports the steer and ends at once. The steer then
                // runs as its own turn, answered by the next queued script (or end_turn with no text when none is queued).
                var openId = _openTurnIds.Dequeue();
                var steerText = PromptText(frame);
                Write(SessionUpdate($"[steered] {steerText}"));
                Write(ResponseFrame(openId, JsonNode.Parse("""{"stopReason":"end_turn"}""")));
                AnswerTurn(id, _turns.Count > 0 ? _turns.Dequeue() : AntigravityTurnScript.EndTurn());
                return;
            }

            var script = _turns.Count > 0
                ? _turns.Dequeue()
                : AntigravityTurnScript.EndTurn(SessionUpdate("pong"));

            AnswerTurn(id, script);
        }
    }

    // Sends the script's frames, then its answer. A turn with no answer stays open, and an answer may wait for ResponseDelay.
    private void AnswerTurn(JsonNode id, AntigravityTurnScript script)
    {
        foreach (var frameText in script.Frames)
        {
            Write(frameText);
        }

        if (script.StaysOpen)
        {
            _openTurnIds.Enqueue(id.DeepClone());
            return;
        }

        var answer = script.Error is null
            ? ResponseFrame(id, script.Result)
            : ErrorFrame(id, script.Error);
        if (script.ResponseDelay <= TimeSpan.Zero)
        {
            Write(answer);
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(script.ResponseDelay).ConfigureAwait(false);
            lock (_gate)
            {
                Write(answer);
            }
        });
    }

    private void Respond(JsonNode id, JsonNode? result)
    {
        lock (_gate)
        {
            Write(ResponseFrame(id, result));
        }
    }

    private void RespondError(JsonNode id, int code, string message)
    {
        lock (_gate)
        {
            Write(ErrorFrame(id, JsonNode.Parse($$"""{"code":{{code}},"message":"{{message}}"}""")));
        }
    }

    private void Write(string frame) => _inbound.Writer.TryWrite(frame);

    private string SessionUpdate(string text) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = SessionId, update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } } } });

    private static string ResponseFrame(JsonNode id, JsonNode? result) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result?.DeepClone()
        }.ToJsonString();

    private static string ErrorFrame(JsonNode id, JsonNode? error) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = error?.DeepClone()
        }.ToJsonString();

    private static string PromptText(JsonObject frame)
    {
        var blocks = frame["params"]?["prompt"]?.AsArray();
        return blocks?.FirstOrDefault(static block => block?["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>() ?? string.Empty;
    }
}

/// <summary>An Antigravity provider whose ACP transport is the scripted agent. The process is never started.</summary>
internal sealed class ScriptedAntigravityProvider(AntigravityScriptedAgent agent)
    : AntigravityProvider(new StubAgyExecutableResolver(), new CliProcessManager(), new StubAgyRuntimeEnvironmentResolver())
{
    public ProcessStartContext? LastStartContext { get; private set; }

    protected override IAcpTransport CreateAcpTransport(ProcessStartContext startContext)
    {
        LastStartContext = startContext;
        return agent;
    }
}

internal sealed class StubAgyExecutableResolver : CliExecutableResolver
{
    public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        => executableName;

    public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        => executableNames.FirstOrDefault();
}

internal sealed class StubAgyRuntimeEnvironmentResolver : IRuntimeEnvironmentResolver
{
    public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>
        {
            ["PATH"] = "/tmp/bin"
        });
    }
}

/// <summary>Helpers shared by the Antigravity feature tests.</summary>
internal static class AntigravityTestHelpers
{
    public static async Task<List<CliMessage>> CollectAsync(IAsyncEnumerable<CliMessage> messages)
    {
        var collected = new List<CliMessage>();
        await foreach (var message in messages)
        {
            collected.Add(message);
        }

        return collected;
    }

    public static string TextOf(IEnumerable<CliMessage> messages) =>
        string.Concat(messages.Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.GetProperty("text").GetString()));

    public static string? StopReasonOf(CliMessage message) =>
        message.Content.TryGetProperty("stop_reason", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string? MessageOf(CliMessage message) =>
        message.Content.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static JsonNode PromptParamsOf(JsonObject frame) => frame["params"]!;
}
