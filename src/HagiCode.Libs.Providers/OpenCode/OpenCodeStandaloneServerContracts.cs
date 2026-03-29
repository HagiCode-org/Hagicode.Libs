namespace HagiCode.Libs.Providers.OpenCode;

public enum OpenCodeStandaloneServerStatus
{
    Skipped = 0,
    Initializing = 1,
    Ready = 2,
    Unhealthy = 3,
}

public enum OpenCodeLifecycleStage
{
    None = 0,
    Skipped = 1,
    Attach = 2,
    StartOwnedRuntime = 3,
    HealthProbe = 4,
    Invalidated = 5,
    Ready = 6,
}

public sealed record OpenCodeStandaloneServerOptions
{
    public string? ExecutablePath { get; init; }

    public string? BaseUrl { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? Workspace { get; init; }

    public TimeSpan? StartupTimeout { get; init; }

    public TimeSpan? RequestTimeout { get; init; }

    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}

public sealed class OpenCodeStandaloneServerLifecycleResult
{
    public OpenCodeStandaloneServerStatus Status { get; init; } = OpenCodeStandaloneServerStatus.Skipped;

    public OpenCodeLifecycleStage Stage { get; init; } = OpenCodeLifecycleStage.None;

    public string? RuntimeKey { get; init; }

    public string? BaseUri { get; init; }

    public string? Version { get; init; }

    public string? ExecutionProfile { get; init; }

    public bool OwnsRuntime { get; init; }

    public string? WorkingDirectory { get; init; }

    public DateTimeOffset AttemptedAt { get; init; }

    public DateTimeOffset? LastSucceededAt { get; init; }

    public string? ErrorMessage { get; init; }

    public string? DiagnosticOutput { get; init; }
}

public interface IOpenCodeStandaloneServerClient : IAsyncDisposable
{
    Task<OpenCodeStandaloneServerConnection> AcquireAsync(
        OpenCodeStandaloneServerOptions options,
        CancellationToken cancellationToken = default);

    Task<OpenCodeStandaloneServerLifecycleResult> WarmupAsync(
        OpenCodeStandaloneServerOptions options,
        CancellationToken cancellationToken = default);

    Task<OpenCodeStandaloneServerLifecycleResult> InvalidateAsync(
        OpenCodeStandaloneServerOptions options,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

public sealed class OpenCodeStandaloneServerLifecycleException : InvalidOperationException
{
    public OpenCodeStandaloneServerLifecycleException(OpenCodeStandaloneServerLifecycleResult result)
        : base(result?.ErrorMessage ?? "OpenCode standalone lifecycle failed.")
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public OpenCodeStandaloneServerLifecycleResult Result { get; }
}

public sealed class OpenCodeStandaloneServerConnection : IAsyncDisposable
{
    private readonly IAsyncDisposable? _ownedResource;
    private int _disposed;

    internal OpenCodeStandaloneServerConnection(
        string runtimeKey,
        OpenCodeHttpClient client,
        string executionProfile,
        bool ownsRuntime,
        IAsyncDisposable? ownedResource,
        OpenCodeStandaloneServerLifecycleResult lifecycleResult)
    {
        RuntimeKey = runtimeKey;
        Client = client ?? throw new ArgumentNullException(nameof(client));
        ExecutionProfile = executionProfile;
        OwnsRuntime = ownsRuntime;
        _ownedResource = ownedResource;
        LifecycleResult = lifecycleResult ?? throw new ArgumentNullException(nameof(lifecycleResult));
    }

    public string RuntimeKey { get; }

    public OpenCodeHttpClient Client { get; }

    public string ExecutionProfile { get; }

    public bool OwnsRuntime { get; }

    public Uri BaseUri => Client.BaseUri;

    public OpenCodeStandaloneServerLifecycleResult LifecycleResult { get; internal set; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Client.Dispose();
        if (_ownedResource is not null)
        {
            await _ownedResource.DisposeAsync().ConfigureAwait(false);
        }
    }
}
