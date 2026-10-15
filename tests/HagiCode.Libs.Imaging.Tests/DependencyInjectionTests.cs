using HagiCode.Libs.Imaging.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace HagiCode.Libs.Imaging.Tests;

[NonParallelizable]
public class DependencyInjectionTests
{
    [Test]
    public void Converter_resolves_as_a_singleton_with_the_configured_limits()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeImaging(options =>
        {
            options.MaxInputBytes = 1234;
            options.MaxPixels = 5678;
            options.MaxConcurrentConversions = 3;
        });

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<IAvifConverter>();
        var second = provider.GetRequiredService<IAvifConverter>();
        var limits = provider.GetRequiredService<AvifConverterOptions>();

        first.ShouldBeSameAs(second);
        first.ShouldBeOfType<AvifConverter>();
        limits.MaxInputBytes.ShouldBe(1234);
        limits.MaxPixels.ShouldBe(5678);
        limits.MaxConcurrentConversions.ShouldBe(3);
    }

    [Test, NativeAvifGate]
    public async Task Configured_input_limit_reaches_the_resolved_converter()
    {
        NativeRuntime.Require();
        var services = new ServiceCollection();
        services.AddHagiCodeImaging(options => options.MaxInputBytes = 1_000);
        using var provider = services.BuildServiceProvider();
        var converter = provider.GetRequiredService<IAvifConverter>();

        var error = await Should.ThrowAsync<HagiCode.Libs.Imaging.AvifConversionException>(() =>
            converter.ConvertAsync(Fixtures.Read("photo.png")));

        error.ErrorCode.ShouldBe(AvifErrorCode.SourceTooLarge);
    }
}
