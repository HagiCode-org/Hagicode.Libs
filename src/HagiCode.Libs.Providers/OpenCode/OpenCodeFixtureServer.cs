using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Options for the in-process OpenCode 2.0 simulation used by tests.
/// </summary>
public sealed class OpenCodeFixtureServerOptions
{
    public string Hostname { get; init; } = "127.0.0.1";

    public int? Port { get; init; }

    public string Version { get; init; } = "2.0.32";

    public string AssistantPrefix { get; init; } = "READY";

    /// <summary>
    /// The password the simulated server requires. When <c>null</c>, a random password is generated.
    /// </summary>
    public string? ServerPassword { get; init; }

    /// <summary>
    /// When <c>true</c> (the default, as in OpenCode 2.0), every <c>/api/*</c> request needs a valid Basic credential.
    /// </summary>
    public bool RequireAuthentication { get; init; } = true;

    /// <summary>Models whose prompt completes without any assistant text.</summary>
    public IReadOnlyCollection<string> EmptyTextModelIds { get; init; } = Array.Empty<string>();

    /// <summary>Models whose prompt answers with an empty body.</summary>
    public IReadOnlyCollection<string> EmptyBodyModelIds { get; init; } = Array.Empty<string>();

    /// <summary>Models rejected when they are selected for a session.</summary>
    public IReadOnlyCollection<string> InvalidModelIds { get; init; } = Array.Empty<string>();

    /// <summary>Models whose prompt answers with a non-JSON body.</summary>
    public IReadOnlyCollection<string> NonJsonModelIds { get; init; } = Array.Empty<string>();

