using HagiCode.Libs.Imaging.Engine;
using HagiCode.Libs.Imaging.Internal;
using HagiCode.Libs.Imaging.Tests.Support;
using Shouldly;

namespace HagiCode.Libs.Imaging.Tests;

public class AtomicFileAndErrorMappingTests
{
    [Test]
    public async Task Failed_conversion_keeps_the_existing_destination_and_leaves_no_temp_file()
    {
        var directory = Fixtures.NewTempDirectory();
        var destination = Path.Combine(directory, "out.avif");
        File.WriteAllText(destination, "previous content");
        var engine = new FakeAvifEngine { ConvertException = new EngineEncodeException("encoder down") };
        using var converter = Converter(engine);

        var error = await Should.ThrowAsync<AvifConversionException>(() =>
            converter.ConvertFileAsync(Fixtures.PathOf("photo.png"), destination, overwrite: true));

        error.ErrorCode.ShouldBe(AvifErrorCode.EncodeFailed);
        File.ReadAllText(destination).ShouldBe("previous content");
        Directory.GetFiles(directory, "*.tmp").ShouldBeEmpty();
        Directory.GetFiles(directory, ".*").ShouldBeEmpty();
    }

    [Test]
    public async Task Existing_destination_is_protected_unless_overwrite_is_requested()
    {
        var directory = Fixtures.NewTempDirectory();
        var destination = Path.Combine(directory, "out.avif");
        File.WriteAllText(destination, "keep me");
        var engine = new FakeAvifEngine();
        using var converter = Converter(engine);

        var error = await Should.ThrowAsync<AvifConversionException>(() =>
            converter.ConvertFileAsync(Fixtures.PathOf("photo.png"), destination, overwrite: false));

        error.ErrorCode.ShouldBe(AvifErrorCode.DestinationExists);
        File.ReadAllText(destination).ShouldBe("keep me");
        engine.HeaderCalls.ShouldBe(0);
    }

    [Test]
    public async Task Destination_that_is_the_source_is_invalid_and_source_is_untouched()
    {
        var directory = Fixtures.NewTempDirectory();
        var source = Path.Combine(directory, "same.png");
        File.Copy(Fixtures.PathOf("photo.png"), source);
        var before = File.ReadAllBytes(source);
        using var converter = Converter(new FakeAvifEngine());

        var error = await Should.ThrowAsync<AvifConversionException>(() =>
            converter.ConvertFileAsync(source, source, overwrite: true));

        error.ErrorCode.ShouldBe(AvifErrorCode.InvalidOptions);
        File.ReadAllBytes(source).ShouldBe(before);
    }

    [Test]
    public async Task Successful_file_conversion_moves_output_into_place()
    {
        var directory = Fixtures.NewTempDirectory();
        var destination = Path.Combine(directory, "out.avif");
        using var converter = Converter(new FakeAvifEngine());

        var result = await converter.ConvertFileAsync(Fixtures.PathOf("photo.png"), destination);

        File.ReadAllBytes(destination).Length.ShouldBe((int)result.OutputByteLength);
        result.Data.IsEmpty.ShouldBeTrue();
        Directory.GetFiles(directory, "*.tmp").ShouldBeEmpty();
    }

    [Test]
    public void Engine_load_failure_maps_to_corrupt_source()
    {
        ErrorMapper.Map(new EngineLoadException("decode failed"), ConversionStage.Header).ErrorCode.ShouldBe(AvifErrorCode.CorruptSource);
    }

    [Test]
    [Category("CriticalRegression")]
    public void Engine_encode_failure_maps_to_encode_failed()
    {
        ErrorMapper.Map(new EngineEncodeException("encode failed"), ConversionStage.Encode).ErrorCode.ShouldBe(AvifErrorCode.EncodeFailed);
    }

    [TestCase(typeof(DllNotFoundException))]
    [TestCase(typeof(BadImageFormatException))]
    [TestCase(typeof(EntryPointNotFoundException))]
    public void Native_loader_failures_map_to_runtime_unavailable_with_rid_and_supported_list(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "libvips-42.dll not found")!;

        var error = ErrorMapper.Map(exception, ConversionStage.Header);

        error.ErrorCode.ShouldBe(AvifErrorCode.NativeRuntimeUnavailable);
        error.Message.ShouldContain(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        error.Message.ShouldContain("win-x64");
        error.Message.ShouldContain("osx-arm64");
        error.Message.ShouldNotContain("PNG");
    }

    [Test]
    public async Task Engine_load_failure_reaches_the_caller_as_corrupt_source_without_source_bytes()
    {
        var engine = new FakeAvifEngine { HeaderException = new EngineLoadException("libvips could not load the source.") };
        using var converter = Converter(engine);

        var error = await Should.ThrowAsync<AvifConversionException>(() =>
            converter.ConvertAsync(Fixtures.Read("photo.png")));

        error.ErrorCode.ShouldBe(AvifErrorCode.CorruptSource);
        error.Message.ShouldNotContain("\u0089PNG");
    }

    internal static AvifConverter Converter(FakeAvifEngine engine) => new(
        new AvifConverterOptions { ConfigureProcessWideEngineSettings = false },
        engine,
        () => new AvifRuntimeStatus("test-rid", true, [AvifSourceFormat.Png], null));
}
