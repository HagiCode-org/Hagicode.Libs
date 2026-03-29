using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace HagiCode.Libs.Providers.OpenCode;

public sealed class OpenCodeFixtureServerOptions
{
    public string Hostname { get; init; } = "127.0.0.1";

    public int? Port { get; init; }

    public string Version { get; init; } = "fixture-1.0.0";

    public string AssistantPrefix { get; init; } = "READY";

    public IReadOnlyCollection<string> EmptyBodyModelIds { get; init; } = Array.Empty<string>();

    public IReadOnlyCollection<string> EmptyTextModelIds { get; init; } = Array.Empty<string>();

    public IReadOnlyCollection<string> InvalidModelIds { get; init; } = Array.Empty<string>();

    public IReadOnlyCollection<string> NonJsonModelIds { get; init; } = Array.Empty<string>();

    public bool EmptyBodyWhenModelMissing { get; init; }

    public bool NonJsonWhenModelMissing { get; init; }
}

public static class OpenCodeFixtureServer
{
    public static Task<OpenCodeFixtureServerHandle> StartAsync(
        OpenCodeFixtureServerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new OpenCodeFixtureServerOptions();
        var port = options.Port ?? OpenCodeProcessLauncher.GetFreeTcpPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{options.Hostname}:{port}/");
        listener.Start();

        var state = new FixtureState(options);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loopTask = Task.Run(() => AcceptLoopAsync(listener, state, cts.Token), CancellationToken.None);
        var baseUri = new Uri($"http://{options.Hostname}:{port}/");
        return Task.FromResult(new OpenCodeFixtureServerHandle(listener, state, baseUri, loopTask, cts));
    }

