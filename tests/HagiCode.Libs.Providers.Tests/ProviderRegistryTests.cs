using Shouldly;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Gemini;
using Microsoft.Extensions.DependencyInjection;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void Register_adds_provider_and_GetProvider_returns_it()
    {
        var registry = new ProviderRegistry();
        var provider = new StubProvider("stub", isAvailable: true);

        registry.Register(provider.Name, provider);

        registry.GetProvider("stub").ShouldBeSameAs(provider);
        registry.GetAllProviders().ShouldHaveSingleItem();
    }

    [Fact]
    public void Register_rejects_duplicate_names()
    {
        var registry = new ProviderRegistry();
        registry.Register("stub", new StubProvider("stub", true));

        Should.Throw<InvalidOperationException>(() => registry.Register("stub", new StubProvider("stub", false)));
    }

    [Fact]
    public void Register_maps_aliases_to_the_same_provider()
    {
        var registry = new ProviderRegistry();
        var provider = new StubProvider("stub", isAvailable: true);

        registry.Register("stub", provider, ["stub-cli"]);

        registry.GetProvider("stub-cli").ShouldBeSameAs(provider);
        registry.GetAllProviders().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AddHagiCodeLibs_registers_gemini_aliases_in_provider_registry()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeLibs();

        await using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<ProviderRegistry>();

        registry.GetProvider("gemini-cli").ShouldNotBeNull();
        registry.GetProvider<GeminiOptions>("gemini").ShouldBeOfType<GeminiProvider>();
        registry.GetProvider<GeminiOptions>("gemini-cli").ShouldBeOfType<GeminiProvider>();
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
