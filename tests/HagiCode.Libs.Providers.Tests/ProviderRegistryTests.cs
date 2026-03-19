using FluentAssertions;
using HagiCode.Libs.Providers;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void Register_adds_provider_and_GetProvider_returns_it()
    {
        var registry = new ProviderRegistry();
        var provider = new StubProvider("stub", isAvailable: true);

        registry.Register(provider.Name, provider);

        registry.GetProvider("stub").Should().BeSameAs(provider);
        registry.GetAllProviders().Should().ContainSingle();
    }

    [Fact]
    public void Register_rejects_duplicate_names()
    {
        var registry = new ProviderRegistry();
        registry.Register("stub", new StubProvider("stub", true));

        var action = () => registry.Register("stub", new StubProvider("stub", false));

        action.Should().Throw<InvalidOperationException>();
    }

    private sealed class StubProvider(string name, bool isAvailable) : ICliProvider
    {
        public string Name { get; } = name;
        public bool IsAvailable { get; } = isAvailable;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CliProviderTestResult { ProviderName = Name, Success = IsAvailable });
    }
}
