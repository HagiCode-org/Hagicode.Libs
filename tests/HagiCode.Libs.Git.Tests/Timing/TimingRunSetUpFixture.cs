using NUnit.Framework;
using HagiCode.Libs.Git.Tests.Support;

// A [SetUpFixture] without a namespace applies to the whole assembly, so its OneTimeTearDown runs once after every git test.
[SetUpFixture]
public sealed class TimingRunSetUpFixture
{
    [OneTimeTearDown]
    public void WriteTimingFiles()
    {
        var directory = HagiCode.Libs.Git.Tests.Timing.TimingWriter.DirectoryFromEnvironment();
        if (directory is null)
        {
            return;
        }

        var records = HagiCode.Libs.Git.Tests.Timing.DurationRecorder.Shared.Snapshot();
        var statistics = HagiCode.Libs.Git.Tests.Timing.TimingStatistics.Summarize(records);
        var metadata = new HagiCode.Libs.Git.Tests.Timing.TimingMetadata(
            OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Platform: HagiCode.Libs.Git.Tests.Timing.TimingWriter.PlatformName(),
            Architecture: HagiCode.Libs.Git.Tests.Timing.TimingWriter.ArchitectureName(),
            Runtime: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            GitVersion: GitVersionProbe.Read(),
            FileSystem: HagiCode.Libs.Git.Tests.Timing.TimingWriter.FileSystemName(),
            UtcTimestamp: DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            WarmUpIterations: HagiCode.Libs.Git.Tests.Timing.ScenarioRunner.WarmUpIterations,
            MeasuredIterations: HagiCode.Libs.Git.Tests.Timing.ScenarioRunner.MeasuredIterations);

        HagiCode.Libs.Git.Tests.Timing.TimingWriter.Write(directory, metadata, records, statistics);
    }
}
