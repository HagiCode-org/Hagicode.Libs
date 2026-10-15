using HagiCode.Libs.Imaging.Engine;
using HagiCode.Libs.Imaging.Tests.Support;
using Shouldly;

namespace HagiCode.Libs.Imaging.Tests;

public class AvifConverterUnitTests
{
    [Test]
    public async Task Token_canceled_before_start_ends_as_canceled_without_engine_work()
    {
        var engine = new FakeAvifEngine();
        using var converter = NewConverter(engine);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            converter.ConvertAsync(Fixtures.Read("photo.png"), cancellationToken: cts.Token));

        engine.HeaderCalls.ShouldBe(0);
        engine.ConvertCalls.ShouldBe(0);
    }

    [Test]
    public async Task Token_canceled_while_queued_releases_the_waiter_and_later_work_proceeds()
    {
        var engine = new FakeAvifEngine { Block = new ManualResetEventSlim(false) };
        using var converter = NewConverter(engine, maxConcurrent: 1);
        var source = Fixtures.Read("photo.png");

        var first = converter.ConvertAsync(source);
        await WaitUntil(() => engine.ConvertCalls == 1);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<OperationCanceledException>(() => converter.ConvertAsync(source, cancellationToken: cts.Token));

        engine.Block.Set();
        (await first).OutputByteLength.ShouldBeGreaterThan(0);
        engine.Block.Dispose();
    }

    [Test]
    public async Task Sixteen_parallel_conversions_never_exceed_the_limit_and_all_succeed()
    {
        var engine = new FakeAvifEngine { Delay = TimeSpan.FromMilliseconds(20) };
        using var converter = NewConverter(engine, maxConcurrent: 3);
        var source = Fixtures.Read("photo.png");

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => converter.ConvertAsync(source)));

        results.Length.ShouldBe(16);
        engine.ConvertCalls.ShouldBe(16);
        engine.MaxConcurrentConverts.ShouldBeLessThanOrEqualTo(3);
        engine.MaxConcurrentConverts.ShouldBeGreaterThan(1);
    }

    [Test]
    public async Task Failed_stream_conversion_writes_zero_bytes_to_the_destination()
    {
        var engine = new FakeAvifEngine { ConvertException = new EngineEncodeException("boom") };
        using var converter = NewConverter(engine);
        using var source = new MemoryStream(Fixtures.Read("photo.png"));
        using var destination = new MemoryStream();

        var error = await Should.ThrowAsync<AvifConversionException>(() => converter.ConvertAsync(source, destination));

        error.ErrorCode.ShouldBe(AvifErrorCode.EncodeFailed);
        destination.Length.ShouldBe(0);
    }

    [Test]
    public async Task Caller_streams_stay_open_and_receive_the_output_on_success()
    {
        using var converter = NewConverter(new FakeAvifEngine());
        using var source = new MemoryStream(Fixtures.Read("photo.png"));
        using var destination = new MemoryStream();

        var result = await converter.ConvertAsync(source, destination);

        destination.Length.ShouldBe(result.OutputByteLength);
        source.CanRead.ShouldBeTrue();
        destination.CanWrite.ShouldBeTrue();
        result.Data.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public async Task Caller_input_buffer_is_unchanged_and_buffer_result_carries_the_bytes()
    {
        using var converter = NewConverter(new FakeAvifEngine());
        var source = Fixtures.Read("photo.png");
        var snapshot = (byte[])source.Clone();

        var result = await converter.ConvertAsync(source);

        source.ShouldBe(snapshot);
        result.Data.Length.ShouldBe((int)result.OutputByteLength);
        result.SourceFormat.ShouldBe(AvifSourceFormat.Png);
    }

    [Test]
    public async Task Result_reports_source_output_dimensions_length_and_alpha()
    {
        var engine = new FakeAvifEngine { Header = new SourceHeader(800, 600, HasAlpha: true) };
        using var converter = NewConverter(engine);

        var result = await converter.ConvertAsync(Fixtures.Read("photo.jpg"));

        result.SourceWidth.ShouldBe(800);
        result.SourceHeight.ShouldBe(600);
        result.OutputWidth.ShouldBe(800);
        result.OutputHeight.ShouldBe(600);
        result.OutputByteLength.ShouldBe(result.Data.Length);
        result.HasAlpha.ShouldBeTrue();
    }

    [Test]
    public async Task Unsupported_source_is_rejected_before_the_engine_runs()
    {
        var engine = new FakeAvifEngine();
        using var converter = NewConverter(engine);

        var error = await Should.ThrowAsync<AvifConversionException>(() =>
            converter.ConvertAsync(System.Text.Encoding.UTF8.GetBytes("<svg/>")));

        error.ErrorCode.ShouldBe(AvifErrorCode.UnsupportedSourceFormat);
        engine.HeaderCalls.ShouldBe(0);
    }

    [Test]
    public async Task Source_above_the_input_limit_is_rejected_as_source_too_large()
    {
        var engine = new FakeAvifEngine();
        var converter = new AvifConverter(
            new AvifConverterOptions { MaxInputBytes = 100, ConfigureProcessWideEngineSettings = false },
            engine,
            () => Available());

        var error = await Should.ThrowAsync<AvifConversionException>(() => converter.ConvertAsync(Fixtures.Read("photo.png")));

        error.ErrorCode.ShouldBe(AvifErrorCode.SourceTooLarge);
        engine.HeaderCalls.ShouldBe(0);
    }

    [Test]
    public async Task Declared_pixels_above_the_limit_fail_before_encoding()
    {
        var engine = new FakeAvifEngine { Header = new SourceHeader(100_000, 100_000, HasAlpha: false) };
        using var converter = NewConverter(engine);

        var error = await Should.ThrowAsync<AvifConversionException>(() => converter.ConvertAsync(Fixtures.Read("photo.png")));

        error.ErrorCode.ShouldBe(AvifErrorCode.DimensionsTooLarge);
        engine.ConvertCalls.ShouldBe(0);
    }

    [Test]
    public async Task Unavailable_runtime_reports_native_error_with_the_probe_reason()
    {
        var converter = new AvifConverter(
            new AvifConverterOptions { ConfigureProcessWideEngineSettings = false },
            new FakeAvifEngine(),
            () => new AvifRuntimeStatus("linux-musl-x64", false, [], "not in the supported set"));

        var error = await Should.ThrowAsync<AvifConversionException>(() => converter.ConvertAsync(Fixtures.Read("photo.png")));

        error.ErrorCode.ShouldBe(AvifErrorCode.NativeRuntimeUnavailable);
        error.Message.ShouldContain("linux-musl-x64");
        error.Message.ShouldContain("not in the supported set");
    }

    [Test]
    public void Disposal_is_idempotent_and_blocks_new_work()
    {
        var converter = NewConverter(new FakeAvifEngine());

        converter.Dispose();
        converter.Dispose();

        Should.Throw<ObjectDisposedException>(() => converter.ConvertAsync(Fixtures.Read("photo.png")).GetAwaiter().GetResult());
    }

    [Test]
    public void Invalid_converter_limits_are_rejected_at_construction()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new AvifConverter(new AvifConverterOptions { MaxConcurrentConversions = 0, ConfigureProcessWideEngineSettings = false }));
    }

    internal static AvifConverter NewConverter(FakeAvifEngine engine, int maxConcurrent = 4) => new(
        new AvifConverterOptions { MaxConcurrentConversions = maxConcurrent, ConfigureProcessWideEngineSettings = false },
        engine,
        () => Available());

    private static AvifRuntimeStatus Available() =>
        new("test-rid", true, [AvifSourceFormat.Png, AvifSourceFormat.Jpeg], null);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(5);
        }
    }
}
