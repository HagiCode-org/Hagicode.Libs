using HagiCode.Libs.Imaging;
using NUnit.Framework.Interfaces;

namespace HagiCode.Libs.Imaging.Tests.Support;

/// <summary>
/// Marks a test that needs the native runtime. Such a test fails when the runtime is unavailable (see
/// <see cref="NativeRuntime.Require()"/>). A developer on an unsupported machine can set
/// <c>HAGICODE_IMAGING_SKIP_NATIVE=1</c> to ignore these tests locally. CI never sets it. The decision is taken at
/// discovery, like the former xUnit <c>Skip</c>, and applies to every case of a data-driven test.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class NativeAvifGateAttribute : Attribute, IApplyToTest
{
    internal const string SkipVariable = "HAGICODE_IMAGING_SKIP_NATIVE";

    public void ApplyToTest(NUnit.Framework.Internal.Test test)
    {
        if (Environment.GetEnvironmentVariable(SkipVariable) == "1")
        {
            test.RunState = RunState.Ignored;
            test.Properties.Set(
                NUnit.Framework.Internal.PropertyNames.SkipReason,
                $"Skipped locally by {SkipVariable}=1. CI does not set this variable.");
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
