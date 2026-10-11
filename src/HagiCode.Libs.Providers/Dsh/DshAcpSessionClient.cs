using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Providers.Dsh;

internal sealed class DshAcpSessionClient : IAcpSessionClient
{
    private const string ModelConfigId = "model";
    private readonly IAcpSessionClient _inner;
    private readonly string? _provider;

    public DshAcpSessionClient(IAcpSessionClient inner, string? provider)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _provider = ArgumentValueNormalizer.NormalizeOptionalValue(provider);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        return _inner.ConnectAsync(cancellationToken);
    }

    public Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
    {
        return _inner.InitializeAsync(cancellationToken);
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
