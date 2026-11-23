namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Represents a process that the caller started and registered with <see cref="CliProcessManager.RegisterExternalProcessAsync" />.
/// Disposing the registration removes it from the ledger and the persisted state; it never stops the process.
/// </summary>
public sealed class CliExternalProcessRegistration : IAsyncDisposable
{
    private readonly Func<ValueTask> _release;
    private int _released;

    internal CliExternalProcessRegistration(ProcessIdentity identity, bool isContained, Func<ValueTask> release)
    {
        Identity = identity;
        IsContained = isContained;
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    /// <summary>
    /// Gets the process id and start time that identify the registered process.
    /// </summary>
    public ProcessIdentity Identity { get; }

    /// <summary>
    /// Gets a value indicating whether the process was assigned to a Windows Job Object that ends its members on stop.
    /// </summary>
    public bool IsContained { get; }

    /// <summary>
    /// Removes the registration. The registered process keeps running.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
        {
            return;
        }

        await _release().ConfigureAwait(false);
    }
}