    /// <summary>Models whose turn ends with <c>session.execution.failed</c>.</summary>
    public IReadOnlyCollection<string> FailingModelIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Simulates OpenCode 2.0: the startup lines, Basic authentication, the <c>/api/*</c> routes,
/// the <c>/api/event</c> stream, and the Web UI HTML shell (HTTP 200) on every other path.
/// </summary>
public static class OpenCodeFixtureServer
{
    private const string Username = "opencode";
    private const string LegacyHtml = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>OpenCode</title></head><body><div id=\"root\"></div></body></html>";

    public static Task<OpenCodeFixtureServerHandle> StartAsync(
        OpenCodeFixtureServerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new OpenCodeFixtureServerOptions();
        var password = string.IsNullOrEmpty(options.ServerPassword)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
            : options.ServerPassword;
        var port = options.Port ?? OpenCodeProcessLauncher.GetFreeTcpPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{options.Hostname}:{port}/");
        listener.Start();

        var baseUri = new Uri($"http://{options.Hostname}:{port}/");
        var state = new FixtureState(options, password, baseUri);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loopTask = Task.Run(() => AcceptLoopAsync(listener, state, cts.Token), CancellationToken.None);
        var startupOutput = $"server listening on {baseUri.ToString().TrimEnd('/')}{Environment.NewLine}server password {password}{Environment.NewLine}";
        return Task.FromResult(new OpenCodeFixtureServerHandle(listener, baseUri, loopTask, cts, password, startupOutput));
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
            var method = request.HttpMethod;

            if (!IsApiPath(path))
            {
                // OpenCode 2.0 serves the Web UI shell with HTTP 200 for every non-API route, including the legacy ones.
                await WriteAsync(context.Response, 200, "text/html; charset=utf-8", LegacyHtml, cancellationToken);
                return;
            }

            if (state.Options.RequireAuthentication && !IsAuthorized(request, state.Password))
            {
                await WriteAsync(
                    context.Response,
                    401,
                    "application/json",
                    JsonSerializer.Serialize(new { _tag = "UnauthorizedError", message = "Authentication required" }),
                    cancellationToken);
                return;
            }

            if (method == "GET" && path == "/api/info")
            {
                await WriteJsonAsync(context.Response, 200, new
                {
                    version = state.Options.Version,
                    pid = Environment.ProcessId,
                    urls = new[] { state.BaseUri.ToString().TrimEnd('/') },
                    paths = new { tmp = "/tmp/opencode" },
                    capabilities = new { persistentPty = true },
                }, cancellationToken);
                return;
            }

            if (method == "GET" && path == "/api/project")
            {
                await WriteJsonAsync(context.Response, 200, Array.Empty<object>(), cancellationToken);
                return;
            }

            if (method == "GET" && path == "/api/fs/list")
            {
                await HandleFileListAsync(context.Response, request, cancellationToken);
                return;
            }

            if (method == "GET" && path == "/api/session")
            {
                var sessions = state.Sessions.Values.Select(session => session.Definition).ToArray();
                await WriteJsonAsync(context.Response, 200, new { data = sessions, cursor = new { previous = (string?)null, next = (string?)null } }, cancellationToken);
                return;
            }

            if (method == "POST" && path == "/api/session")
            {
                var body = await ReadBodyAsync(request, cancellationToken);
                using var document = body.Length == 0 ? null : JsonDocument.Parse(body);
                var title = TryGetString(document?.RootElement, "title");
                var directory = TryGetNestedString(document?.RootElement, "location", "directory");
                var number = Interlocked.Increment(ref state.NextSessionId);
                var newSessionId = $"ses_{number:D8}";
                var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var definition = new
                {
                    id = newSessionId,
                    projectID = "fixture-project",
                    title = string.IsNullOrWhiteSpace(title) ? $"Fixture Session {number}" : title,
                    location = directory is null ? null : new { directory },
                    time = new { created, updated = created },
                };
                state.Sessions[newSessionId] = new FixtureSession(newSessionId, definition);
                await WriteJsonAsync(context.Response, 200, new { data = definition }, cancellationToken);
                return;
            }

            if (method == "GET" && path == "/api/event")
            {
                await StreamEventsAsync(context.Response, state, cancellationToken);
                return;
            }

            if (TryMatchSessionRoute(path, out var sessionId, out var action))
            {
                if (!state.Sessions.TryGetValue(sessionId, out var session))
                {
                    await WriteJsonAsync(context.Response, 404, new { _tag = "SessionNotFoundError", message = $"Session not found: {sessionId}" }, cancellationToken);
                    return;
                }

                if (method == "POST" && action == "model")
                {
                    await HandleSetModelAsync(context.Response, request, session, state, cancellationToken);
                    return;
                }

                if (method == "POST" && action == "prompt")
                {
                    await HandlePromptAsync(context.Response, request, session, state, cancellationToken);
                    return;
                }

                if (method == "GET" && action == "message")
                {
                    await WriteJsonAsync(context.Response, 200, new { data = session.Messages.ToArray(), cursor = new { previous = (string?)null, next = (string?)null } }, cancellationToken);
                    return;
                }
            }

            await WriteJsonAsync(context.Response, 404, new { _tag = "NotFoundError", message = $"Not found: {method} {path}" }, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteAsync(context.Response, 500, "text/plain; charset=utf-8", ex.Message, cancellationToken);
        }
        finally
        {
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.Close();
            }
        }
    }

    private static async Task HandleSetModelAsync(
        HttpListenerResponse response,
        HttpListenerRequest request,
        FixtureSession session,
        FixtureState state,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ReadBodyAsync(request, cancellationToken));
        if (!document.RootElement.TryGetProperty("model", out var model))
        {
            await WriteJsonAsync(response, 400, new { _tag = "InvalidRequestError", message = "Missing key\n  at [\"model\"]", kind = "Payload" }, cancellationToken);
            return;
        }

        var providerId = TryGetString(model, "providerID") ?? string.Empty;
        var modelId = TryGetString(model, "id") ?? string.Empty;
        if (Matches(state.Options.InvalidModelIds, providerId, modelId))
        {
            await WriteJsonAsync(response, 400, new { _tag = "ProviderModelNotFoundError", message = $"ProviderModelNotFoundError: {providerId}/{modelId}" }, cancellationToken);
            return;
        }

        session.Model = (providerId, modelId);
        response.StatusCode = 204;
    }

    private static async Task HandlePromptAsync(
        HttpListenerResponse response,
        HttpListenerRequest request,
        FixtureSession session,
        FixtureState state,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ReadBodyAsync(request, cancellationToken));
        var root = document.RootElement;
        var promptText = TryGetString(root, "text") ?? string.Empty;
        var requestedId = TryGetString(root, "id");
        if (!string.IsNullOrWhiteSpace(requestedId) && !requestedId.StartsWith("msg", StringComparison.Ordinal))
        {
            await WriteJsonAsync(response, 400, new { _tag = "InvalidRequestError", message = $"Invalid string: must start with \"msg\" (id={requestedId})", kind = "Payload" }, cancellationToken);
            return;
        }

        var (providerId, modelId) = session.Model ?? (string.Empty, string.Empty);
        var hasModel = session.Model is not null;
        if (hasModel && Matches(state.Options.NonJsonModelIds, providerId, modelId))
        {
            await WriteAsync(response, 200, "text/plain; charset=utf-8", "not-json", cancellationToken);
            return;
        }

        if (hasModel && Matches(state.Options.EmptyBodyModelIds, providerId, modelId))
        {
            await WriteAsync(response, 200, "application/json", string.Empty, cancellationToken);
            return;
        }

        var sessionId = session.Id;
        var inboxId = $"msg_{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        session.Messages.Add(new { id = inboxId, time = new { created }, type = "user", text = promptText });

        state.Publish("session.inbox.enqueued", new { inboxID = inboxId, sessionID = sessionId });
        state.Publish("session.execution.started", new { sessionID = sessionId });

        if (hasModel && Matches(state.Options.FailingModelIds, providerId, modelId))
        {
            state.Publish("session.execution.failed", new
            {
                sessionID = sessionId,
                error = new { type = "provider.no-route", message = $"Model unavailable: {providerId}/{modelId}" },
            });
        }
        else
        {
            var assistantMessageId = $"msg_{Guid.NewGuid():N}";
            if (hasModel && Matches(state.Options.EmptyTextModelIds, providerId, modelId))
            {
                state.Publish("session.step.ended", new { sessionID = sessionId, assistantMessageID = assistantMessageId, finish = "stop", rawFinish = "stop" });
            }
            else
            {
                var assistantText = BuildAssistantText(promptText, state.Options.AssistantPrefix);
                state.Publish("session.text.started", new { sessionID = sessionId, assistantMessageID = assistantMessageId, ordinal = 0 });
                state.Publish("session.text.delta", new { sessionID = sessionId, assistantMessageID = assistantMessageId, ordinal = 0, delta = assistantText });
                state.Publish("session.text.ended", new { sessionID = sessionId, assistantMessageID = assistantMessageId, ordinal = 0, text = assistantText });
                state.Publish("session.step.ended", new { sessionID = sessionId, assistantMessageID = assistantMessageId, finish = "stop", rawFinish = "stop" });
                session.Messages.Add(new { id = assistantMessageId, time = new { created }, type = "assistant", content = new[] { new { type = "text", text = assistantText } }, finish = "stop" });
            }

            state.Publish("session.execution.succeeded", new { sessionID = sessionId });
        }

        await WriteJsonAsync(response, 200, new
        {
            data = new
            {
                id = inboxId,
                sessionID = sessionId,
                time = new { created },
                type = "user",
                payload = new { text = promptText },
                delivery = "steer",
            },
        }, cancellationToken);
    }

    private static async Task StreamEventsAsync(HttpListenerResponse response, FixtureState state, CancellationToken cancellationToken)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        var channel = Channel.CreateUnbounded<string>();
        state.Subscribe(channel);
        try
        {
            await using var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(new { id = $"evt_{Guid.NewGuid():N}", type = "server.connected", data = new { } })}\n\n");
            await writer.FlushAsync(cancellationToken);
            await foreach (var json in channel.Reader.ReadAllAsync(cancellationToken))
            {
                await writer.WriteAsync($"data: {json}\n\n");
                await writer.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (HttpListenerException)
        {
        }
        finally
        {
            state.Unsubscribe(channel);
        }
    }

    private static async Task HandleFileListAsync(HttpListenerResponse response, HttpListenerRequest request, CancellationToken cancellationToken)
    {
        var locationDirectory = request.QueryString["location[directory]"];
        var relativePath = request.QueryString["path"] ?? string.Empty;
        var root = string.IsNullOrWhiteSpace(locationDirectory) ? Directory.GetCurrentDirectory() : locationDirectory;
        var target = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!Directory.Exists(target))
        {
            await WriteJsonAsync(response, 404, new { _tag = "FileNotFoundError", path = target, message = $"Directory not found: {target}" }, cancellationToken);
            return;
        }

        var entries = Directory.EnumerateFileSystemEntries(target)
            .Select(entry => new
            {
                path = Path.GetRelativePath(target, entry),
                type = Directory.Exists(entry) ? "directory" : "file",
            })
            .ToArray();
        await WriteJsonAsync(response, 200, new { location = new { directory = root }, data = entries }, cancellationToken);
    }

    private static bool IsApiPath(string path)
    {
        return path == "/api" || path.StartsWith("/api/", StringComparison.Ordinal);
    }

    private static bool IsAuthorized(HttpListenerRequest request, string password)
    {
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{password}"));
        return string.Equals(request.Headers["Authorization"], expected, StringComparison.Ordinal);
    }

    private static bool TryMatchSessionRoute(string path, out string sessionId, out string action)
    {
        const string prefix = "/api/session/";
        sessionId = string.Empty;
        action = string.Empty;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        sessionId = Uri.UnescapeDataString(parts[0]);
        action = parts[1];
        return true;
    }

    private static bool Matches(IReadOnlyCollection<string> configuredValues, string providerId, string modelId)
    {
        if (configuredValues.Count == 0)
        {
            return false;
        }

        var qualified = string.IsNullOrEmpty(providerId) ? modelId : $"{providerId}/{modelId}";
        return configuredValues.Any(candidate =>
            string.Equals(candidate, qualified, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, modelId, StringComparison.OrdinalIgnoreCase));
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
            return assistantPrefix;
        }

        return $"{assistantPrefix}: {promptText}";
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string? TryGetString(JsonElement? element, string propertyName)
    {
        return element is { ValueKind: JsonValueKind.Object } value &&
               value.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? TryGetNestedString(JsonElement? element, string outer, string inner)
    {
        if (element is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty(outer, out var nested))
        {
            return null;
        }

        return TryGetString(nested, inner);
    }

    private static Task WriteJsonAsync<T>(HttpListenerResponse response, int statusCode, T payload, CancellationToken cancellationToken)
    {
        return WriteAsync(response, statusCode, "application/json", JsonSerializer.Serialize(payload), cancellationToken);
    }

    private static async Task WriteAsync(
        HttpListenerResponse response,
        int statusCode,
        string contentType,
        string body,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    private sealed class FixtureState
    {
        private readonly List<Channel<string>> _subscribers = [];
        private readonly object _subscriberLock = new();

        public FixtureState(OpenCodeFixtureServerOptions options, string password, Uri baseUri)
        {
            Options = options;
            Password = password;
            BaseUri = baseUri;
        }

        public OpenCodeFixtureServerOptions Options { get; }

        public string Password { get; }

        public Uri BaseUri { get; }

        public ConcurrentDictionary<string, FixtureSession> Sessions { get; } = new(StringComparer.Ordinal);

        public long NextSessionId;

        public void Subscribe(Channel<string> channel)
        {
            lock (_subscriberLock)
            {
                _subscribers.Add(channel);
            }
        }

        public void Unsubscribe(Channel<string> channel)
        {
            lock (_subscriberLock)
            {
                _subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        }

        public void Publish(string type, object data)
        {
            var json = JsonSerializer.Serialize(new
            {
                id = $"evt_{Guid.NewGuid():N}",
                created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                type,
                data,
            });

            lock (_subscriberLock)
            {
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryWrite(json);
                }
            }
        }
    }

    private sealed class FixtureSession
    {
        public FixtureSession(string id, object definition)
        {
            Id = id;
            Definition = definition;
        }

        public object Definition { get; }

        public string Id { get; }

        public (string ProviderId, string ModelId)? Model { get; set; }

        public List<object> Messages { get; } = [];
    }
}

public sealed class OpenCodeFixtureServerHandle : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loopTask;
    private readonly CancellationTokenSource _cts;
    private readonly string _startupOutput;
    private readonly object _sync = new();
    private int _disposed;

    internal OpenCodeFixtureServerHandle(
        HttpListener listener,
        Uri baseUri,
        Task loopTask,
        CancellationTokenSource cts,
        string serverPassword,
        string startupOutput)
    {
        _listener = listener;
        BaseUri = baseUri;
        _loopTask = loopTask;
        _cts = cts;
        ServerPassword = serverPassword;
        _startupOutput = startupOutput;
    }

    public Uri BaseUri { get; }

    /// <summary>
    /// Gets the password the simulated server requires. Test code uses it to configure attach mode.
    /// </summary>
    public string ServerPassword { get; }

    public int? ProcessId => null;

    /// <summary>
    /// Gets the startup output with the password redacted, in the same form an owned OpenCode 2.0 runtime prints.
    /// </summary>
    public string? CapturedOutput => OpenCodeSecretRedactor.Redact(_startupOutput, ServerPassword);

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
