using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Providers.Dsh;

internal sealed class DshAcpSessionClient : IAcpSessionClient
{
    private const string ModelConfigId = "model";
    private readonly IAcpSessionClient _inner;
    private readonly string? _provider;
    private bool _supportsImageInput;

    public DshAcpSessionClient(IAcpSessionClient inner, string? provider)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _provider = ArgumentValueNormalizer.NormalizeOptionalValue(provider);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        return _inner.ConnectAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a value indicating whether the connection advertised <c>promptCapabilities.image</c> in <c>initialize</c>.
    /// A missing or non-boolean value counts as <see langword="false"/>. The value is per connection and is false until
    /// <see cref="InitializeAsync"/> has completed. It is not a statement about the model route.
    /// </summary>
    public bool SupportsImageInput => _supportsImageInput;

    /// <summary>
    /// Records that dsh rejected inline images on this connection, so later prompts on it are text-only.
    /// </summary>
    public void MarkImageInputUnsupported()
    {
        _supportsImageInput = false;
    }

    public async Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var initializeResult = await _inner.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _supportsImageInput = ParsePromptImageCapability(initializeResult);
        return initializeResult;
    }

    public Task<JsonElement> InvokeBootstrapMethodAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        return _inner.InvokeBootstrapMethodAsync(method, parameters, cancellationToken);
    }

    public async Task<AcpSessionHandle> StartSessionAsync(
        string workingDirectory,
        string? sessionId,
        string? model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var requestedSessionId = ArgumentValueNormalizer.NormalizeOptionalValue(sessionId);
        var normalizedModel = ArgumentValueNormalizer.NormalizeOptionalValue(model);
        var modelSelectionValue = normalizedModel is null ? null : ToModelSelectionValue(normalizedModel);
        JsonElement sessionResult;
        string resolvedSessionId;
        if (requestedSessionId is not null)
        {
            sessionResult = await _inner.InvokeBootstrapMethodAsync(
                "session/resume",
                new
                {
                    sessionId = requestedSessionId,
                    cwd = workingDirectory,
                    mcpServers = Array.Empty<object>()
                },
                cancellationToken).ConfigureAwait(false);
            resolvedSessionId = requestedSessionId;
        }
        else
        {
            sessionResult = await _inner.InvokeBootstrapMethodAsync(
                "session/new",
                new
                {
                    cwd = workingDirectory,
                    mcpServers = Array.Empty<object>()
                },
                cancellationToken).ConfigureAwait(false);
            resolvedSessionId = ParseSessionId(sessionResult);
        }

        if (modelSelectionValue is not null)
        {
            await _inner.InvokeBootstrapMethodAsync(
                "session/set_config_option",
                new
                {
                    sessionId = resolvedSessionId,
                    configId = ModelConfigId,
                    value = modelSelectionValue
                },
                cancellationToken).ConfigureAwait(false);
        }

        return new AcpSessionHandle(resolvedSessionId, requestedSessionId is not null, sessionResult.Clone());
    }

    public Task<JsonElement> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
    {
        return _inner.SetModeAsync(sessionId, modeId, cancellationToken);
    }

    public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
    {
        return _inner.SendPromptAsync(sessionId, prompt, cancellationToken);
    }

    public Task<JsonElement> SendPromptAsync(
        string sessionId,
        string prompt,
        IReadOnlyList<AcpImageBlock> images,
        CancellationToken cancellationToken = default)
    {
        return _inner.SendPromptAsync(sessionId, prompt, images, cancellationToken);
    }

    public IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync(CancellationToken cancellationToken = default)
    {
        return _inner.ReceiveNotificationsAsync(cancellationToken);
    }

    public string? GetDiagnosticSummary()
    {
        return _inner.GetDiagnosticSummary();
    }

    public ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }

    private string ToModelSelectionValue(string model)
    {
        if (_provider is null)
        {
            throw new ArgumentException(
                $"DshOptions.Provider is required when DshOptions.Model '{model}' is set.",
                nameof(model));
        }

        return JsonSerializer.Serialize(new[] { _provider, model });
    }

    private static bool ParsePromptImageCapability(JsonElement initializeResult)
    {
        if (initializeResult.ValueKind == JsonValueKind.Object &&
            initializeResult.TryGetProperty("agentCapabilities", out var agentCapabilities) &&
            agentCapabilities.ValueKind == JsonValueKind.Object &&
            agentCapabilities.TryGetProperty("promptCapabilities", out var promptCapabilities) &&
            promptCapabilities.ValueKind == JsonValueKind.Object &&
            promptCapabilities.TryGetProperty("image", out var image) &&
            image.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return image.ValueKind == JsonValueKind.True;
        }

        return false;
    }

    private static string ParseSessionId(JsonElement sessionResult)
    {
        if (sessionResult.ValueKind == JsonValueKind.Object &&
            sessionResult.TryGetProperty("sessionId", out var sessionIdElement) &&
            sessionIdElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(sessionIdElement.GetString()))
        {
            return sessionIdElement.GetString()!;
        }

        throw new JsonException("session/new result did not include a valid sessionId.");
    }
}
