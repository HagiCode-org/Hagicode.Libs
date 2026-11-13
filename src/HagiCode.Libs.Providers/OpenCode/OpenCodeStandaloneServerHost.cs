using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Providers.OpenCode;

public sealed class OpenCodeStandaloneServerHost : IOpenCodeStandaloneServerClient
{
    private static readonly IReadOnlyDictionary<string, string?> EmptyRuntimeEnvironment =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<Task<OpenCodeStandaloneServerConnection>>> _runtimeCache =
        new(StringComparer.Ordinal);
    private readonly CliExecutableResolver _executableResolver;
    private readonly IRuntimeEnvironmentResolver? _runtimeEnvironmentResolver;
    private readonly OpenCodeProcessLauncher _processLauncher;

    /// <summary>
    /// The default idle time after which a server dedicated to one session is stopped.
    /// Measured on this machine: one idle <c>opencode serve</c> starts in about 0.3 s and holds about 200 MB resident.
    /// </summary>
    public static readonly TimeSpan DefaultSessionIdleTimeout = TimeSpan.FromMinutes(10);

    private const string SessionIdVariableName = "HAGI_SESSION_ID";

    // Guards the cache entries of per-session servers against the idle sweep: an acquire touches its server under this lock,
    // and the sweep removes a server only under it, so a server that has just been handed out is never removed.
    private readonly object _idleGate = new();
    private readonly ConcurrentDictionary<string, byte> _sessionRuntimeKeys = new(StringComparer.Ordinal);
    private TimeSpan _sessionIdleTimeout = DefaultSessionIdleTimeout;
    private Timer? _idleTimer;
    private int _disposed;

    public OpenCodeStandaloneServerHost(
        CliExecutableResolver executableResolver,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null,
        ICliResourceLimitProvider? resourceLimitProvider = null)
        : this(executableResolver, runtimeEnvironmentResolver, new OpenCodeProcessLauncher(executableResolver, resourceLimitProvider))
    {
    }

