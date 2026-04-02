using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using Microsoft.Extensions.Logging;
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
        var diagnostics = pool.GetDiagnosticsSnapshot();
        diagnostics.HitCount.ShouldBe(1);
        diagnostics.MissCount.ShouldBe(1);
        diagnostics.EvictionCount.ShouldBe(0);
        diagnostics.FaultCount.ShouldBe(0);
        diagnostics.ProviderDiagnostics.Count.ShouldBe(1);
        diagnostics.ProviderDiagnostics[0].ProviderName.ShouldBe("codebuddy");
        diagnostics.ProviderDiagnostics[0].HitCount.ShouldBe(1);
        diagnostics.ProviderDiagnostics[0].MissCount.ShouldBe(1);
    }

    [Fact]
    public async Task AcquireAsync_replaces_named_entry_when_compatibility_fingerprint_changes()
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
        var diagnostics = pool.GetDiagnosticsSnapshot();
        diagnostics.HitCount.ShouldBe(0);
        diagnostics.MissCount.ShouldBe(2);
        diagnostics.EvictionCount.ShouldBe(1);
        diagnostics.FaultCount.ShouldBe(0);
        diagnostics.LastEviction.ShouldNotBeNull();
        diagnostics.LastEviction.Reason.ShouldBe(CliAcpSessionPoolEventReason.CompatibilityMismatch);
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
                         CreateRequest("kimi", "idle-key", "fp-1", new CliPoolSettings { IdleTimeout = TimeSpan.FromSeconds(5) }),
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
        var diagnostics = pool.GetDiagnosticsSnapshot();
        diagnostics.EvictionCount.ShouldBe(1);
        diagnostics.FaultCount.ShouldBe(0);
        diagnostics.ActiveEntryCount.ShouldBe(0);
        diagnostics.LastEviction.ShouldNotBeNull();
        diagnostics.LastEviction.ProviderName.ShouldBe("kimi");
        diagnostics.LastEviction.Reason.ShouldBe(CliAcpSessionPoolEventReason.Idle);
    }

    [Fact]
    public async Task ReturnAsync_faulted_lease_disposes_entry_immediately()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = new CliAcpSessionPool(timeProvider);
        var client = new StubAcpSessionClient();

        var lease = await pool.AcquireAsync(
            CreateRequest("qodercli", "fault-key", "fp-1"),
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
        var diagnostics = pool.GetDiagnosticsSnapshot();
        diagnostics.EvictionCount.ShouldBe(1);
        diagnostics.FaultCount.ShouldBe(1);
        diagnostics.ActiveEntryCount.ShouldBe(0);
        diagnostics.LastEviction.ShouldNotBeNull();
        diagnostics.LastFault.ShouldNotBeNull();
        diagnostics.LastEviction.Reason.ShouldBe(CliAcpSessionPoolEventReason.Fault);
        diagnostics.LastFault.Reason.ShouldBe(CliAcpSessionPoolEventReason.Fault);
        diagnostics.ProviderDiagnostics.Single().FaultCount.ShouldBe(1);
    }

    [Fact]
    public async Task AcquireAsync_creates_fresh_entry_after_fault_for_same_logical_key()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = new CliAcpSessionPool(timeProvider);
        var firstClient = new StubAcpSessionClient();

        await using (var firstLease = await pool.AcquireAsync(
                         CreateRequest("deepagents", "deepagents-key", "fp-1"),
                         _ => Task.FromResult(new PooledAcpSessionEntry(
                             "deepagents",
                             "session-1",
                             firstClient,
                             "fp-1",
                             new AcpSessionHandle("session-1", false, default),
                             new CliPoolSettings(),
                             timeProvider))))
        {
            firstLease.IsFaulted = true;
        }

        await using var secondLease = await pool.AcquireAsync(
            CreateRequest("deepagents", "deepagents-key", "fp-1"),
            _ => Task.FromResult(CreateEntry("deepagents", "session-2", "fp-1", timeProvider)));

        secondLease.IsWarmLease.ShouldBeFalse();
        secondLease.Entry.SessionId.ShouldBe("session-2");
        firstClient.DisposeCalls.ShouldBe(1);

        var diagnostics = pool.GetDiagnosticsSnapshot();
        diagnostics.MissCount.ShouldBe(2);
        diagnostics.FaultCount.ShouldBe(1);
        diagnostics.ActiveEntryCount.ShouldBe(1);
        diagnostics.IndexedKeyCount.ShouldBe(2);
        diagnostics.ProviderDiagnostics.Single().ProviderName.ShouldBe("deepagents");
        diagnostics.ProviderDiagnostics.Single().MissCount.ShouldBe(2);
        diagnostics.ProviderDiagnostics.Single().FaultCount.ShouldBe(1);
        diagnostics.ProviderDiagnostics.Single().ActiveEntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetDiagnosticsSnapshot_reports_live_counts_for_active_and_leased_entries()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = new CliAcpSessionPool(timeProvider);

        var lease = await pool.AcquireAsync(
            CreateRequest("live-key", "fp-1"),
            _ => Task.FromResult(CreateEntry("session-1", "fp-1", timeProvider)));

        var leasedSnapshot = pool.GetDiagnosticsSnapshot();
        leasedSnapshot.ActiveEntryCount.ShouldBe(1);
        leasedSnapshot.LeasedEntryCount.ShouldBe(1);
        leasedSnapshot.IndexedKeyCount.ShouldBe(2);
        leasedSnapshot.ProviderDiagnostics.Single().ActiveEntryCount.ShouldBe(1);
        leasedSnapshot.ProviderDiagnostics.Single().LeasedEntryCount.ShouldBe(1);
        leasedSnapshot.ProviderDiagnostics.Single().IndexedKeyCount.ShouldBe(2);

        await lease.DisposeAsync();

        var idleSnapshot = pool.GetDiagnosticsSnapshot();
        idleSnapshot.ActiveEntryCount.ShouldBe(1);
        idleSnapshot.LeasedEntryCount.ShouldBe(0);
        idleSnapshot.IndexedKeyCount.ShouldBe(2);
        idleSnapshot.ProviderDiagnostics.Single().ActiveEntryCount.ShouldBe(1);
        idleSnapshot.ProviderDiagnostics.Single().LeasedEntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetDiagnosticsSnapshot_reports_provider_scoped_counts_and_recent_events()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = new CliAcpSessionPool(timeProvider);

        await using (var codebuddyLease = await pool.AcquireAsync(
                         CreateRequest("codebuddy-key", "fp-1"),
                         _ => Task.FromResult(CreateEntry("session-1", "fp-1", timeProvider))))
        {
        }

        await using (var warmCodebuddyLease = await pool.AcquireAsync(
                         CreateRequest("codebuddy-key", "fp-1"),
                         _ => Task.FromResult(CreateEntry("session-2", "fp-1", timeProvider))))
        {
            warmCodebuddyLease.IsWarmLease.ShouldBeTrue();
        }

        await using (var kimiLease = await pool.AcquireAsync(
                         CreateRequest("kimi", "kimi-key", "fp-kimi"),
                         _ => Task.FromResult(CreateEntry("kimi", "kimi-session", "fp-kimi", timeProvider))))
        {
            kimiLease.IsFaulted = true;
        }

        var diagnostics = pool.GetDiagnosticsSnapshot();
        diagnostics.HitCount.ShouldBe(1);
        diagnostics.MissCount.ShouldBe(2);
        diagnostics.EvictionCount.ShouldBe(1);
        diagnostics.FaultCount.ShouldBe(1);
        diagnostics.LastEviction.ShouldNotBeNull();
        diagnostics.LastEviction.ProviderName.ShouldBe("kimi");
        diagnostics.LastFault.ShouldNotBeNull();
        diagnostics.LastFault.ProviderName.ShouldBe("kimi");

        var codebuddyDiagnostics = diagnostics.ProviderDiagnostics.Single(static item => item.ProviderName == "codebuddy");
        codebuddyDiagnostics.HitCount.ShouldBe(1);
        codebuddyDiagnostics.MissCount.ShouldBe(1);
        codebuddyDiagnostics.EvictionCount.ShouldBe(0);
        codebuddyDiagnostics.FaultCount.ShouldBe(0);
        codebuddyDiagnostics.ActiveEntryCount.ShouldBe(1);
        codebuddyDiagnostics.LeasedEntryCount.ShouldBe(0);
        codebuddyDiagnostics.IndexedKeyCount.ShouldBe(2);

        var kimiDiagnostics = diagnostics.ProviderDiagnostics.Single(static item => item.ProviderName == "kimi");
        kimiDiagnostics.HitCount.ShouldBe(0);
        kimiDiagnostics.MissCount.ShouldBe(1);
        kimiDiagnostics.EvictionCount.ShouldBe(1);
        kimiDiagnostics.FaultCount.ShouldBe(1);
        kimiDiagnostics.ActiveEntryCount.ShouldBe(0);
        kimiDiagnostics.LeasedEntryCount.ShouldBe(0);
        kimiDiagnostics.IndexedKeyCount.ShouldBe(0);
        kimiDiagnostics.LastEviction.ShouldNotBeNull();
        kimiDiagnostics.LastFault.ShouldNotBeNull();
        kimiDiagnostics.LastEviction.Reason.ShouldBe(CliAcpSessionPoolEventReason.Fault);
        kimiDiagnostics.LastFault.Reason.ShouldBe(CliAcpSessionPoolEventReason.Fault);
    }

    [Fact]
    public async Task Pool_emits_structured_logs_and_metrics_for_pool_events()
    {
        var timeProvider = new ManualTimeProvider();
        var logger = new TestLogger<CliAcpSessionPool>();
        using var listener = new MeterListener();
        var measurements = new ConcurrentBag<MeasurementSnapshot>();

        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "HagiCode.Libs.Core.Acp.CliAcpSessionPool")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            measurements.Add(new MeasurementSnapshot(
                instrument.Name,
                value,
                GetTagValue(tags, "provider"),
                GetTagValue(tags, "reason")));
        });
        listener.Start();

        await using var pool = new CliAcpSessionPool(timeProvider, logger);
        await using (var missLease = await pool.AcquireAsync(
                         CreateRequest("codebuddy-key", "fp-1"),
                         _ => Task.FromResult(CreateEntry("session-1", "fp-1", timeProvider))))
        {
        }

        await using (var hitLease = await pool.AcquireAsync(
                         CreateRequest("codebuddy-key", "fp-1"),
                         _ => Task.FromResult(CreateEntry("session-2", "fp-1", timeProvider))))
        {
        }

        await using (var faultLease = await pool.AcquireAsync(
                         CreateRequest("qodercli", "fault-key", "fp-fault"),
                         _ => Task.FromResult(CreateEntry("qodercli", "fault-session", "fp-fault", timeProvider))))
        {
            faultLease.IsFaulted = true;
        }

        measurements.ShouldContain(static item => item.Name == "hagicode.cli_acp_session_pool.miss" && item.Provider == "codebuddy" && item.Value == 1);
        measurements.ShouldContain(static item => item.Name == "hagicode.cli_acp_session_pool.hit" && item.Provider == "codebuddy" && item.Value == 1);
        measurements.ShouldContain(static item => item.Name == "hagicode.cli_acp_session_pool.evict" && item.Provider == "qodercli" && item.Reason == CliAcpSessionPoolEventReason.Fault.ToString() && item.Value == 1);
        measurements.ShouldContain(static item => item.Name == "hagicode.cli_acp_session_pool.fault" && item.Provider == "qodercli" && item.Reason == CliAcpSessionPoolEventReason.Fault.ToString() && item.Value == 1);

        logger.Entries.ShouldContain(entry => entry.LogLevel == LogLevel.Debug && entry.Message.Contains("reused warm entry", StringComparison.Ordinal));
        logger.Entries.ShouldContain(entry => entry.LogLevel == LogLevel.Warning && entry.Message.Contains("faulted entry", StringComparison.Ordinal));
    }

    private static CliAcpPoolRequest CreateRequest(
        string providerName,
        string logicalKey,
        string fingerprint,
        CliPoolSettings? settings = null)
    {
        return new CliAcpPoolRequest(providerName, logicalKey, fingerprint, settings ?? new CliPoolSettings { IdleTimeout = TimeSpan.FromMinutes(5) });
    }

    private static CliAcpPoolRequest CreateRequest(
        string logicalKey,
        string fingerprint,
        CliPoolSettings? settings = null)
    {
        return CreateRequest("codebuddy", logicalKey, fingerprint, settings);
    }

    private static PooledAcpSessionEntry CreateEntry(
        string sessionId,
        string fingerprint,
        TimeProvider timeProvider,
        ref int createdEntries)
    {
        createdEntries++;
        return CreateEntry("codebuddy", sessionId, fingerprint, timeProvider);
    }

    private static PooledAcpSessionEntry CreateEntry(
        string providerName,
        string sessionId,
        string fingerprint,
        TimeProvider timeProvider)
    {
        return new PooledAcpSessionEntry(
            providerName,
            sessionId,
            new StubAcpSessionClient(),
            fingerprint,
            new AcpSessionHandle(sessionId, false, default),
            new CliPoolSettings { IdleTimeout = TimeSpan.FromMinutes(5) },
            timeProvider);
    }

    private static PooledAcpSessionEntry CreateEntry(string sessionId, string fingerprint, TimeProvider timeProvider)
    {
        return CreateEntry("codebuddy", sessionId, fingerprint, timeProvider);
    }

    private static string? GetTagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, name, StringComparison.Ordinal))
            {
                return tag.Value?.ToString();
            }
        }

        return null;
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

    private sealed record MeasurementSnapshot(string Name, long Value, string? Provider, string? Reason);

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        public sealed record LogEntry(LogLevel LogLevel, string Message);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
