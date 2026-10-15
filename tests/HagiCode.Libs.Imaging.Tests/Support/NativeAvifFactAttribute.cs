using HagiCode.Libs.Imaging;
using Xunit;

namespace HagiCode.Libs.Imaging.Tests.Support;

/// <summary>
/// Marks a test that needs the native runtime. Such a test fails when the runtime is unavailable (see
/// <see cref="NativeRuntime.Require()"/>). A developer on an unsupported machine can set
/// <c>HAGICODE_IMAGING_SKIP_NATIVE=1</c> to skip these tests locally. CI never sets it.
/// </summary>
public sealed class NativeAvifFactAttribute : FactAttribute
{
    internal const string SkipVariable = "HAGICODE_IMAGING_SKIP_NATIVE";

    public NativeAvifFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(SkipVariable) == "1")
        {
            Skip = $"Skipped locally by {SkipVariable}=1. CI does not set this variable.";
        }
    }
}

/// <summary>Theory form of <see cref="NativeAvifFactAttribute"/>, with the same local-only opt-out.</summary>
public sealed class NativeAvifTheoryAttribute : TheoryAttribute
{
    public NativeAvifTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable(NativeAvifFactAttribute.SkipVariable) == "1")
        {
            Skip = $"Skipped locally by {NativeAvifFactAttribute.SkipVariable}=1. CI does not set this variable.";
        }
    }
}

/// <summary>Turns an unavailable native runtime into a test failure instead of a silent pass.</summary>
internal static class NativeRuntime
{
    internal static void Require() => Require(AvifRuntime.Probe);

    internal static void Require(Func<AvifRuntimeStatus> probe)
    {
        var status = probe();
        if (!status.IsAvailable)
        {
            throw new InvalidOperationException(
                $"Native AVIF runtime is unavailable on '{status.RuntimeIdentifier}': {status.UnavailableReason}");
        }
    }
}
