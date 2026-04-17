using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Pooling;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class CliRuntimePoolTests
{
    [Fact]
    public async Task AcquireAsync_marks_warm_reuse_when_key_and_fingerprint_match()
    {
        await using var pool = new CliRuntimePool<StubResource>();
        var settings = new CliPoolSettings();

        await using (var firstLease = await pool.AcquireAsync(
                         new CliRuntimePoolRequest("codex", "logical::session-1", "fp-1", settings),
                         _ => Task.FromResult(CreateEntry("codex", "fp-1", settings))))
        {
            firstLease.Kind.ShouldBe(CliRuntimePoolLeaseKind.ColdStart);
        }

        await using var secondLease = await pool.AcquireAsync(
            new CliRuntimePoolRequest("codex", "logical::session-1", "fp-1", settings),
            _ => Task.FromResult(CreateEntry("codex", "fp-1", settings)));

        secondLease.Kind.ShouldBe(CliRuntimePoolLeaseKind.WarmReuse);
        secondLease.IsWarmLease.ShouldBeTrue();
    }

    [Fact]
    public async Task AcquireAsync_marks_compatibility_replacement_when_same_key_changes_fingerprint()
    {
        await using var pool = new CliRuntimePool<StubResource>();
        var settings = new CliPoolSettings();
        var firstResource = new StubResource();

        await using (var firstLease = await pool.AcquireAsync(
                         new CliRuntimePoolRequest("codex", "logical::session-1", "fp-1", settings),
                         _ => Task.FromResult(new CliRuntimePoolEntry<StubResource>("codex", firstResource, "fp-1", settings))))
        {
        }

        await using var secondLease = await pool.AcquireAsync(
            new CliRuntimePoolRequest("codex", "logical::session-1", "fp-2", settings),
            _ => Task.FromResult(CreateEntry("codex", "fp-2", settings)));

        secondLease.Kind.ShouldBe(CliRuntimePoolLeaseKind.CompatibilityReplacement);
        secondLease.IsWarmLease.ShouldBeFalse();
        firstResource.DisposeCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task AcquireAsync_reclaims_expired_idle_entry_before_capacity_failure()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = new CliRuntimePool<StubResource>(timeProvider);
        var settings = new CliPoolSettings { IdleTimeout = TimeSpan.FromSeconds(5), MaxActiveSessions = 1 };
        var staleResource = new StubResource();

        await using (var lease = await pool.AcquireAsync(
                         new CliRuntimePoolRequest("claude-code", "logical::stale", "fp-1", settings),
                         _ => Task.FromResult(new CliRuntimePoolEntry<StubResource>("claude-code", staleResource, "fp-1", settings, timeProvider))))
        {
        }

        timeProvider.Advance(TimeSpan.FromSeconds(6));

        await using var freshLease = await pool.AcquireAsync(
            new CliRuntimePoolRequest("claude-code", "logical::fresh", "fp-2", settings),
            _ => Task.FromResult(CreateEntry("claude-code", "fp-2", settings, timeProvider)));

        staleResource.DisposeCallCount.ShouldBe(1);
        freshLease.Kind.ShouldBe(CliRuntimePoolLeaseKind.ColdStart);
    }

    [Fact]
    public async Task AcquireAsync_replaces_oldest_idle_entry_when_provider_is_still_full()
    {
        var timeProvider = new ManualTimeProvider();
        await using var pool = new CliRuntimePool<StubResource>(timeProvider);
        var settings = new CliPoolSettings { IdleTimeout = TimeSpan.FromMinutes(5), MaxActiveSessions = 2 };
        var oldestResource = new StubResource();
        var newerResource = new StubResource();

        await using (var lease = await pool.AcquireAsync(
                         new CliRuntimePoolRequest("claude-code", "logical::first", "fp-1", settings),
                         _ => Task.FromResult(new CliRuntimePoolEntry<StubResource>("claude-code", oldestResource, "fp-1", settings, timeProvider))))
        {
        }

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        await using (var lease = await pool.AcquireAsync(
                         new CliRuntimePoolRequest("claude-code", "logical::second", "fp-2", settings),
                         _ => Task.FromResult(new CliRuntimePoolEntry<StubResource>("claude-code", newerResource, "fp-2", settings, timeProvider))))
        {
        }

        await using var replacementLease = await pool.AcquireAsync(
            new CliRuntimePoolRequest("claude-code", "logical::third", "fp-3", settings),
            _ => Task.FromResult(CreateEntry("claude-code", "fp-3", settings, timeProvider)));

        oldestResource.DisposeCallCount.ShouldBe(1);
        newerResource.DisposeCallCount.ShouldBe(0);
        replacementLease.Kind.ShouldBe(CliRuntimePoolLeaseKind.ColdStart);
    }

    [Fact]
    public async Task AcquireAsync_throws_when_provider_capacity_is_fully_leased()
    {
        await using var pool = new CliRuntimePool<StubResource>();
        var settings = new CliPoolSettings { IdleTimeout = TimeSpan.FromMinutes(5), MaxActiveSessions = 1 };

        await using var lease = await pool.AcquireAsync(
            new CliRuntimePoolRequest("claude-code", "logical::leased", "fp-1", settings),
            _ => Task.FromResult(CreateEntry("claude-code", "fp-1", settings)));

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await pool.AcquireAsync(
                new CliRuntimePoolRequest("claude-code", "logical::other", "fp-2", settings),
                _ => Task.FromResult(CreateEntry("claude-code", "fp-2", settings))));

        exception.Message.ShouldContain("maximum active session limit");
    }

    private static CliRuntimePoolEntry<StubResource> CreateEntry(
        string providerName,
        string compatibilityFingerprint,
        CliPoolSettings settings,
        TimeProvider? timeProvider = null)
    {
        return new CliRuntimePoolEntry<StubResource>(
            providerName,
            new StubResource(),
            compatibilityFingerprint,
            settings,
            timeProvider);
    }

    private sealed class StubResource : IAsyncDisposable
    {
        public int DisposeCallCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
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
