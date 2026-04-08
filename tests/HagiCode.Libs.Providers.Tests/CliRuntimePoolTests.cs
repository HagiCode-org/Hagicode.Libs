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

    private static CliRuntimePoolEntry<StubResource> CreateEntry(
        string providerName,
        string compatibilityFingerprint,
        CliPoolSettings settings)
    {
        return new CliRuntimePoolEntry<StubResource>(
            providerName,
            new StubResource(),
            compatibilityFingerprint,
            settings);
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
}
