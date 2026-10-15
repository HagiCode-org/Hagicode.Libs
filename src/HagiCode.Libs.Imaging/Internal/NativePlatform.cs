using System.Runtime.InteropServices;

namespace HagiCode.Libs.Imaging.Internal;

/// <summary>The runtime identifiers the package ships native libraries for, and the detected identifier of this process.</summary>
internal static class NativePlatform
{
    private static readonly string[] Supported = ["win-x64", "linux-x64", "osx-x64", "osx-arm64"];

    /// <summary>The supported runtime identifiers, in the order shown in diagnostics.</summary>
    internal static IReadOnlyList<string> SupportedRuntimeIdentifiers => Supported;

    /// <summary>The supported identifiers as a comma-separated list.</summary>
    internal static string SupportedList => string.Join(", ", Supported);

    /// <summary>The runtime identifier of the current process, for example <c>linux-x64</c> or <c>linux-musl-x64</c>.</summary>
    internal static string DetectRuntimeIdentifier() => RuntimeInformation.RuntimeIdentifier;

    /// <summary>True when the identifier is one of the four shipped platforms.</summary>
    internal static bool IsSupported(string runtimeIdentifier) =>
        Array.IndexOf(Supported, runtimeIdentifier) >= 0;
}
