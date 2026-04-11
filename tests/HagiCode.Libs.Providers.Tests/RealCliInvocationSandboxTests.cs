using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class RealCliInvocationSandboxTests
{
    [Fact]
    public void DeleteDirectoryWithRetries_retries_io_failures_until_delete_succeeds()
    {
        using var tempDirectory = new TemporaryDirectory();
        var attempts = 0;
        var sleepCalls = 0;

        RealCliInvocationSandbox.DeleteDirectoryWithRetries(
            tempDirectory.Path,
            maxAttempts: 4,
            retryDelay: TimeSpan.Zero,
            deleteDirectory: path =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new IOException("Directory is still locked.");
                }

                Directory.Delete(path, recursive: true);
            },
            sleep: _ => sleepCalls++);

        attempts.ShouldBe(3);
        sleepCalls.ShouldBe(2);
        Directory.Exists(tempDirectory.Path).ShouldBeFalse();
    }

    [Fact]
    public void DeleteDirectoryWithRetries_retries_unauthorized_access_until_delete_succeeds()
    {
        using var tempDirectory = new TemporaryDirectory();
        var attempts = 0;
        var sleepCalls = 0;

        RealCliInvocationSandbox.DeleteDirectoryWithRetries(
            tempDirectory.Path,
            maxAttempts: 3,
            retryDelay: TimeSpan.Zero,
            deleteDirectory: path =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new UnauthorizedAccessException("keytar.node is still in use.");
                }

                Directory.Delete(path, recursive: true);
            },
            sleep: _ => sleepCalls++);

        attempts.ShouldBe(2);
        sleepCalls.ShouldBe(1);
        Directory.Exists(tempDirectory.Path).ShouldBeFalse();
    }

    [Fact]
    public void DeleteDirectoryWithRetries_throws_after_exhausting_retry_budget()
    {
        using var tempDirectory = new TemporaryDirectory();
        var attempts = 0;
        var sleepCalls = 0;

        var exception = Should.Throw<IOException>(() =>
            RealCliInvocationSandbox.DeleteDirectoryWithRetries(
                tempDirectory.Path,
                maxAttempts: 3,
                retryDelay: TimeSpan.Zero,
                deleteDirectory: _ =>
                {
                    attempts++;
                    throw new UnauthorizedAccessException("still locked");
                },
                sleep: _ => sleepCalls++));

        attempts.ShouldBe(3);
        sleepCalls.ShouldBe(2);
        exception.Message.ShouldContain("Failed to delete sandbox directory");
        exception.InnerException.ShouldBeOfType<UnauthorizedAccessException>();
        Directory.Exists(tempDirectory.Path).ShouldBeTrue();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hagicode-libs-providers-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "probe.txt"), "sandbox");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}