    internal OpenCodeStandaloneServerHost(
        CliExecutableResolver executableResolver,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver,
        OpenCodeProcessLauncher? processLauncher)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _runtimeEnvironmentResolver = runtimeEnvironmentResolver;
        _processLauncher = processLauncher ?? new OpenCodeProcessLauncher(_executableResolver);
    }

    internal OpenCodeProcessLauncher ProcessLauncher => _processLauncher;

    public async Task<OpenCodeStandaloneServerConnection> AcquireAsync(
        OpenCodeStandaloneServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var runtimeKey = BuildRuntimeKey(options, runtimeEnvironment);
        while (true)
        {
            var lazyRuntime = _runtimeCache.GetOrAdd(
                runtimeKey,
                _ => new Lazy<Task<OpenCodeStandaloneServerConnection>>(
                    () => CreateRuntimeAsync(options, runtimeEnvironment, runtimeKey, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                var runtime = await lazyRuntime.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                await ProbeHealthyAsync(runtime, options, runtimeKey, cancellationToken).ConfigureAwait(false);
                lock (_idleGate)
                {
                    // The idle sweep may have removed this entry while the acquire was awaiting; in that case start again.
                    if (!_runtimeCache.TryGetValue(runtimeKey, out var current) || !ReferenceEquals(current, lazyRuntime))
                    {
                        continue;
                    }

                    runtime.Touch();
                    if (SessionIdOf(options.RunEnvironmentVariables) is not null)
                    {
                        _sessionIdleTimeout = options.SessionIdleTimeout ?? DefaultSessionIdleTimeout;
                        _sessionRuntimeKeys.TryAdd(runtimeKey, 0);
                        EnsureIdleTimer();
                    }
                }

                return runtime;
            }
            catch
            {
                if (_runtimeCache.TryRemove(runtimeKey, out var removed) && removed.IsValueCreated)
                {
                    try
                    {
                        var runtime = await removed.Value.ConfigureAwait(false);
                        await runtime.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                throw;
            }
        }
    }

    public async Task<OpenCodeStandaloneServerLifecycleResult> WarmupAsync(
        OpenCodeStandaloneServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var runtime = await AcquireAsync(options, cancellationToken).ConfigureAwait(false);
            return runtime.LifecycleResult;
        }
        catch (OpenCodeStandaloneServerLifecycleException ex)
        {
            return ex.Result;
        }
    }

    public async Task<OpenCodeStandaloneServerLifecycleResult> InvalidateAsync(
        OpenCodeStandaloneServerOptions options,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var runtimeEnvironment = await ResolveRuntimeEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var runtimeKey = BuildRuntimeKey(options, runtimeEnvironment);
        var attemptedAt = DateTimeOffset.UtcNow;
        var workingDirectory = NormalizeWorkingDirectory(options.WorkingDirectory);

        if (!_runtimeCache.TryRemove(runtimeKey, out var lazyRuntime))
        {
            return new OpenCodeStandaloneServerLifecycleResult
            {
                Status = OpenCodeStandaloneServerStatus.Unhealthy,
                Stage = OpenCodeLifecycleStage.Invalidated,
                RuntimeKey = runtimeKey,
                WorkingDirectory = workingDirectory,
                ErrorMessage = NormalizeReason(reason, "OpenCode runtime invalidated before it was initialized."),
                AttemptedAt = attemptedAt,
            };
        }

        if (!lazyRuntime.IsValueCreated)
        {
            return new OpenCodeStandaloneServerLifecycleResult
            {
                Status = OpenCodeStandaloneServerStatus.Unhealthy,
                Stage = OpenCodeLifecycleStage.Invalidated,
                RuntimeKey = runtimeKey,
                WorkingDirectory = workingDirectory,
                ErrorMessage = NormalizeReason(reason, "OpenCode runtime invalidated before it finished initializing."),
                AttemptedAt = attemptedAt,
            };
        }

        OpenCodeStandaloneServerConnection? runtime = null;
        OpenCodeStandaloneServerLifecycleResult previous = new()
        {
            Status = OpenCodeStandaloneServerStatus.Unhealthy,
            Stage = OpenCodeLifecycleStage.Invalidated,
            RuntimeKey = runtimeKey,
            WorkingDirectory = workingDirectory,
            AttemptedAt = attemptedAt,
        };

        try
        {
            runtime = await lazyRuntime.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            previous = runtime.LifecycleResult;
        }
        catch (OpenCodeStandaloneServerLifecycleException ex)
        {
            previous = ex.Result;
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new OpenCodeStandaloneServerLifecycleResult
        {
            Status = OpenCodeStandaloneServerStatus.Unhealthy,
            Stage = OpenCodeLifecycleStage.Invalidated,
            RuntimeKey = runtimeKey,
            BaseUri = previous.BaseUri,
            Version = previous.Version,
            ExecutionProfile = previous.ExecutionProfile,
            OwnsRuntime = previous.OwnsRuntime,
            WorkingDirectory = previous.WorkingDirectory ?? workingDirectory,
            AttemptedAt = attemptedAt,
            LastSucceededAt = previous.LastSucceededAt,
            ErrorMessage = NormalizeReason(reason, "OpenCode runtime invalidated by caller."),
            DiagnosticOutput = previous.DiagnosticOutput,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_idleGate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            _sessionRuntimeKeys.Clear();
        }

        foreach (var entry in _runtimeCache.Values)
        {
            if (!entry.IsValueCreated)
            {
                continue;
            }

            try
            {
                var runtime = await entry.Value.ConfigureAwait(false);
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _runtimeCache.Clear();
    }

    internal static string BuildRuntimeKey(
        OpenCodeStandaloneServerOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtimeEnvironment);

        var builder = new StringBuilder();
        builder.Append("baseUrl=").Append(Normalize(options.BaseUrl)).Append('|');
        builder.Append("executable=").Append(Normalize(options.ExecutablePath)).Append('|');
        builder.Append("workingDirectory=").Append(Normalize(NormalizeWorkingDirectory(options.WorkingDirectory))).Append('|');
        builder.Append("workspace=").Append(Normalize(options.Workspace)).Append('|');
        builder.Append("extraArgs=").Append(string.Join(',', options.ExtraArguments.Select(static value => Normalize(value)))).Append('|');

        // Only an attached runtime uses the configured password; a digest keeps two passwords apart without storing either readably.
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            builder.Append("serverPassword=").Append(DigestSecret(options.ServerPassword)).Append('|');
        }

        foreach (var pair in runtimeEnvironment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("env:").Append(pair.Key).Append('=').Append(Normalize(pair.Value)).Append('|');
        }

        foreach (var pair in options.EnvironmentVariables.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("override:").Append(pair.Key).Append('=').Append(Normalize(pair.Value)).Append('|');
        }

        // A run with a session ID gets a server of its own: the session ID is the only run value in the key, because a session has one project.
        // A sessionless run with run-scoped values keys every such value, since nothing else says which project they belong to.
        var sessionId = SessionIdOf(options.RunEnvironmentVariables);
        if (sessionId is not null)
        {
            builder.Append("session=").Append(Normalize(sessionId)).Append('|');
        }
        else
        {
            foreach (var pair in options.RunEnvironmentVariables
                         .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                         .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                builder.Append("run:").Append(pair.Key).Append('=').Append(Normalize(pair.Value)).Append('|');
            }
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// The HagiCode session ID carried by a run's values, or null when the run has none (or a blank one).
    /// </summary>
    internal static string? SessionIdOf(IReadOnlyDictionary<string, string?> runEnvironment)
    {
        foreach (var (key, value) in runEnvironment)
        {
            if (string.Equals(key, SessionIdVariableName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Stops the servers dedicated to one session that have been idle for at least the idle timeout and have no run in flight.
    /// Returns how many were stopped. Called by the idle timer; tests call it directly with a chosen time.
    /// </summary>
    internal int SweepIdleSessionRuntimes(DateTimeOffset now)
    {
        var stopping = new List<OpenCodeStandaloneServerConnection>();
        lock (_idleGate)
        {
            foreach (var key in _sessionRuntimeKeys.Keys.ToArray())
            {
                if (!_runtimeCache.TryGetValue(key, out var lazyRuntime))
                {
                    _sessionRuntimeKeys.TryRemove(key, out _);
                    continue;
                }

                // A server still starting, or one that failed (its acquire removes it), is not idle.
                if (!lazyRuntime.IsValueCreated || !lazyRuntime.Value.IsCompletedSuccessfully)
                {
                    continue;
                }

                var runtime = lazyRuntime.Value.Result;
                if (runtime.IsBusy || now - runtime.LastUsedAt < _sessionIdleTimeout)
                {
                    continue;
                }

                _runtimeCache.TryRemove(key, out _);
                _sessionRuntimeKeys.TryRemove(key, out _);
                stopping.Add(runtime);
            }
        }

        foreach (var runtime in stopping)
        {
            _ = DisposeRuntimeQuietlyAsync(runtime);
        }

        return stopping.Count;
    }

    private void EnsureIdleTimer()
    {
        if (_idleTimer is not null)
        {
            return;
        }

        // Check a few times per timeout, but at most once a minute and at least every 100 ms.
        var period = TimeSpan.FromTicks(Math.Clamp(_sessionIdleTimeout.Ticks / 4, TimeSpan.FromMilliseconds(100).Ticks, TimeSpan.FromMinutes(1).Ticks));
        _idleTimer = new Timer(static state => SweepFromTimer(state), this, period, period);
    }

    private static void SweepFromTimer(object? state)
    {
        try
        {
            ((OpenCodeStandaloneServerHost)state!).SweepIdleSessionRuntimes(DateTimeOffset.UtcNow);
        }
        catch
        {
            // A failed sweep leaves idle servers running until the next one; it must not crash the process.
        }
    }

    private static async Task DisposeRuntimeQuietlyAsync(OpenCodeStandaloneServerConnection runtime)
    {
        try
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task<OpenCodeStandaloneServerConnection> CreateRuntimeAsync(
        OpenCodeStandaloneServerOptions options,
        IReadOnlyDictionary<string, string?> runtimeEnvironment,
        string runtimeKey,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        var workingDirectory = NormalizeWorkingDirectory(options.WorkingDirectory);
        var requestTimeout = options.RequestTimeout ?? TimeSpan.FromMinutes(3);
        var stage = OpenCodeLifecycleStage.Attach;
        OpenCodeStandaloneServerConnection? runtime = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                stage = OpenCodeLifecycleStage.Attach;
                if (!Uri.TryCreate(options.BaseUrl.Trim(), UriKind.Absolute, out var baseUri))
                {
                    throw CreateLifecycleException(
                        OpenCodeStandaloneServerStatus.Unhealthy,
                        stage,
                        runtimeKey,
                        executionProfile: "attach",
                        ownsRuntime: false,
                        workingDirectory,
                        attemptedAt,
                        errorMessage: $"OpenCode base URL '{options.BaseUrl}' is not a valid absolute URI.");
                }

                runtime = CreateHandle(
                    baseUri,
                    options,
                    runtimeKey,
                    requestTimeout,
                    "attach",
                    ownsRuntime: false,
                    ownedResource: null,
                    attemptedAt,
                    diagnosticOutput: null,
                    serverPassword: options.ServerPassword);
                await ProbeHealthyAsync(runtime, options, runtimeKey, cancellationToken).ConfigureAwait(false);
                return runtime;
            }

            stage = OpenCodeLifecycleStage.StartOwnedRuntime;
            var executablePath = _processLauncher.ResolveExecutablePath(options, runtimeEnvironment);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                throw CreateMissingExecutableLifecycleException(
                    options,
                    runtimeKey,
                    workingDirectory,
                    attemptedAt,
                    executablePath);
            }

            var process = await _processLauncher.StartAsync(executablePath, options, runtimeEnvironment, cancellationToken).ConfigureAwait(false);
            runtime = CreateHandle(
                process.BaseUri,
                options,
                runtimeKey,
                requestTimeout,
                "live",
                ownsRuntime: true,
                process,
                attemptedAt,
                process.CapturedOutput,
                serverPassword: process.ServerPassword);
            await ProbeHealthyAsync(runtime, options, runtimeKey, cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (OpenCodeStandaloneServerLifecycleException)
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            var secret = runtime?.ServerPassword;
            var message = OpenCodeSecretRedactor.Redact(ex.Message, secret);
            throw CreateLifecycleException(
                OpenCodeStandaloneServerStatus.Unhealthy,
                stage,
                runtimeKey,
                executionProfile: stage == OpenCodeLifecycleStage.Attach ? "attach" : "live",
                ownsRuntime: stage != OpenCodeLifecycleStage.Attach,
                workingDirectory,
                attemptedAt,
                errorMessage: $"OpenCode lifecycle stage '{FormatStage(stage)}' failed: {message}",
                diagnosticOutput: ex is TimeoutException or InvalidOperationException ? message : null,
                baseUri: runtime?.BaseUri.ToString());
        }
    }

    private static async Task ProbeHealthyAsync(
        OpenCodeStandaloneServerConnection runtime,
        OpenCodeStandaloneServerOptions options,
        string runtimeKey,
        CancellationToken cancellationToken)
    {
        var attemptedAt = runtime.LifecycleResult.AttemptedAt;
        var workingDirectory = runtime.LifecycleResult.WorkingDirectory ?? NormalizeWorkingDirectory(options.WorkingDirectory);

        try
        {
            var health = await runtime.Client.HealthAsync(cancellationToken).ConfigureAwait(false);
            if (!health.Healthy)
            {
                throw CreateLifecycleException(
                    OpenCodeStandaloneServerStatus.Unhealthy,
                    OpenCodeLifecycleStage.HealthProbe,
                    runtimeKey,
                    runtime.ExecutionProfile,
                    runtime.OwnsRuntime,
                    workingDirectory,
                    attemptedAt,
                    errorMessage: "OpenCode lifecycle stage 'health_probe' failed: OpenCode health endpoint reported an unhealthy runtime.",
                    diagnosticOutput: runtime.LifecycleResult.DiagnosticOutput,
                    baseUri: runtime.BaseUri.ToString());
            }

            runtime.LifecycleResult = new OpenCodeStandaloneServerLifecycleResult
            {
                Status = OpenCodeStandaloneServerStatus.Ready,
                Stage = OpenCodeLifecycleStage.Ready,
                RuntimeKey = runtimeKey,
                BaseUri = runtime.BaseUri.ToString(),
                Version = string.IsNullOrWhiteSpace(health.Version) ? null : health.Version,
                ExecutionProfile = runtime.ExecutionProfile,
                OwnsRuntime = runtime.OwnsRuntime,
                WorkingDirectory = workingDirectory,
                AttemptedAt = attemptedAt,
                LastSucceededAt = runtime.LifecycleResult.LastSucceededAt ?? DateTimeOffset.UtcNow,
                DiagnosticOutput = runtime.LifecycleResult.DiagnosticOutput,
            };
        }
        catch (OpenCodeStandaloneServerLifecycleException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Classify the cause so the diagnostic says whether the runtime was unreachable, rejected the password, or is not OpenCode 2.0.
            var cause = ex switch
            {
                OpenCodeAuthenticationException => "authentication failed: the OpenCode server rejected the credentials (HTTP 401). Provide the OpenCode server password for this runtime.",
                OpenCodeUnsupportedApiException => "unsupported API version: the server does not expose the OpenCode 2.0 API.",
                HttpRequestException or TaskCanceledException => $"connection failed: {ex.Message}",
                _ => ex.Message,
            };
            throw CreateLifecycleException(
                OpenCodeStandaloneServerStatus.Unhealthy,
                OpenCodeLifecycleStage.HealthProbe,
                runtimeKey,
                runtime.ExecutionProfile,
                runtime.OwnsRuntime,
                workingDirectory,
                attemptedAt,
                errorMessage: OpenCodeSecretRedactor.Redact($"OpenCode lifecycle stage 'health_probe' failed: {cause}", runtime.ServerPassword)!,
                diagnosticOutput: runtime.LifecycleResult.DiagnosticOutput,
                baseUri: runtime.BaseUri.ToString(),
                innerException: ex);
        }
    }

    private static string DigestSecret(string? secret)
    {
        return string.IsNullOrEmpty(secret)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResolveRuntimeEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (_runtimeEnvironmentResolver is null)
        {
            return EmptyRuntimeEnvironment;
        }

        return await _runtimeEnvironmentResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
    }

    private static OpenCodeStandaloneServerConnection CreateHandle(
        Uri baseUri,
        OpenCodeStandaloneServerOptions options,
        string runtimeKey,
        TimeSpan requestTimeout,
        string executionProfile,
        bool ownsRuntime,
        IAsyncDisposable? ownedResource,
        DateTimeOffset attemptedAt,
        string? diagnosticOutput,
        string? serverPassword)
    {
        var httpClient = new HttpClient
        {
            BaseAddress = NormalizeBaseUri(baseUri),
            Timeout = requestTimeout,
        };
        var client = new OpenCodeHttpClient(httpClient, options.WorkingDirectory, options.Workspace, serverPassword);
        return new OpenCodeStandaloneServerConnection(
            runtimeKey,
            client,
            executionProfile,
            ownsRuntime,
            ownedResource,
            new OpenCodeStandaloneServerLifecycleResult
            {
                Status = OpenCodeStandaloneServerStatus.Initializing,
                Stage = executionProfile == "attach" ? OpenCodeLifecycleStage.Attach : OpenCodeLifecycleStage.StartOwnedRuntime,
                RuntimeKey = runtimeKey,
                BaseUri = NormalizeBaseUri(baseUri).ToString(),
                ExecutionProfile = executionProfile,
                OwnsRuntime = ownsRuntime,
                WorkingDirectory = NormalizeWorkingDirectory(options.WorkingDirectory),
                AttemptedAt = attemptedAt,
                DiagnosticOutput = string.IsNullOrWhiteSpace(diagnosticOutput) ? null : diagnosticOutput,
            },
            serverPassword);
    }

    private static OpenCodeStandaloneServerLifecycleException CreateLifecycleException(
        OpenCodeStandaloneServerStatus status,
        OpenCodeLifecycleStage stage,
        string runtimeKey,
        string executionProfile,
        bool ownsRuntime,
        string? workingDirectory,
        DateTimeOffset attemptedAt,
        string errorMessage,
        string? diagnosticOutput = null,
        string? baseUri = null,
        Exception? innerException = null)
    {
        return new OpenCodeStandaloneServerLifecycleException(
            new OpenCodeStandaloneServerLifecycleResult
            {
                Status = status,
                Stage = stage,
                RuntimeKey = runtimeKey,
                BaseUri = baseUri,
                ExecutionProfile = executionProfile,
                OwnsRuntime = ownsRuntime,
                WorkingDirectory = workingDirectory,
                AttemptedAt = attemptedAt,
                ErrorMessage = errorMessage,
                DiagnosticOutput = string.IsNullOrWhiteSpace(diagnosticOutput) ? null : diagnosticOutput,
            },
            innerException);
    }

    private static OpenCodeStandaloneServerLifecycleException CreateMissingExecutableLifecycleException(
        OpenCodeStandaloneServerOptions options,
        string runtimeKey,
        string? workingDirectory,
        DateTimeOffset attemptedAt,
        string? resolvedExecutablePath)
    {
        var configuredExecutable = string.IsNullOrWhiteSpace(options.ExecutablePath)
            ? "opencode"
            : options.ExecutablePath.Trim();
        var resolvedDisplay = string.IsNullOrWhiteSpace(resolvedExecutablePath)
            ? "(unresolved)"
            : resolvedExecutablePath.Trim();
        var workingDirectoryDisplay = workingDirectory ?? "(none)";
        var errorMessage =
            $"OpenCode lifecycle stage 'owned_runtime' failed: OpenCode executable was not found. Configured executable='{configuredExecutable}', resolved executable='{resolvedDisplay}', workingDirectory='{workingDirectoryDisplay}'.";

        return CreateLifecycleException(
            OpenCodeStandaloneServerStatus.Unhealthy,
            OpenCodeLifecycleStage.StartOwnedRuntime,
            runtimeKey,
            executionProfile: "live",
            ownsRuntime: true,
            workingDirectory,
            attemptedAt,
            errorMessage,
            diagnosticOutput: errorMessage);
    }

    private static string FormatStage(OpenCodeLifecycleStage stage)
    {
        return stage switch
        {
            OpenCodeLifecycleStage.Attach => "attach",
            OpenCodeLifecycleStage.StartOwnedRuntime => "owned_runtime",
            OpenCodeLifecycleStage.HealthProbe => "health_probe",
            OpenCodeLifecycleStage.Invalidated => "invalidated",
            OpenCodeLifecycleStage.Ready => "ready",
            OpenCodeLifecycleStage.Skipped => "skipped",
            _ => "none",
        };
    }

    private static Uri NormalizeBaseUri(Uri input)
    {
        var text = input.ToString();
        return text.EndsWith("/", StringComparison.Ordinal) ? input : new Uri(text + "/");
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string? NormalizeWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(workingDirectory);
        }
        catch
        {
            return workingDirectory.Trim();
        }
    }

    private static string NormalizeReason(string? reason, string fallback)
    {
        return string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(OpenCodeStandaloneServerHost));
        }
    }
}
