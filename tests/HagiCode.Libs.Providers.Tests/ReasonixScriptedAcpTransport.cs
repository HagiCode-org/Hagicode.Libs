using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using HagiCode.Libs.Core.Acp;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// One line of a recorded ACP transcript. <see cref="Direction" /> is <c>in</c> for agent-to-client frames and
/// <c>out</c> for client-to-agent frames.
/// </summary>
internal sealed record ReasonixTranscriptRecord(string Direction, JsonObject Message)
{
    /// <summary>Gets the JSON-RPC method, or <c>null</c> for a response frame.</summary>
    public string? Method => Message["method"]?.GetValue<string>();

    /// <summary>Gets a value indicating whether the frame is a JSON-RPC response (result or error, no method).</summary>
    public bool IsResponse => Message["method"] is null && (Message["result"] is not null || Message["error"] is not null);
}

/// <summary>
/// Loads redacted Reasonix ACP transcripts recorded from the installed <c>reasonix v2.41.0</c> binary.
/// </summary>
internal static class ReasonixTranscriptFixtures
{
    /// <summary>The Reasonix release the committed transcripts were captured from.</summary>
    public const string ReasonixVersion = "v2.41.0";

    /// <summary>Loads one transcript by its file name without the <c>.jsonl</c> extension.</summary>
    public static IReadOnlyList<ReasonixTranscriptRecord> Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Reasonix", ReasonixVersion, name + ".jsonl");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Reasonix transcript fixture '{name}' was not copied to the test output.", path);
        }

        return File.ReadLines(path)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(static line => ParseRecord(line))
            .ToList();
    }

    private static ReasonixTranscriptRecord ParseRecord(string line)
    {
        var root = JsonNode.Parse(line)!.AsObject();
        var direction = root["dir"]!.GetValue<string>();
        var message = (JsonObject)root["msg"]!.DeepClone();
        return new ReasonixTranscriptRecord(direction, message);
    }
}

/// <summary>
/// Replays a recorded Reasonix ACP transcript through the real JSON-RPC client.
/// </summary>
/// <remarks>
/// Each client request is matched to the next recorded request with the same method. The agent frames that
/// follow it are delivered in order until the next recorded client request. Response frames are re-addressed
/// to the id the client actually sent, so request-id correlation is exercised. Client notifications and client
/// responses to agent requests are recorded but do not stop replay, unless <c>awaitAgentRequestAnswers</c> is set: then
/// replay pauses at each recorded client answer to an agent request (for example a <c>session/request_permission</c>)
/// until the client actually sends a response with that id, so later frames only arrive after the answer, as with Reasonix.
/// </remarks>
internal sealed class ReasonixScriptedAcpTransport : IAcpTransport
{
    private readonly IReadOnlyList<ReasonixTranscriptRecord> _transcript;
    private readonly Action<string, JsonObject>? _mutateResponse;
    private readonly IReadOnlySet<string> _failingMethods;
    private readonly bool _awaitAgentRequestAnswers;
    private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();
    private readonly List<JsonObject> _sent = [];
    private readonly object _sync = new();
    private int _cursor;
    private bool _connected;
    private string? _activeMethod;
    private JsonNode? _activeRecordedId;
    private JsonNode? _activeClientId;
    private JsonNode? _awaitedAnswerId;

    /// <summary>
    /// Initializes a new scripted transport.
    /// </summary>
    /// <param name="transcript">The recorded transcript to replay.</param>
    /// <param name="mutateResponse">Optional hook that edits a replayed response, given the request method it answers.</param>
    /// <param name="failingMethods">Request methods whose replayed response is replaced by a JSON-RPC error.</param>
    /// <param name="awaitAgentRequestAnswers">Pause replay at each recorded client answer until the client sends it.</param>
    public ReasonixScriptedAcpTransport(
        IReadOnlyList<ReasonixTranscriptRecord> transcript,
        Action<string, JsonObject>? mutateResponse = null,
        IEnumerable<string>? failingMethods = null,
        bool awaitAgentRequestAnswers = false)
    {
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _mutateResponse = mutateResponse;
        _failingMethods = new HashSet<string>(failingMethods ?? [], StringComparer.Ordinal);
        _awaitAgentRequestAnswers = awaitAgentRequestAnswers;
    }

