using System.Runtime.ExceptionServices;
using System.Text;

namespace HagiCode.Libs.Providers.Copilot;

internal sealed class CopilotSdkEnvironmentLeaseCoordinator
{
    // GitHub Copilot SDK reads process-wide environment state, so only requests
    // with the same effective environment fingerprint may overlap safely.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<bool> _stateChanged = CreateStateChangedSignal();
    private ActiveLeaseState? _activeLease;

    public async Task<IAsyncDisposable> AcquireAsync(
        CopilotSdkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await AcquireAsync(request.EnvironmentVariables, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IAsyncDisposable> AcquireAsync(
        IReadOnlyDictionary<string, string?> environmentVariables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);
        var fingerprint = BuildFingerprint(environmentVariables);

        while (true)
        {
            Task waitTask;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_activeLease is null)
                {
                    var baselineEnvironment = CaptureBaseline(environmentVariables);
                    try
                    {
                        ApplyEnvironment(environmentVariables);
                        _activeLease = new ActiveLeaseState(fingerprint, baselineEnvironment);
                        return new EnvironmentLease(this, fingerprint);
                    }
                    catch
                    {
                        RestoreBaseline(baselineEnvironment);
                        throw;
                    }
                }

                if (string.Equals(_activeLease.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    _activeLease.ReferenceCount++;
                    return new EnvironmentLease(this, fingerprint);
                }

                waitTask = _stateChanged.Task;
            }
            finally
            {
                _gate.Release();
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string BuildFingerprint(IReadOnlyDictionary<string, string?> environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);

        var builder = new StringBuilder();
        foreach (var entry in environmentVariables.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            builder.Append(entry.Key.Length)
                   .Append(':')
                   .Append(entry.Key)
                   .Append('=')
                   .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Value ?? "\0")))
                   .Append(';');
        }

        return builder.ToString();
    }

    private async ValueTask ReleaseAsync(string fingerprint)
    {
        TaskCompletionSource<bool>? stateChanged = null;
        Exception? restoreException = null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeLease is null ||
                !string.Equals(_activeLease.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            _activeLease.ReferenceCount--;
            if (_activeLease.ReferenceCount > 0)
            {
                return;
            }

            try
            {
                RestoreBaseline(_activeLease.BaselineEnvironment);
            }
            catch (Exception ex)
            {
                restoreException = ex;
            }
            finally
            {
                _activeLease = null;
                stateChanged = _stateChanged;
                _stateChanged = CreateStateChangedSignal();
            }
        }
        finally
        {
            _gate.Release();
        }

        stateChanged?.TrySetResult(true);

        if (restoreException is not null)
        {
            ExceptionDispatchInfo.Capture(restoreException).Throw();
        }
    }

    private static Dictionary<string, string?> CaptureBaseline(IReadOnlyDictionary<string, string?> environmentVariables)
    {
        var baseline = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in environmentVariables)
        {
            baseline[entry.Key] = Environment.GetEnvironmentVariable(entry.Key);
        }

        return baseline;
    }

    private static void ApplyEnvironment(IReadOnlyDictionary<string, string?> environmentVariables)
    {
        foreach (var entry in environmentVariables)
        {
            Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }

    private static void RestoreBaseline(IReadOnlyDictionary<string, string?> baselineEnvironment)
    {
        foreach (var entry in baselineEnvironment)
        {
            Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }

    private static TaskCompletionSource<bool> CreateStateChangedSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ActiveLeaseState(string fingerprint, Dictionary<string, string?> baselineEnvironment)
    {
        public string Fingerprint { get; } = fingerprint;

        public Dictionary<string, string?> BaselineEnvironment { get; } = baselineEnvironment;

        public int ReferenceCount { get; set; } = 1;
    }

    private sealed class EnvironmentLease(CopilotSdkEnvironmentLeaseCoordinator owner, string fingerprint) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await owner.ReleaseAsync(fingerprint).ConfigureAwait(false);
        }
    }
}
