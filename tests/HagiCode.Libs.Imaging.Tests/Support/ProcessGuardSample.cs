using System.Diagnostics;

namespace HagiCode.Libs.Imaging.Tests.Support;

/// <summary>
/// A deliberately process-spawning type. The guard scan must detect it, which proves the scan can fail. Never call it.
/// </summary>
internal static class ProcessGuardSample
{
    internal static ProcessStartInfo Describe() => new("ffmpeg");
}