    /// <summary>Gets every outgoing frame the provider sent, in order.</summary>
    public IReadOnlyList<JsonObject> SentMessages
    {
        get
        {
            lock (_sync)
            {
                return _sent.ToArray();
            }
        }
    }

    /// <summary>Gets the method of every outgoing request or notification, in order.</summary>
    public IReadOnlyList<string> SentMethods =>
        SentMessages.Select(static message => message["method"]?.GetValue<string>()).OfType<string>().ToArray();

    /// <summary>Gets the client responses to agent requests (frames with an id and no method), in order.</summary>
    public IReadOnlyList<JsonObject> SentAnswers =>
        SentMessages.Where(static message => message["method"] is null && message["id"] is not null).ToArray();

    /// <summary>Gets a value indicating whether the transport was closed, which terminates the Reasonix process.</summary>
    public bool IsTerminated { get; private set; }

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var sent = JsonNode.Parse(message)!.AsObject();
        lock (_sync)
        {
            _sent.Add(sent);

            var method = sent["method"]?.GetValue<string>();
            if (method is null || sent["id"] is null)
            {
                // Client responses to agent requests and notifications do not stop replay; an awaited answer resumes it.
                if (method is null && _awaitedAnswerId is not null && JsonNode.DeepEquals(sent["id"], _awaitedAnswerId))
                {
                    _awaitedAnswerId = null;
                    _cursor++;
                    ReplayFromCursor();
                }

                return Task.CompletedTask;
            }

            ReplayRequest(method, sent);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ReceiveMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        IsTerminated = true;
        _frames.Writer.TryComplete();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    private void ReplayRequest(string method, JsonObject sent)
    {
        var requestIndex = FindNextRecordedRequest(method);
        if (requestIndex < 0)
        {
            throw new InvalidOperationException(
                $"The transcript has no remaining client request '{method}' after record {_cursor}.");
        }

        _activeMethod = method;
        _activeRecordedId = _transcript[requestIndex].Message["id"]?.DeepClone();
        _activeClientId = sent["id"]!.DeepClone();
        _awaitedAnswerId = null;

        _cursor = requestIndex + 1;
        ReplayFromCursor();
    }

    private void ReplayFromCursor()
    {
        var method = _activeMethod!;
        var recordedId = _activeRecordedId;
        var clientId = _activeClientId!;
        var failRequest = _failingMethods.Contains(method);

        for (; _cursor < _transcript.Count; _cursor++)
        {
            var record = _transcript[_cursor];
            if (record.Direction == "out")
            {
                if (record.Message["id"] is not null && record.Method is not null)
                {
                    break;
                }

                if (_awaitAgentRequestAnswers && record.IsResponse)
                {
                    // Wait for the client's own answer before the agent continues.
                    _awaitedAnswerId = record.Message["id"]!.DeepClone();
                    return;
                }

                continue;
            }

            if (record.IsResponse && JsonNode.DeepEquals(record.Message["id"], recordedId))
            {
                _frames.Writer.TryWrite(failRequest
                    ? CreateSimulatedFailure(clientId, method)
                    : CreateReplayedResponse(method, record.Message, clientId));
                continue;
            }

            _frames.Writer.TryWrite(record.Message.DeepClone().ToJsonString());
        }
    }

    private string CreateReplayedResponse(string method, JsonObject recorded, JsonNode clientId)
    {
        var frame = (JsonObject)recorded.DeepClone();
        frame["id"] = clientId.DeepClone();
        _mutateResponse?.Invoke(method, frame);
        return frame.ToJsonString();
    }

    private static string CreateSimulatedFailure(JsonNode clientId, string method)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = clientId.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = -32603,
                ["message"] = $"simulated {method} failure"
            }
        }.ToJsonString();
    }

    private int FindNextRecordedRequest(string method)
    {
        for (var index = _cursor; index < _transcript.Count; index++)
        {
            var record = _transcript[index];
            if (record.Direction == "out" && record.Method == method && record.Message["id"] is not null)
            {
                return index;
            }
        }

        return -1;
    }
}
