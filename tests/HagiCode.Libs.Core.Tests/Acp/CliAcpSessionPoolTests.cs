using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using Shouldly;

namespace HagiCode.Libs.Core.Tests.Acp;

public sealed class CliAcpSessionPoolTests
{
    [Fact]
    public async Task AcquireAsync_reuses_warm_compatible_entry_for_same_logical_key()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = new CliAcpSessionPool(timeProvider);
        var createdEntries = 0;

        var request = CreateRequest("hermes-key", "fp-1");
        await using (var firstLease = await pool.AcquireAsync(
                         request,
                         _ => Task.FromResult(CreateEntry("session-1", "fp-1", timeProvider, ref createdEntries))))
        {
            firstLease.IsWarmLease.ShouldBeFalse();
        }

        await using var secondLease = await pool.AcquireAsync(
            request,
            _ => Task.FromResult(CreateEntry("session-2", "fp-1", timeProvider, ref createdEntries)));

        secondLease.IsWarmLease.ShouldBeTrue();
        secondLease.Entry.SessionId.ShouldBe("session-1");
        createdEntries.ShouldBe(1);
    }

    [Fact]
    public async Task AcquireAsync_replaces_incompatible_entry_before_reuse()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = new CliAcpSessionPool(timeProvider);
        var createdEntries = 0;
        var firstClient = new StubAcpSessionClient();

        await using (var firstLease = await pool.AcquireAsync(
                         CreateRequest("session-key", "fp-1"),
                         _ => Task.FromResult(new PooledAcpSessionEntry(
                             "codebuddy",
                             "session-1",
                             firstClient,
                             "fp-1",
                             new AcpSessionHandle("session-1", false, default),
                             new CliPoolSettings { IdleTimeout = TimeSpan.FromMinutes(5) },
                             timeProvider))))
        {
        }

        await using var secondLease = await pool.AcquireAsync(
            CreateRequest("session-key", "fp-2"),
            _ => Task.FromResult(CreateEntry("session-2", "fp-2", timeProvider, ref createdEntries)));

        secondLease.IsWarmLease.ShouldBeFalse();
        secondLease.Entry.SessionId.ShouldBe("session-2");
        firstClient.DisposeCalls.ShouldBe(1);
        createdEntries.ShouldBe(1);
    }

    [Fact]
    public async Task Entry_execution_lock_serializes_concurrent_prompt_access()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = new CliAcpSessionPool(timeProvider);
        var request = CreateRequest("shared-key", "fp-1");
        await using var firstLease = await pool.AcquireAsync(
            request,
            _ => Task.FromResult(CreateEntry("session-1", "fp-1", timeProvider)));
        firstLease.Entry.ExecutionLock.Wait(0).ShouldBeTrue();

        await using var secondLease = await pool.AcquireAsync(request, _ => throw new InvalidOperationException("Should reuse existing entry."));
        secondLease.IsWarmLease.ShouldBeTrue();

        var waitingTask = secondLease.Entry.ExecutionLock.WaitAsync();
        waitingTask.IsCompleted.ShouldBeFalse();

        firstLease.Entry.ExecutionLock.Release();
        (await Task.WhenAny(waitingTask, Task.Delay(TimeSpan.FromSeconds(1)))).ShouldBe(waitingTask);
        secondLease.Entry.ExecutionLock.Release();
    }

    [Fact]
    public async Task ReapIdleEntriesAsync_evicts_entries_past_idle_timeout()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = new CliAcpSessionPool(timeProvider);
        var client = new StubAcpSessionClient();

        await using (var lease = await pool.AcquireAsync(
                         CreateRequest("idle-key", "fp-1", new CliPoolSettings { IdleTimeout = TimeSpan.FromSeconds(5) }),
                         _ => Task.FromResult(new PooledAcpSessionEntry(
                             "kimi",
                             "session-1",
                             client,
                             "fp-1",
                             new AcpSessionHandle("session-1", false, default),
                             new CliPoolSettings { IdleTimeout = TimeSpan.FromSeconds(5) },
                             timeProvider))))
        {
        }

        timeProvider.Advance(TimeSpan.FromSeconds(6));
        var reaped = await pool.ReapIdleEntriesAsync("kimi");

        reaped.ShouldBe(1);
        client.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ReturnAsync_faulted_lease_disposes_entry_immediately()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = new CliAcpSessionPool(timeProvider);
        var client = new StubAcpSessionClient();

        var lease = await pool.AcquireAsync(
            CreateRequest("fault-key", "fp-1"),
            _ => Task.FromResult(new PooledAcpSessionEntry(
                "qodercli",
                "session-1",
                client,
                "fp-1",
                new AcpSessionHandle("session-1", false, default),
                new CliPoolSettings(),
                timeProvider)));
        lease.IsFaulted = true;
        await lease.DisposeAsync();

        client.DisposeCalls.ShouldBe(1);
    }

    private static CliAcpPoolRequest CreateRequest(
        string logicalKey,
        string fingerprint,
        CliPoolSettings? settings = null)
    {
        return new CliAcpPoolRequest("codebuddy", logicalKey, fingerprint, settings ?? new CliPoolSettings { IdleTimeout = TimeSpan.FromMinutes(5) });
    }

    private static PooledAcpSessionEntry CreateEntry(
        string sessionId,
        string fingerprint,
        TimeProvider timeProvider,
        ref int createdEntries)
    {
        createdEntries++;
        return CreateEntry(sessionId, fingerprint, timeProvider);
    }

    private static PooledAcpSessionEntry CreateEntry(string sessionId, string fingerprint, TimeProvider timeProvider)
    {
        return new PooledAcpSessionEntry(
            "codebuddy",
            sessionId,
            new StubAcpSessionClient(),
            fingerprint,
            new AcpSessionHandle(sessionId, false, default),
            new CliPoolSettings { IdleTimeout = TimeSpan.FromMinutes(5) },
            timeProvider);
    }

    private sealed class StubAcpSessionClient : IAcpSessionClient
    {
        public int DisposeCalls { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }

        public Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default) => Task.FromResult(default(JsonElement));

        public Task<JsonElement> InvokeBootstrapMethodAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
            => Task.FromResult(default(JsonElement));

        public IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync(CancellationToken cancellationToken = default)
            => CreateEmptyNotifications();

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(default(JsonElement));

        public Task<JsonElement> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
            => Task.FromResult(default(JsonElement));

        public Task<AcpSessionHandle> StartSessionAsync(string workingDirectory, string? sessionId, string? model, CancellationToken cancellationToken = default)
            => Task.FromResult(new AcpSessionHandle(sessionId ?? "session", sessionId is not null, default));

        private static async IAsyncEnumerable<AcpNotification> CreateEmptyNotifications()
        {
            yield break;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by)
        {
            _utcNow = _utcNow.Add(by);
        }
    }
}