    private static async Task AcceptLoopAsync(HttpListener listener, FixtureState state, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (context is not null)
            {
                _ = Task.Run(() => HandleAsync(context, state, cancellationToken), CancellationToken.None);
            }
        }
    }

    private static async Task HandleAsync(HttpListenerContext context, FixtureState state, CancellationToken cancellationToken)
    {
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "/";

            if (request.HttpMethod == "GET" && path == "/global/health")
            {
                await WriteJsonAsync(context.Response, new OpenCodeHealthResponse
                {
                    Healthy = true,
                    Version = state.Options.Version,
                }, cancellationToken);
                return;
            }

            if (request.HttpMethod == "GET" && path == "/project")
            {
                await WriteJsonAsync(context.Response, Array.Empty<OpenCodeProject>(), cancellationToken);
                return;
            }

            if (request.HttpMethod == "GET" && path == "/session")
            {
                var sessions = state.Sessions.Values.Select(session => session.Definition).ToArray();
                await WriteJsonAsync(context.Response, sessions, cancellationToken);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/session")
            {
                var createRequest = await JsonSerializer.DeserializeAsync<OpenCodeSessionCreateRequest>(request.InputStream, cancellationToken: cancellationToken)
                    ?? new OpenCodeSessionCreateRequest();
                var sessionId = $"session-{Interlocked.Increment(ref state.NextSessionId):D4}";
                var definition = new OpenCodeSession
                {
                    Id = sessionId,
                    Title = string.IsNullOrWhiteSpace(createRequest.Title) ? $"Fixture Session {state.NextSessionId}" : createRequest.Title,
                    Directory = ReadStringQuery(request, "directory"),
                    WorkspaceId = ReadStringQuery(request, "workspace"),
                    ProjectId = "fixture-project"
                };
                state.Sessions[sessionId] = new FixtureSession(definition);
                await WriteJsonAsync(context.Response, definition, cancellationToken);
                return;
            }

            if (request.HttpMethod == "GET" && path == "/event")
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                await using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false));
                await writer.WriteAsync("event: session.updated\n");
                await writer.WriteAsync("data: {\"source\":\"fixture\",\"status\":\"ok\"}\n\n");
                await writer.FlushAsync(cancellationToken);
                context.Response.Close();
                return;
            }

            if (TryMatchSessionMessages(path, out var sessionIdForMessages))
            {
                if (!state.Sessions.TryGetValue(sessionIdForMessages, out var session))
                {
                    await WriteNotFoundAsync(context.Response, cancellationToken);
                    return;
                }

                if (request.HttpMethod == "GET")
                {
                    await WriteJsonAsync(context.Response, session.Messages.ToArray(), cancellationToken);
                    return;
                }

                if (request.HttpMethod == "POST")
                {
                    var promptRequest = await JsonSerializer.DeserializeAsync<OpenCodeSessionPromptRequest>(request.InputStream, cancellationToken: cancellationToken)
                        ?? new OpenCodeSessionPromptRequest();
                    if (!string.IsNullOrWhiteSpace(promptRequest.MessageId) &&
                        !promptRequest.MessageId.StartsWith("msg", StringComparison.Ordinal))
                    {
                        await WriteBadRequestAsync(
                            context.Response,
                            $"Invalid string: must start with \"msg\" (messageID={promptRequest.MessageId})",
                            cancellationToken);
                        return;
                    }

                    if (promptRequest.Model is not null)
                    {
                        session.LastModel = promptRequest.Model;
                    }

                    var effectiveModel = promptRequest.Model ?? session.LastModel;
                    var effectiveModelId = effectiveModel?.ModelId;
                    var effectiveRawModel = BuildEffectiveRawModel(effectiveModel);
                    if (effectiveModel == null && state.Options.NonJsonWhenModelMissing)
                    {
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "text/plain";
                        await WriteStringAsync(context.Response, "not-json", cancellationToken);
                        return;
                    }

                    if (effectiveModel == null && state.Options.EmptyBodyWhenModelMissing)
                    {
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        return;
                    }

                    if (MatchesConfiguredModel(state.Options.InvalidModelIds, effectiveModelId, effectiveRawModel))
                    {
                        await WriteBadRequestAsync(
                            context.Response,
                            $"ProviderModelNotFoundError: invalid-model ({effectiveRawModel ?? effectiveModelId ?? "unknown-model"})",
                            cancellationToken);
                        return;
                    }

                    if (MatchesConfiguredModel(state.Options.NonJsonModelIds, effectiveModelId, effectiveRawModel))
                    {
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "text/plain";
                        await WriteStringAsync(context.Response, "not-json", cancellationToken);
                        return;
                    }

                    if (MatchesConfiguredModel(state.Options.EmptyBodyModelIds, effectiveModelId, effectiveRawModel))
                    {
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        return;
                    }

                    if (MatchesConfiguredModel(state.Options.EmptyTextModelIds, effectiveModelId, effectiveRawModel))
                    {
                        var diagnosticAssistantMessage = CreateDiagnosticMessage(
                            sessionIdForMessages,
                            $"message-{Guid.NewGuid():N}",
                            "assistant",
                            "OpenCode fixture emitted an assistant envelope without text content.",
                            "error");
                        session.Messages.Add(diagnosticAssistantMessage);
                        await WriteJsonAsync(context.Response, diagnosticAssistantMessage, cancellationToken);
                        return;
                    }

                    var promptText = promptRequest.Parts.FirstOrDefault(part => string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase))?.Text
                        ?? string.Empty;
                    var userMessage = CreateTextMessage(sessionIdForMessages, $"message-{Guid.NewGuid():N}", "user", promptText);
                    var assistantText = BuildAssistantText(promptText, state.Options.AssistantPrefix);
                    var assistantMessage = CreateTextMessage(sessionIdForMessages, $"message-{Guid.NewGuid():N}", "assistant", assistantText);
                    session.Messages.Add(userMessage);
                    session.Messages.Add(assistantMessage);
                    await WriteJsonAsync(context.Response, assistantMessage, cancellationToken);
                    return;
                }
            }

            if (request.HttpMethod == "GET" && path == "/file")
            {
                await WriteJsonAsync(context.Response, Array.Empty<OpenCodeFileNode>(), cancellationToken);
                return;
            }

            await WriteNotFoundAsync(context.Response, cancellationToken);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await WriteStringAsync(context.Response, ex.Message, cancellationToken);
        }
        finally
        {
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.Close();
            }
        }
    }

    private static OpenCodeMessageEnvelope CreateTextMessage(string sessionId, string messageId, string role, string text)
    {
        return new OpenCodeMessageEnvelope
        {
            Info = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["id"] = messageId,
                ["sessionID"] = sessionId,
                ["role"] = role,
            }),
            Parts =
            [
                JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = text,
                })
            ]
        };
    }

    private static OpenCodeMessageEnvelope CreateDiagnosticMessage(
        string sessionId,
        string messageId,
        string role,
        string message,
        string reason)
    {
        return new OpenCodeMessageEnvelope
        {
            Info = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["id"] = messageId,
                ["sessionID"] = sessionId,
                ["role"] = role,
            }),
            Parts =
            [
                JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["type"] = "step-finish",
                    ["reason"] = reason,
                }),
                JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["type"] = "error",
                    ["message"] = message,
                })
            ]
        };
    }

    private static async Task WriteJsonAsync<T>(HttpListenerResponse response, T payload, CancellationToken cancellationToken)
    {
        response.StatusCode = 200;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, payload, cancellationToken: cancellationToken);
    }

    private static async Task WriteNotFoundAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        response.StatusCode = 404;
        await WriteStringAsync(response, "Not Found", cancellationToken);
    }

    private static async Task WriteBadRequestAsync(
        HttpListenerResponse response,
        string message,
        CancellationToken cancellationToken)
    {
        response.StatusCode = 400;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            response.OutputStream,
            new
            {
                success = false,
                error = new[]
                {
                    new
                    {
                        message,
                    }
                }
            },
            cancellationToken: cancellationToken);
    }

    private static async Task WriteStringAsync(HttpListenerResponse response, string value, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteAsync(value.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static bool TryMatchSessionMessages(string path, out string sessionId)
    {
        sessionId = string.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 3 &&
            string.Equals(segments[0], "session", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[2], "message", StringComparison.OrdinalIgnoreCase))
        {
            sessionId = Uri.UnescapeDataString(segments[1]);
            return true;
        }

        return false;
    }

    private static string? ReadStringQuery(HttpListenerRequest request, string key)
    {
        var value = request.QueryString[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool MatchesConfiguredModel(
        IReadOnlyCollection<string> configuredValues,
        string? modelId,
        string? rawModel)
    {
        if (configuredValues.Count == 0)
        {
            return false;
        }

        return configuredValues.Any(candidate =>
            (!string.IsNullOrWhiteSpace(modelId)
             && string.Equals(candidate, modelId, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(rawModel)
                && string.Equals(candidate, rawModel, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? BuildEffectiveRawModel(OpenCodeModelSelection? model)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.ModelId))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(model.ProviderId)
            ? model.ModelId
            : $"{model.ProviderId}/{model.ModelId}";
    }

    private static string BuildAssistantText(string promptText, string assistantPrefix)
    {
        if (promptText.Contains("PCode.slnx", StringComparison.OrdinalIgnoreCase) &&
            promptText.Contains("PCode.Provider.Testing", StringComparison.OrdinalIgnoreCase))
        {
            return """
Repository analysis complete.

The solution file `PCode.slnx` is at the root of the `hagicode-core` repository.
The canonical OpenCode console project `HagiCode.Libs.OpenCode.Console` owns the provider validation scenarios.

- `HagiCode.Libs.OpenCode.Console` runs OpenCode integration scenarios such as bootstrap, prompt round-trip, repository analysis, and diagnostics.
- The OpenCode test console verifies live or fixture-backed repository investigation workflows without editing repository files.
""";
        }

        if (promptText.Contains("READY", StringComparison.OrdinalIgnoreCase))
        {
            return $"{assistantPrefix}";
        }

        return $"{assistantPrefix}: {promptText}";
    }

    private sealed class FixtureState
    {
        public FixtureState(OpenCodeFixtureServerOptions options)
        {
            Options = options;
        }

        public OpenCodeFixtureServerOptions Options { get; }

        public ConcurrentDictionary<string, FixtureSession> Sessions { get; } = new(StringComparer.Ordinal);

        public int NextSessionId;
    }

    private sealed class FixtureSession
    {
        public FixtureSession(OpenCodeSession definition)
        {
            Definition = definition;
        }

        public OpenCodeSession Definition { get; }

        public List<OpenCodeMessageEnvelope> Messages { get; } = [];

        public OpenCodeModelSelection? LastModel { get; set; }
    }
}

public sealed class OpenCodeFixtureServerHandle : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loopTask;
    private readonly CancellationTokenSource _cts;
    private readonly object _sync = new();
    private int _disposed;

    internal OpenCodeFixtureServerHandle(
        HttpListener listener,
        object state,
        Uri baseUri,
        Task loopTask,
        CancellationTokenSource cts)
    {
        _listener = listener;
        _ = state;
        BaseUri = baseUri;
        _loopTask = loopTask;
        _cts = cts;
    }

    public Uri BaseUri { get; }

    public int? ProcessId => null;

    public string? CapturedOutput => "fixture-server";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_sync)
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }

        try
        {
            await _loopTask;
        }
        catch
        {
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
