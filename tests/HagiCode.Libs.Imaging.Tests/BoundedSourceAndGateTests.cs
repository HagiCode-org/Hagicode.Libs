using HagiCode.Libs.Imaging.Internal;
using HagiCode.Libs.Imaging.Tests.Support;
using Shouldly;
using Xunit;

namespace HagiCode.Libs.Imaging.Tests;

public class BoundedSourceAndGateTests
{
    [Fact]
    public async Task Oversize_forward_only_stream_fails_after_at_most_one_extra_chunk()
    {
        var stream = new CountingForwardStream(new byte[500_000]);

        var error = await Should.ThrowAsync<AvifConversionException>(() =>
            BoundedSourceReader.ReadAsync(stream, maxBytes: 1_000, CancellationToken.None));

        error.ErrorCode.ShouldBe(AvifErrorCode.SourceTooLarge);
        stream.BytesRead.ShouldBeLessThanOrEqualTo(1_000 + BoundedSourceReader.ChunkSize);
    }

    [Fact]
    public async Task Non_seekable_stream_within_the_limit_is_accepted()
    {
        var payload = Enumerable.Range(0, 5_000).Select(i => (byte)i).ToArray();

        var read = await BoundedSourceReader.ReadAsync(new CountingForwardStream(payload), maxBytes: 10_000, CancellationToken.None);

        read.ShouldBe(payload);
    }

    [Fact]
    public async Task Seekable_stream_longer_than_the_limit_is_rejected_without_reading()
    {
        var stream = new MemoryStream(new byte[5_000]);

        var error = await Should.ThrowAsync<AvifConversionException>(() =>
            BoundedSourceReader.ReadAsync(stream, maxBytes: 1_000, CancellationToken.None));

        error.ErrorCode.ShouldBe(AvifErrorCode.SourceTooLarge);
        stream.Position.ShouldBe(0);
    }

    [Fact]
    public async Task Gate_never_runs_more_than_the_limit_under_sixteen_parallel_waiters()
    {
        var gate = new ConversionGate(3);
        var active = 0;
        var maxActive = 0;

        async Task RunOne()
        {
            using var lease = await gate.EnterAsync(CancellationToken.None);
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
        }

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => RunOne()));

        maxActive.ShouldBeLessThanOrEqualTo(3);
        maxActive.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Canceled_waiter_does_not_occupy_a_slot()
    {
        var gate = new ConversionGate(1);
        var holder = await gate.EnterAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiter = gate.EnterAsync(cts.Token);

        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => waiter);
        holder.Dispose();

        using var next = await gate.EnterAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        next.ShouldNotBeNull();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    /// <summary>A non-seekable stream that counts how many bytes were pulled from it.</summary>
    private sealed class CountingForwardStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload);

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
