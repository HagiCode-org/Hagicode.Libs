using System.Text;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Copilot requests rewrite process-wide environment variables (including PATH), so classes that
// spawn child processes must not run alongside them.
[NonParallelizable]
[Category("Integration")]
public sealed class OpenCodeProcessLauncherTests
{
    private const string LauncherPassword = "launcher-test-secret";

    [TestCase("server listening on http://127.0.0.1:4096", "http://127.0.0.1:4096/")]
    [TestCase("opencode server listening on http://127.0.0.1:4097", "http://127.0.0.1:4097/")]
    [TestCase("  Server Listening on http://localhost:4098  ", "http://localhost:4098/")]
    public void TryParseListeningUrl_accepts_the_2_0_and_1_x_startup_lines(string line, string expectedUri)
    {
        var parsed = OpenCodeProcessLauncher.TryParseListeningUrl(line, out var baseUri);

        parsed.ShouldBeTrue();
        baseUri!.ToString().ShouldBe(expectedUri);
    }

    [Test]
    public void TryParseListeningUrl_ignores_lines_that_are_not_the_listening_line()
    {
        OpenCodeProcessLauncher.TryParseListeningUrl("server password abc123", out var baseUri).ShouldBeFalse();
        baseUri.ShouldBeNull();
        OpenCodeProcessLauncher.TryParseListeningUrl("starting database migration", out _).ShouldBeFalse();
    }

    [Test]
    public void TryParsePassword_captures_the_password_printed_at_startup()
    {
        OpenCodeProcessLauncher.TryParsePassword("server password abc_DEF-123", out var password).ShouldBeTrue();
        password.ShouldBe("abc_DEF-123");

        OpenCodeProcessLauncher.TryParsePassword("server listening on http://127.0.0.1:4096", out _).ShouldBeFalse();
    }

    [Test]
    public void Redactor_removes_the_password_value_and_the_password_line_even_without_a_known_secret()
    {
        var redacted = OpenCodeSecretRedactor.Redact($"server password {LauncherPassword}{Environment.NewLine}echo {LauncherPassword}", LauncherPassword);
        redacted.ShouldNotBeNull();
        redacted.ShouldNotContain(LauncherPassword);
        redacted.ShouldContain("server password <redacted>");

        var unknownSecret = OpenCodeSecretRedactor.Redact("server password other-secret-value", secret: null);
        unknownSecret.ShouldBe("server password <redacted>");
    }

    [Test]
    public async Task StartAsync_captures_the_printed_password_and_keeps_it_out_of_captured_output()
    {
        var executablePath = CreateRuntimeShim("http://127.0.0.1:49999", LauncherPassword, keepRunning: true);
        var launcher = new OpenCodeProcessLauncher(new CliExecutableResolver());

        try
        {
            await using var handle = await launcher.StartAsync(
                executablePath,
                new OpenCodeStandaloneServerOptions { StartupTimeout = TimeSpan.FromSeconds(15) },
                new Dictionary<string, string?>(StringComparer.Ordinal),
                CancellationToken.None);

            handle.BaseUri.ToString().ShouldBe("http://127.0.0.1:49999/");
            handle.ServerPassword.ShouldBe(LauncherPassword);
            handle.CapturedOutput.ShouldContain("server listening on http://127.0.0.1:49999");
            handle.CapturedOutput.ShouldContain("server password <redacted>");
            handle.CapturedOutput.ShouldNotContain(LauncherPassword);
        }
        finally
        {
            File.Delete(executablePath);
        }
    }

    [Test]
    public async Task StartAsync_without_a_printed_password_still_succeeds()
    {
        var executablePath = CreateRuntimeShim("http://127.0.0.1:49998", password: null, keepRunning: true);
        var launcher = new OpenCodeProcessLauncher(new CliExecutableResolver());

        try
        {
            await using var handle = await launcher.StartAsync(
                executablePath,
                new OpenCodeStandaloneServerOptions { StartupTimeout = TimeSpan.FromSeconds(15) },
                new Dictionary<string, string?>(StringComparer.Ordinal),
                CancellationToken.None);

            handle.BaseUri.ToString().ShouldBe("http://127.0.0.1:49998/");
            handle.ServerPassword.ShouldBeNull();
        }
        finally
        {
            File.Delete(executablePath);
        }
    }

    [Test]
    public async Task StartAsync_timeout_diagnostic_redacts_a_password_that_was_printed()
    {
        var executablePath = CreateRuntimeShim(listeningUrl: null, LauncherPassword, keepRunning: true);
        var launcher = new OpenCodeProcessLauncher(new CliExecutableResolver());

        try
        {
            var exception = await Should.ThrowAsync<TimeoutException>(async () =>
            {
                await using var handle = await launcher.StartAsync(
                    executablePath,
                    new OpenCodeStandaloneServerOptions { StartupTimeout = TimeSpan.FromSeconds(2) },
                    new Dictionary<string, string?>(StringComparer.Ordinal),
                    CancellationToken.None);
            });

            exception.Message.ShouldContain("Timed out waiting for OpenCode runtime startup");
            exception.Message.ShouldNotContain(LauncherPassword);
            exception.Message.ShouldContain("server password <redacted>");
        }
        finally
        {
            File.Delete(executablePath);
        }
    }

    /// <summary>
    /// Writes a shell runtime that prints the given startup lines and keeps running, like <c>opencode serve</c>.
    /// </summary>
    private static string CreateRuntimeShim(string? listeningUrl, string? password, bool keepRunning)
    {
        var path = Path.Combine(Path.GetTempPath(), $"opencode-launcher-shim-{Guid.NewGuid():N}.sh");
        var script = new StringBuilder("#!/usr/bin/env bash\nset -euo pipefail\n");
        if (listeningUrl is not null)
        {
            script.Append($"printf 'server listening on {listeningUrl}\\n'\n");
        }

        if (password is not null)
        {
            script.Append($"printf 'server password {password}\\n'\n");
        }

        if (keepRunning)
        {
            script.Append("while true; do sleep 1; done\n");
        }

        File.WriteAllText(path, script.ToString(), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
