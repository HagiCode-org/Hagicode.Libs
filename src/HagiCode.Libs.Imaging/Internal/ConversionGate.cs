namespace HagiCode.Libs.Imaging.Internal;

/// <summary>
/// Cancellation-aware semaphore that bounds how many conversions encode at the same time. A waiter that is canceled
/// never occupies a slot.
/// </summary>
internal sealed class ConversionGate
{
    private readonly SemaphoreSlim _slots;

    internal ConversionGate(int limit)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The gate limit must be at least 1.");
        }

        Limit = limit;
        _slots = new SemaphoreSlim(limit, limit);
    }

    /// <summary>The maximum number of concurrent holders.</summary>
    internal int Limit { get; }

    /// <summary>Waits for a free slot. Throws <see cref="OperationCanceledException"/> if the token is canceled first.</summary>
    internal async Task<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_slots);
    }

    /// <summary>A held slot. Disposing it releases the slot exactly once.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _slots;
        private int _released;

        internal Lease(SemaphoreSlim slots)
        {
            _slots = slots;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _slots.Release();
            }
        }
    }
}
